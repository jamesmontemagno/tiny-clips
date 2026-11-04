use crate::{
    AudioOptions, MediaResult, Region,
    engine::{self, VideoInput},
    storage::Take,
};
use gst::prelude::*;
use std::{
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    time::{Duration, Instant},
};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Phase {
    #[default]
    Idle,
    Starting,
    Recording,
    Paused,
    Finalizing,
    Failed,
}
#[derive(Clone, Debug, Default)]
pub struct Status {
    pub phase: Phase,
    pub error: Option<String>,
    pub recovery_dir: Option<PathBuf>,
}
impl Status {
    pub fn active(&self) -> bool {
        !matches!(self.phase, Phase::Idle | Phase::Failed)
    }
}

type Reply<T> = async_channel::Sender<MediaResult<T>>;
enum Command {
    Pause(Reply<()>),
    Resume(Reply<()>),
    Stop(Instant, Reply<PathBuf>),
}
#[derive(Clone, Default)]
pub(crate) struct Recorder {
    state: Arc<Mutex<Status>>,
    channel: Arc<Mutex<Option<mpsc::Sender<Command>>>>,
}
impl Recorder {
    pub fn status(&self) -> Status {
        self.state.lock().unwrap().clone()
    }
    pub async fn start(
        &self,
        input: VideoInput,
        valid: Arc<AtomicBool>,
        dir: &Path,
        audio: AudioOptions,
        segment_seconds: u64,
        region: Option<Region>,
    ) -> MediaResult<()> {
        let (tx, rx) = mpsc::channel();
        let (started, result) = async_channel::bounded(1);
        {
            let mut state = self.state.lock().unwrap();
            if state.active() {
                return Err("A recording is already active".into());
            }
            *state = Status {
                phase: Phase::Starting,
                ..Default::default()
            };
            *self.channel.lock().unwrap() = Some(tx);
        }
        let state = Arc::clone(&self.state);
        let dir = dir.to_owned();
        let run = move || {
            let mut stop = None;
            let recorded = (|| {
                gst::init().map_err(|e| e.to_string())?;
                if !valid.load(Ordering::Acquire) {
                    return Err("ScreenCast source is no longer authorized".into());
                }
                let take = Take::new(&dir)?;
                let mut disk_budget = crate::disk_budget::DiskBudget::default();
                state.lock().unwrap().recovery_dir = Some(take.dir.clone());
                let capture = engine::capture(input, audio, &take, segment_seconds, region)?;
                let play_at = Instant::now();
                let mut ready_at = play_at;
                let mut paused_at = None;
                let mut paused = Duration::ZERO;
                let mut end_at = None;
                let (mut count, mut split_sum) = (0u32, 0u64);
                let mut opened = std::collections::HashMap::<String, u64>::new();
                capture.pipeline.play()?;
                let bus = capture.pipeline.0.bus().unwrap();
                let startup_deadline = Instant::now() + Duration::from_secs(15);
                let mut stopping = None;
                let eos_failed = Arc::new(AtomicBool::new(false));
                let mut interrupt = None;
                let mut phase = Phase::Starting;
                let mut route_check = Instant::now();
                loop {
                    capture.check_bridge()?;
                    let cmd = rx.try_recv();
                    // A portal revocation (even while paused) ends the live take. Do not
                    // silently turn a failed/interrupted take into a successful output.
                    if !valid.load(Ordering::Acquire) && interrupt.is_none() {
                        interrupt = Some("ScreenCast source was revoked; closed segments retained");
                    }
                    let disappearing =
                        interrupt.is_some() || matches!(cmd, Err(mpsc::TryRecvError::Disconnected));
                    let start_stop = matches!(cmd, Ok(Command::Stop(..))) || disappearing;
                    match cmd {
                        Ok(Command::Stop(at, reply)) if stop.is_none() => {
                            stop = Some(reply);
                            end_at = Some(at);
                        }
                        Ok(Command::Stop(_, reply)) => {
                            let _ = reply.try_send(Err("Recording is already finalizing".into()));
                        }
                        Ok(Command::Pause(reply)) | Ok(Command::Resume(reply))
                            if stopping.is_some() =>
                        {
                            let _ = reply.try_send(Err("Recording is finalizing".into()));
                        }
                        Ok(Command::Pause(reply)) => {
                            if phase != Phase::Recording {
                                let _ = reply.try_send(Err("No running recording to pause".into()));
                            } else {
                                change_state(&capture.pipeline.0, gst::State::Paused)?;
                                paused_at = Some(Instant::now());
                                phase = Phase::Paused;
                                state.lock().unwrap().phase = phase;
                                let _ = reply.try_send(Ok(()));
                            }
                        }
                        Ok(Command::Resume(reply)) => {
                            if phase != Phase::Paused {
                                let _ = reply.try_send(Err("Recording is not paused".into()));
                            } else {
                                if let Some(at) = paused_at.take() {
                                    paused += Instant::now().saturating_duration_since(at);
                                }
                                change_state(&capture.pipeline.0, gst::State::Playing)?;
                                phase = Phase::Recording;
                                state.lock().unwrap().phase = phase;
                                let _ = reply.try_send(Ok(()));
                            }
                        }
                        Err(_) => (),
                    }
                    if start_stop && stopping.is_none() {
                        let at = *end_at.get_or_insert_with(Instant::now);
                        if let Some(p) = paused_at.take() {
                            paused += at.saturating_duration_since(p);
                        }
                        if phase == Phase::Paused {
                            capture.pipeline.play()?;
                        }
                        phase = Phase::Finalizing;
                        state.lock().unwrap().phase = phase;
                        capture.drain(eos_failed.clone());
                        stopping = Some(Instant::now() + Duration::from_secs(15));
                    }
                    if eos_failed.load(Ordering::Acquire)
                        || stopping.is_some_and(|until| Instant::now() >= until)
                    {
                        return Err("Recording could not drain; closed segments retained".into());
                    }
                    if let Some(msg) = bus.timed_pop(gst::ClockTime::from_mseconds(30)) {
                        match msg.view() {
                            gst::MessageView::Eos(_) => {
                                if stopping.is_none() {
                                    valid.store(false, Ordering::Release);
                                    interrupt = Some(
                                        "Capture stream ended unexpectedly; closed segments retained",
                                    );
                                }
                                break;
                            }
                            gst::MessageView::Error(e) => {
                                return Err(engine::source_error(e, Some(&valid)));
                            }
                            gst::MessageView::Element(e) => {
                                if let Some(s) = e
                                    .structure()
                                    .filter(|s| s.name() == "splitmuxsink-fragment-opened")
                                {
                                    opened.insert(
                                        s.get::<String>("location").map_err(|e| e.to_string())?,
                                        s.get::<u64>("running-time").map_err(|e| e.to_string())?,
                                    );
                                }
                                if let Some(s) = e
                                    .structure()
                                    .filter(|s| s.name() == "splitmuxsink-fragment-closed")
                                {
                                    let file =
                                        s.get::<String>("location").map_err(|e| e.to_string())?;
                                    let size = std::fs::symlink_metadata(&file)
                                        .map_err(|e| e.to_string())?
                                        .len();
                                    let origin = opened
                                        .remove(&file)
                                        .ok_or("Missing original fragment clock")?;
                                    take.closed(Path::new(&file), origin)?;
                                    disk_budget.note_fragment(size);
                                    if stopping.is_none() && !disk_budget.can_continue(&take.dir)? {
                                        interrupt.get_or_insert("Low disk space; capture stopped to preserve closed segments. Free space and reopen Tiny Clips to recover.");
                                    }
                                    count += 1;
                                    split_sum += s
                                        .get::<u64>("running-time")
                                        .unwrap_or(0)
                                        .saturating_sub(origin);
                                }
                            }
                            _ => (),
                        }
                    }
                    if phase == Phase::Recording && route_check.elapsed() >= Duration::from_secs(1)
                    {
                        capture.check_audio_routes()?;
                        route_check = Instant::now();
                    }
                    if phase == Phase::Starting {
                        if capture.ready()? {
                            ready_at = Instant::now();
                            phase = Phase::Recording;
                            state.lock().unwrap().phase = phase;
                            if started.try_send(Ok(())).is_err() {
                                interrupt = Some("Recording request was abandoned");
                            }
                        } else if Instant::now() > startup_deadline {
                            return Err(
                                "No usable encoded frames from the selected source(s)".into()
                            );
                        }
                    }
                }
                let end = end_at.unwrap_or_else(Instant::now);
                let active = end
                    .saturating_duration_since(ready_at)
                    .saturating_sub(paused);
                capture.timing.report(
                    active,
                    end.saturating_duration_since(play_at),
                    paused,
                    capture
                        .pipeline
                        .0
                        .current_running_time()
                        .map(|t| t.nseconds())
                        .unwrap_or(0),
                    count,
                    split_sum,
                );
                let coverage = capture.timing.check(active);
                drop(capture);
                if let Some(reason) = interrupt {
                    return Err(reason.into());
                }
                coverage?;
                engine::finalize(&take, Some(active))
            })();
            {
                let mut s = state.lock().unwrap();
                match &recorded {
                    Ok(_) => *s = Status::default(),
                    Err(e) => {
                        s.phase = Phase::Failed;
                        s.error = Some(e.clone());
                        if s.recovery_dir.as_ref().is_some_and(|p| !p.exists()) {
                            s.recovery_dir = None;
                        }
                    }
                }
            }
            let _ = started.try_send(recorded.as_ref().map(|_| ()).map_err(Clone::clone));
            if let Some(reply) = stop {
                let _ = reply.try_send(recorded);
            }
        };
        let spawn = std::thread::Builder::new()
            .name("tinyclips-recorder".into())
            .spawn(run);
        if let Err(e) = spawn {
            let mut s = self.state.lock().unwrap();
            s.phase = Phase::Failed;
            s.error = Some(e.to_string());
            return Err(e.to_string());
        }
        result
            .recv()
            .await
            .map_err(|_| "Recorder worker stopped".to_owned())?
    }
    async fn command<T>(&self, make: impl FnOnce(Reply<T>) -> Command) -> MediaResult<T> {
        let (tx, rx) = async_channel::bounded(1);
        let status = self.status();
        if !status.active() {
            return Err(status.error.unwrap_or_else(|| "No active recording".into()));
        }
        self.channel
            .lock()
            .unwrap()
            .as_ref()
            .ok_or("No recorder worker")?
            .send(make(tx))
            .map_err(|_| "Recorder worker stopped")?;
        rx.recv().await.map_err(|_| {
            self.status()
                .error
                .unwrap_or_else(|| "Recorder worker stopped".into())
        })?
    }
    pub async fn pause(&self) -> MediaResult<()> {
        self.command(Command::Pause).await
    }
    pub async fn resume(&self) -> MediaResult<()> {
        self.command(Command::Resume).await
    }
    pub async fn stop(&self) -> MediaResult<PathBuf> {
        self.command(|reply| Command::Stop(Instant::now(), reply))
            .await
    }
}
fn change_state(p: &gst::Pipeline, state: gst::State) -> MediaResult<()> {
    p.set_state(state).map_err(|e| e.to_string())?;
    let (result, current, _) = p.state(gst::ClockTime::from_seconds(4));
    result.map_err(|e| e.to_string())?;
    if current != state {
        return Err(format!("Pipeline did not reach {state:?}"));
    }
    Ok(())
}
