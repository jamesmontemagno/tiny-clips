//! Only mix PCM that was actually captured by *every* selected source. The
//! live audiomixer clock alone can synthesize silent frames on missed deadlines.
//! Pair sample ranges first, then feed non-live, already-available appsrc inputs
//! to the native mixer; no clock deadline is allowed to substitute another input.
use crate::{MediaResult, engine::element};
use gst::prelude::*;
use std::{
    collections::VecDeque,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
    },
};
const RATE: u64 = 48_000;
const FRAME: usize = 4;
const NONE: u64 = u64::MAX;
fn frame(ns: u64) -> u64 {
    ((ns as u128 * RATE as u128 + 500_000_000) / 1_000_000_000) as u64
}
fn time(f: u64) -> gst::ClockTime {
    gst::ClockTime::from_nseconds((f as u128 * 1_000_000_000 / RATE as u128) as u64)
}
#[derive(Clone)]
pub(crate) struct Start {
    audio: Arc<AtomicU64>,
    video: Arc<AtomicU64>,
}
impl Default for Start {
    fn default() -> Self {
        Self {
            audio: Arc::new(AtomicU64::new(NONE)),
            video: Arc::new(AtomicU64::new(NONE)),
        }
    }
}
impl Start {
    pub fn gate_video(&self, element: &gst::Element) -> MediaResult<()> {
        let gate = self.clone();
        let segment: Mutex<Option<gst::FormattedSegment<gst::ClockTime>>> = Mutex::new(None);
        element
            .static_pad("src")
            .ok_or("Missing video gate")?
            .add_probe(
                gst::PadProbeType::EVENT_DOWNSTREAM | gst::PadProbeType::BUFFER,
                move |_, info| {
                    if gate.video.load(Ordering::Acquire) != NONE {
                        return gst::PadProbeReturn::Remove;
                    }
                    if let Some(gst::EventView::Segment(e)) = info.event().map(|e| e.view()) {
                        *segment.lock().unwrap() =
                            e.segment().downcast_ref::<gst::ClockTime>().cloned();
                    }
                    if let Some(b) = info.buffer() {
                        let t = b
                            .pts()
                            .and_then(|v| segment.lock().unwrap().as_ref()?.to_running_time(v));
                        let Some(t) = t else {
                            return gst::PadProbeReturn::Drop;
                        };
                        let start = gate.audio.load(Ordering::Acquire);
                        if start == NONE || t.nseconds() < start {
                            return gst::PadProbeReturn::Drop;
                        }
                        gate.video.store(t.nseconds(), Ordering::Release);
                        return gst::PadProbeReturn::Remove;
                    }
                    gst::PadProbeReturn::Ok
                },
            )
            .ok_or("Cannot install initial A/V overlap gate")?;
        Ok(())
    }
}
struct Chunk {
    buf: gst::Buffer,
    start: u64,
    from: u64,
    end: u64,
}
struct Channel {
    src: gst::Element,
    chunks: VecDeque<Chunk>,
    last: Option<u64>,
    queued_bytes: usize,
    ended: bool,
}
struct Pair {
    channels: Vec<Channel>,
    gate: Start,
    cursor: Option<u64>,
    eos: bool,
    error: Option<String>,
}
#[derive(Clone)]
pub(crate) struct Bridge(Arc<Mutex<Pair>>);
impl Bridge {
    pub fn new(
        p: &gst::Pipeline,
        mixer: &gst::Element,
        n: usize,
        caps: &gst::Caps,
        gate: Start,
        timing: &crate::timing::Timing,
    ) -> MediaResult<Self> {
        let mut channels = Vec::new();
        for i in 0..n {
            let src = element("appsrc")?;
            src.set_property("caps", caps);
            src.set_property("format", gst::Format::Time);
            src.set_property("max-bytes", RATE * FRAME as u64);
            p.add(&src).map_err(|e| e.to_string())?;
            let pad = mixer
                .request_pad_simple("sink_%u")
                .ok_or("Missing mixer input")?;
            if n == 2 {
                pad.set_property("volume", 0.5f64);
            }
            src.static_pad("src")
                .unwrap()
                .link(&pad)
                .map_err(|e| e.to_string())?;
            timing.attach(
                if i == 0 {
                    "paired_audio_0"
                } else {
                    "paired_audio_1"
                },
                &src,
            )?;
            channels.push(Channel {
                src,
                chunks: VecDeque::new(),
                last: None,
                queued_bytes: 0,
                ended: false,
            });
        }
        Ok(Self(Arc::new(Mutex::new(Pair {
            channels,
            gate,
            cursor: None,
            eos: false,
            error: None,
        }))))
    }
    pub fn sink(&self, i: usize) -> MediaResult<gst::Element> {
        let sink = element("appsink")?;
        sink.set_property("emit-signals", true);
        sink.set_property("sync", false);
        sink.set_property("async", false);
        sink.set_property("enable-last-sample", false);
        sink.set_property("max-buffers", 2u32);
        let state = self.0.clone();
        sink.connect("new-sample", false, move |args| {
            let app = args[0].get::<gst::Element>().unwrap();
            let sample = app.emit_by_name::<Option<gst::Sample>>("pull-sample", &[]);
            let mut s = state.lock().unwrap();
            let r = sample
                .ok_or_else(|| "Missing acquired PCM".to_owned())
                .and_then(|v| s.receive(i, &v));
            if let Err(e) = r {
                s.error.get_or_insert(e);
            }
            Some(gst::FlowReturn::Ok.to_value())
        });
        let state = self.0.clone();
        sink.connect("eos", false, move |_| {
            let mut s = state.lock().unwrap();
            s.channels[i].ended = true;
            if let Err(e) = s.drain() {
                s.error.get_or_insert(e);
            }
            None
        });
        Ok(sink)
    }
    pub fn check(&self) -> MediaResult<()> {
        match &self.0.lock().unwrap().error {
            Some(e) => Err(e.clone()),
            None => Ok(()),
        }
    }
}
impl Pair {
    fn receive(&mut self, i: usize, sample: &gst::Sample) -> MediaResult<()> {
        if self.eos || self.error.is_some() {
            return Ok(());
        }
        let buf = sample.buffer().ok_or("Empty acquired PCM")?;
        if buf.flags().contains(gst::BufferFlags::GAP) {
            return Ok(());
        }
        let t = buf
            .pts()
            .and_then(|p| {
                sample
                    .segment()?
                    .downcast_ref::<gst::ClockTime>()?
                    .to_running_time(p)
            })
            .ok_or("Captured PCM missing monotonic timestamp")?;
        let mut start = frame(t.nseconds());
        let c = &mut self.channels[i];
        if let Some(last) = c.last {
            if start.abs_diff(last) <= 2 {
                start = last;
            } else if start < last && last - start > RATE / 10 {
                return Err("Audio capture clock regressed; retaining segments".into());
            }
        }
        if buf.size() % FRAME != 0 {
            return Err("Invalid captured PCM frame length".into());
        }
        let end = start + buf.size() as u64 / FRAME as u64;
        if end == start {
            return Ok(());
        }
        c.last = Some(end);
        c.queued_bytes = c.queued_bytes.saturating_add(buf.size());
        if c.queued_bytes > RATE as usize * FRAME * 4 || c.chunks.len() >= 512 {
            return Err(
                "Unpaired captured PCM reached its bounded memory limit; retaining segments".into(),
            );
        }
        c.chunks.push_back(Chunk {
            buf: buf.to_owned(),
            start,
            from: start,
            end,
        });
        if c.chunks
            .front()
            .is_some_and(|v| end.saturating_sub(v.from) > 4 * RATE)
        {
            return Err("Selected audio sources did not supply overlapping PCM within the bounded capture queue; retaining segments".into());
        }
        self.drain()
    }
    fn drain(&mut self) -> MediaResult<()> {
        if self.eos {
            return Ok(());
        }
        while self.channels.iter().all(|c| !c.chunks.is_empty()) {
            let mut start = self
                .channels
                .iter()
                .map(|c| c.chunks.front().unwrap().from)
                .max()
                .unwrap();
            let end = self
                .channels
                .iter()
                .map(|c| c.chunks.front().unwrap().end)
                .min()
                .unwrap();
            if start < end && self.cursor.is_none() {
                let _ = self.gate.audio.compare_exchange(
                    NONE,
                    time(start).nseconds(),
                    Ordering::AcqRel,
                    Ordering::Acquire,
                );
                let video = self.gate.video.load(Ordering::Acquire);
                if video == NONE {
                    break;
                }
                start = start.max(frame(video));
            }
            if let Some(cursor) = self.cursor {
                start = start.max(cursor);
            }
            if start < end {
                if self
                    .cursor
                    .is_some_and(|c| start.saturating_sub(c) > RATE / 5)
                {
                    return Err("More than 200 ms of selected real audio is missing; retaining closed segments".into());
                }
                for c in &self.channels {
                    if c.src.property::<u64>("current-level-bytes") > RATE * FRAME as u64 * 2 {
                        return Err(
                            "Audio encoder is more than two seconds behind; retaining segments"
                                .into(),
                        );
                    }
                    let front = c.chunks.front().unwrap();
                    let skip = (start - front.start) as usize * FRAME;
                    let n = (end - start) as usize * FRAME;
                    let mut buffer = front
                        .buf
                        .copy_region(gst::BufferCopyFlags::MEMORY, skip..skip + n)
                        .map_err(|e| e.to_string())?;
                    let b = buffer.make_mut();
                    b.set_pts(time(start));
                    b.set_dts(None);
                    b.set_duration(time(end) - time(start));
                    b.set_offset(start);
                    b.set_offset_end(end);
                    c.src
                        .emit_by_name::<gst::FlowReturn>("push-buffer", &[&buffer])
                        .into_result()
                        .map_err(|e| e.to_string())?;
                }
                self.cursor = Some(end);
            }
            for c in &mut self.channels {
                let f = c.chunks.front_mut().unwrap();
                f.from = f.from.max(end);
                if f.from >= f.end {
                    c.queued_bytes -= f.buf.size();
                    c.chunks.pop_front();
                }
            }
        }
        if self.channels.iter().any(|c| c.ended && c.chunks.is_empty()) {
            for c in &self.channels {
                c.src
                    .emit_by_name::<gst::FlowReturn>("end-of-stream", &[])
                    .into_result()
                    .map_err(|e| e.to_string())?;
            }
            self.eos = true;
        }
        Ok(())
    }
}
