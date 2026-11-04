use crate::{
    AudioOptions, MediaResult, Region,
    storage::{self, Scratch, Take},
};
use gst::prelude::*;
#[cfg(any(test, feature = "portal"))]
use std::os::fd::AsRawFd;
#[cfg(feature = "portal")]
use std::sync::Mutex;
use std::{
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, Instant},
};

pub(crate) enum VideoInput {
    #[cfg(feature = "portal")]
    PipeWire {
        fd: std::os::fd::OwnedFd,
        node: u32,
        dimensions: Arc<Mutex<Option<(u32, u32)>>>,
    },
    Synthetic,
    SyntheticSlow(u32),
}
impl VideoInput {
    fn source(&self) -> MediaResult<gst::Element> {
        match self {
            #[cfg(feature = "portal")]
            Self::PipeWire {
                fd,
                node,
                dimensions,
            } => {
                let e = element("pipewiresrc")?;
                // path takes the portal node ID; target-object takes a *serial*, not a node ID.
                // pipewiresrc duplicates the fd internally. Keep its owner until NULL.
                e.set_property("fd", fd.as_raw_fd());
                e.set_property("path", node.to_string());
                e.set_property("do-timestamp", true);
                if e.has_property("dont-reconnect") {
                    e.set_property("dont-reconnect", true);
                }
                if e.has_property("on-disconnect") {
                    e.set_property_from_str("on-disconnect", "error");
                }
                if e.has_property("keepalive-time") {
                    e.set_property_from_str("keepalive-time", "1000");
                }
                let dimensions = Arc::clone(dimensions);
                e.static_pad("src")
                    .ok_or("PipeWire source pad unavailable")?
                    .add_probe(gst::PadProbeType::EVENT_DOWNSTREAM, move |_, info| {
                        if let Some(gst::EventView::Caps(c)) = info.event().map(|e| e.view()) {
                            let size = c.caps().structure(0).and_then(|s| {
                                Some((
                                    u32::try_from(s.get::<i32>("width").ok()?).ok()?,
                                    u32::try_from(s.get::<i32>("height").ok()?).ok()?,
                                ))
                            });
                            *dimensions.lock().unwrap() = size;
                        }
                        gst::PadProbeReturn::Ok
                    });
                Ok(e)
            }
            Self::Synthetic | Self::SyntheticSlow(_) => {
                let e = element("videotestsrc")?;
                e.set_property("is-live", true);
                e.set_property_from_str("pattern", "ball");
                Ok(e)
            }
        }
    }
}

pub(crate) fn element(name: &str) -> MediaResult<gst::Element> {
    gst::ElementFactory::make(name)
        .build()
        .map_err(|e| format!("Cannot load GStreamer element {name}: {e}"))
}
fn caps(c: gst::Caps) -> MediaResult<gst::Element> {
    let e = element("capsfilter")?;
    e.set_property("caps", c);
    Ok(e)
}
fn queue() -> MediaResult<gst::Element> {
    let e = element("queue")?;
    e.set_property("max-size-time", 2_000_000_000u64);
    e.set_property("max-size-bytes", 32 * 1024 * 1024u32);
    e.set_property("max-size-buffers", 60u32);
    Ok(e)
}
fn chain(p: &gst::Pipeline, elements: &[&gst::Element]) -> MediaResult<()> {
    p.add_many(elements.iter().copied())
        .map_err(|e| e.to_string())?;
    gst::Element::link_many(elements.iter().copied()).map_err(|e| e.to_string())
}
fn to_request(src: &gst::Element, dest: &gst::Element, template: &str) -> MediaResult<()> {
    let target = dest
        .request_pad_simple(template)
        .ok_or_else(|| format!("Missing muxer pad {template}"))?;
    src.static_pad("src")
        .ok_or("Missing source pad")?
        .link(&target)
        .map(|_| ())
        .map_err(|e| e.to_string())
}

pub(crate) struct Running(pub gst::Pipeline);
impl Drop for Running {
    fn drop(&mut self) {
        let _ = self.0.set_state(gst::State::Null);
    }
}
impl Running {
    pub fn play(&self) -> MediaResult<()> {
        self.0
            .set_state(gst::State::Playing)
            .map(|_| ())
            .map_err(|e| format!("Cannot start media pipeline: {e}"))
    }
}
pub(crate) fn source_error(msg: &gst::message::Error, valid: Option<&AtomicBool>) -> String {
    let pipewire = msg
        .src()
        .and_then(|s| s.downcast_ref::<gst::Element>())
        .and_then(|e| e.factory())
        .is_some_and(|f| f.name() == "pipewiresrc");
    if pipewire && msg.error().is::<gst::ResourceError>() {
        if let Some(valid) = valid {
            valid.store(false, Ordering::Release);
        }
    }
    message_error(msg)
}
pub(crate) fn message_error(msg: &gst::message::Error) -> String {
    format!(
        "{}: {}",
        msg.src()
            .map(|s| s.name().to_string())
            .unwrap_or_else(|| "GStreamer".into()),
        msg.error()
    )
}
fn wait_eos(bus: &gst::Bus, deadline: Instant, valid: Option<&AtomicBool>) -> MediaResult<()> {
    loop {
        if valid.is_some_and(|v| !v.load(Ordering::Acquire)) {
            return Err("ScreenCast source was revoked".into());
        }
        if Instant::now() >= deadline {
            return Err("Media pipeline timed out".into());
        }
        if let Some(msg) = bus.timed_pop(gst::ClockTime::from_mseconds(100)) {
            match msg.view() {
                gst::MessageView::Eos(_) => return Ok(()),
                gst::MessageView::Error(e) => return Err(source_error(e, valid)),
                _ => (),
            }
        }
    }
}
fn video_encoder(name: &str) -> MediaResult<gst::Element> {
    let e = element(name)?;
    match name {
        "openh264enc" => {
            e.set_property("bitrate", 6_000_000u32);
            e.set_property("gop-size", 60u32);
        }
        "x264enc" => {
            e.set_property_from_str("tune", "zerolatency");
            e.set_property_from_str("speed-preset", "veryfast");
            e.set_property("bitrate", 6000u32);
            e.set_property("key-int-max", 60u32);
        }
        _ => (),
    }
    Ok(e)
}
fn working_encoder(video: bool) -> MediaResult<&'static str> {
    let names: &[&'static str] = if video {
        &["openh264enc", "x264enc", "avenc_h264", "avenc_libx264"]
    } else {
        &["fdkaacenc", "voaacenc", "avenc_aac", "faac"]
    };
    let mut failures = Vec::new();
    for &name in names {
        let probe = || -> MediaResult<()> {
            let enc = if video {
                video_encoder(name)?
            } else {
                element(name)?
            };
            let p = Running(gst::Pipeline::new());
            let src = element(if video {
                "videotestsrc"
            } else {
                "audiotestsrc"
            })?;
            src.set_property("num-buffers", 8i32);
            let conv = element(if video {
                "videoconvert"
            } else {
                "audioconvert"
            })?;
            let parser = element(if video { "h264parse" } else { "aacparse" })?;
            let sink = element("fakesink")?;
            let received = Arc::new(AtomicBool::new(false));
            mark_buffers(&sink, "sink", &received)?;
            let raw = caps(if video {
                gst::Caps::builder("video/x-raw")
                    .field("format", "I420")
                    .field("width", 160)
                    .field("height", 120)
                    .build()
            } else {
                gst::Caps::builder("audio/x-raw")
                    .field("rate", 48000)
                    .field("channels", 2)
                    .build()
            })?;
            chain(&p.0, &[&src, &conv, &raw, &enc, &parser, &sink])?;
            p.play()?;
            wait_eos(
                &p.0.bus().unwrap(),
                Instant::now() + Duration::from_secs(5),
                None,
            )?;
            received
                .load(Ordering::Acquire)
                .then_some(())
                .ok_or("Encoder produced no data".into())
        };
        match probe() {
            Ok(()) => return Ok(name),
            Err(e) => failures.push(format!("{name}: {e}")),
        }
    }
    Err(format!(
        "No working software {} encoder. {}",
        if video { "H.264" } else { "AAC" },
        failures.join("; ")
    ))
}
fn mark_buffers(e: &gst::Element, pad: &str, flag: &Arc<AtomicBool>) -> MediaResult<()> {
    let flag = Arc::clone(flag);
    e.static_pad(pad)
        .ok_or("Missing codec pad")?
        .add_probe(gst::PadProbeType::BUFFER, move |_, _| {
            flag.store(true, Ordering::Release);
            gst::PadProbeReturn::Remove
        })
        .ok_or("Cannot observe encoder output")?;
    Ok(())
}

pub(crate) struct Capture {
    pub pipeline: Running,
    pub timing: crate::timing::Timing,
    acquisition: Vec<gst::Element>,
    bridge: Option<crate::pcm::Bridge>,
    _input: VideoInput,
    first_video: Arc<AtomicBool>,
    first_audio: Arc<AtomicBool>,
    first_inputs: Vec<Arc<AtomicBool>>,
    mic: Option<gst::Element>,
    monitor: Option<gst::Element>,
}
impl Capture {
    pub fn drain(&self, failed: Arc<AtomicBool>) {
        for source in &self.acquisition {
            let f = failed.clone();
            source.call_async(move |s| {
                if !s.send_event(gst::event::Eos::new()) {
                    f.store(true, Ordering::Release);
                }
            });
        }
    }
    pub fn check_bridge(&self) -> MediaResult<()> {
        if let Some(b) = &self.bridge {
            b.check()?;
        }
        Ok(())
    }
    pub fn ready(&self) -> MediaResult<bool> {
        if !self.first_video.load(Ordering::Acquire)
            || !self.first_audio.load(Ordering::Acquire)
            || !self.first_inputs.iter().all(|v| v.load(Ordering::Acquire))
        {
            return Ok(false);
        }
        self.check_audio_routes()?;
        Ok(true)
    }
    pub fn check_audio_routes(&self) -> MediaResult<()> {
        for src in [&self.mic, &self.monitor].into_iter().flatten() {
            let expected = src
                .property::<Option<String>>("device")
                .ok_or("Unnamed Pulse source")?;
            if src.property::<Option<String>>("current-device").as_ref() != Some(&expected) {
                return Err(
                    "PulseAudio changed the selected source; refusing to record a substitute"
                        .into(),
                );
            }
        }
        Ok(())
    }
}

#[derive(Default)]
struct AudioRoutes {
    microphone: Option<String>,
    monitor: Option<String>,
}
fn pulse_routes(options: AudioOptions) -> MediaResult<AudioRoutes> {
    // Ask only Pulse, only when audio was opted in. PipeWire-Pulse versions differ
    // on @DEFAULT_MONITOR@ resolution: use the actual default sink's named monitor.
    let provider = gst::DeviceProviderFactory::by_name("pulsedeviceprovider")
        .ok_or("PulseAudio device provider unavailable")?;
    provider
        .start()
        .map_err(|e| format!("Cannot connect to PulseAudio: {e}"))?;
    let devices = provider.devices();
    provider.stop();
    let default = |d: &gst::Device| {
        d.properties()
            .and_then(|p| p.get::<bool>("is-default").ok())
            .unwrap_or(false)
    };
    let name = |d: &gst::Device| {
        d.property::<Option<String>>("internal-name")
            .filter(|v| !v.is_empty())
            .ok_or("Unnamed PulseAudio device")
    };
    let mut routes = AudioRoutes::default();
    if options.system {
        let sink = devices
            .iter()
            .find(|d| d.has_classes("Audio/Sink") && default(d))
            .ok_or("No default PulseAudio output for system audio")?;
        let monitor = format!("{}.monitor", name(sink)?);
        if !devices
            .iter()
            .any(|d| d.has_classes("Audio/Source") && name(d).is_ok_and(|v| v == monitor))
        {
            return Err("The default output has no accessible PulseAudio monitor".into());
        }
        routes.monitor = Some(monitor);
    }
    if options.microphone {
        let d = devices
            .iter()
            .find(|d| d.has_classes("Audio/Source") && default(d))
            .ok_or("No default PulseAudio microphone")?;
        let mic = name(d)?;
        if mic.ends_with(".monitor")
            || d.properties()
                .and_then(|p| p.get::<String>("device.class").ok())
                .as_deref()
                == Some("monitor")
            || Some(&mic) == routes.monitor.as_ref()
        {
            return Err("Default Pulse input is an output monitor, not a microphone".into());
        }
        routes.microphone = Some(mic);
    }
    Ok(routes)
}
fn audio_src(test: bool, monitor: bool, routes: &AudioRoutes) -> MediaResult<gst::Element> {
    if test {
        let src = element("audiotestsrc")?;
        src.set_property("is-live", true);
        src.set_property("samplesperbuffer", 4096i32);
        src.set_property_from_str("wave", "sine-table");
        src.set_property("freq", if monitor { 880.0f64 } else { 440.0 });
        Ok(src)
    } else {
        let src = element("pulsesrc")?;
        let device = if monitor {
            &routes.monitor
        } else {
            &routes.microphone
        };
        src.set_property(
            "device",
            device.as_deref().ok_or("PulseAudio route not verified")?,
        );
        src.set_property("provide-clock", false);
        Ok(src)
    }
}
fn link_source(
    p: &gst::Pipeline,
    src: &gst::Element,
    dest: &gst::Element,
    region: Option<Region>,
) -> MediaResult<()> {
    p.add(src).map_err(|e| e.to_string())?;
    if let Some(r) = region {
        r.validate(None)?;
        let crop = element("videocrop")?;
        crop.set_property("left", r.x as i32);
        crop.set_property("top", r.y as i32);
        crop.set_property("right", -1i32);
        crop.set_property("bottom", -1i32);
        // videocrop computes right/bottom against the actual upstream caps and rejects
        // out-of-bounds input on negotiation, before the encoder receives a single frame.
        let size = caps(
            gst::Caps::builder("video/x-raw")
                .field("width", r.width as i32)
                .field("height", r.height as i32)
                .build(),
        )?;
        p.add_many([&crop, &size]).map_err(|e| e.to_string())?;
        gst::Element::link_many([src, &crop, &size, dest]).map_err(|e| e.to_string())
    } else {
        src.link(dest).map_err(|e| e.to_string())
    }
}
pub(crate) fn capture(
    input: VideoInput,
    audio: AudioOptions,
    take: &Take,
    segment_seconds: u64,
    region: Option<Region>,
) -> MediaResult<Capture> {
    let video_codec = working_encoder(true)?;
    let aac = audio.any().then(|| working_encoder(false)).transpose()?;
    let p = Running(gst::Pipeline::new());
    p.0.use_clock(Some(&gst::SystemClock::obtain()));
    let test = matches!(input, VideoInput::Synthetic | VideoInput::SyntheticSlow(_));
    let timing = crate::timing::Timing::default();
    let src = input.source()?;
    let mut acquisition = vec![src.clone()];
    let q = queue()?;
    // Keep the newest real frames when software encoding cannot keep up. Never
    // backpressure the common audio clock or replay a backlog of stale video.
    q.set_property_from_str("leaky", "downstream");
    q.set_property("max-size-time", 250_000_000u64);
    q.set_property("max-size-buffers", 3u32);
    let cv = element("videoconvert")?;
    let scale = element("videoscale")?;
    let rate = element("videorate")?;
    rate.set_property("drop-only", true);
    rate.set_property("skip-to-first", true);
    rate.set_property("max-rate", if test { 10i32 } else { 30 });
    let mut raw = gst::Caps::builder("video/x-raw").field("format", "I420");
    if test {
        raw = raw.field("width", 160).field("height", 120);
        eprintln!(
            "synthetic 160x120@10 video={video_codec} audio={}",
            aac.unwrap_or("none")
        );
    } else {
        raw = raw
            .field("width", gst::IntRange::<i32>::with_step(2, 4096, 2))
            .field("height", gst::IntRange::<i32>::with_step(2, 4096, 2));
    }
    let filter = caps(raw.build())?;
    let enc = video_encoder(video_codec)?;
    if test {
        let frames = (segment_seconds * 10).min(60) as u32;
        match video_codec {
            "openh264enc" => enc.set_property("gop-size", frames),
            "x264enc" => enc.set_property("key-int-max", frames),
            _ => (),
        }
    }
    let parse = element("h264parse")?;
    parse.set_property("config-interval", -1i32);
    let h264 = caps(
        gst::Caps::builder("video/x-h264")
            .field("stream-format", "avc")
            .field("alignment", "au")
            .build(),
    )?;
    let delay = if let VideoInput::SyntheticSlow(ms) = input {
        let e = element("identity")?;
        e.set_property("sleep-time", ms * 1000);
        Some(e)
    } else {
        None
    };
    let mut video = vec![&q];
    if let Some(e) = &delay {
        video.push(e);
    }
    video.extend([&cv, &scale, &rate, &filter, &enc, &parse, &h264]);
    chain(&p.0, &video)?;
    timing.attach("video_input", &src)?;
    timing.attach("video_after_queue", &q)?;
    timing.attach("video_encoded", &h264)?;
    if test {
        let raster = caps(
            gst::Caps::builder("video/x-raw")
                .field("format", "I420")
                .field("width", 160)
                .field("height", 120)
                .field("framerate", gst::Fraction::new(10, 1))
                .build(),
        )?;
        p.0.add(&raster).map_err(|e| e.to_string())?;
        raster.link(&q).map_err(|e| e.to_string())?;
        link_source(&p.0, &src, &raster, region)?;
    } else {
        link_source(&p.0, &src, &q, region)?;
    }
    let av_start = crate::pcm::Start::default();
    if audio.any() {
        av_start.gate_video(&filter)?;
    }
    let split = element("splitmuxsink")?;
    split.set_property("location", take.open_pattern()?);
    split.set_property("max-size-time", segment_seconds * 1_000_000_000);
    split.set_property("send-keyframe-requests", true);
    // Synchronous finalization bounds file descriptors and avoids concurrent fragment writers.
    split.set_property("muxer", element("mp4mux")?);
    p.0.add(&split).map_err(|e| e.to_string())?;
    to_request(&h264, &split, "video")?;
    let first_video = Arc::new(AtomicBool::new(false));
    let first_audio = Arc::new(AtomicBool::new(!audio.any()));
    mark_buffers(&h264, "src", &first_video)?;
    let mut first_inputs = Vec::new();
    let mut bridge = None;
    let mut mic = None;
    let mut monitor = None;
    if let Some(aac) = aac {
        let mixer = element("audiomixer")?;
        mixer.set_property_from_str("start-time-selection", "first");
        mixer.set_property("output-buffer-duration", 85_333_333u64);
        let conv = element("audioconvert")?;
        let resample = element("audioresample")?;
        let stereo = || {
            caps(
                gst::Caps::builder("audio/x-raw")
                    .field("rate", 48000)
                    .field("channels", 2)
                    .build(),
            )
        };
        let raw = stereo()?;
        let enc = element(aac)?;
        let parse = element("aacparse")?;
        let aac_caps = caps(
            gst::Caps::builder("audio/mpeg")
                .field("mpegversion", 4)
                .field("stream-format", "raw")
                .build(),
        )?;
        chain(
            &p.0,
            &[&mixer, &conv, &resample, &raw, &enc, &parse, &aac_caps],
        )?;
        mark_buffers(&aac_caps, "src", &first_audio)?;
        timing.attach("audio_encoded", &aac_caps)?;
        to_request(&aac_caps, &split, "audio_%u")?;
        let pcm = gst::Caps::builder("audio/x-raw")
            .field("format", "S16LE")
            .field("rate", 48000)
            .field("channels", 2)
            .field("layout", "interleaved")
            .build();
        let pair = crate::pcm::Bridge::new(
            &p.0,
            &mixer,
            usize::from(audio.system) + usize::from(audio.microphone),
            &pcm,
            av_start,
            &timing,
        )?;
        let routes = if test {
            AudioRoutes::default()
        } else {
            pulse_routes(audio)?
        };
        for is_monitor in [false, true] {
            if !(if is_monitor {
                audio.system
            } else {
                audio.microphone
            }) {
                continue;
            }
            let src = audio_src(test, is_monitor, &routes)?;
            timing.attach(if is_monitor { "system" } else { "microphone" }, &src)?;
            let received = Arc::new(AtomicBool::new(false));
            mark_buffers(&src, "src", &received)?;
            first_inputs.push(received);
            let q = queue()?;
            let convert = element("audioconvert")?;
            let resample = element("audioresample")?;
            let filter = caps(pcm.clone())?;
            let sink = pair.sink(usize::from(is_monitor && audio.microphone))?;
            acquisition.push(src.clone());
            if test {
                let raw_in = caps(pcm.clone())?;
                chain(
                    &p.0,
                    &[&src, &raw_in, &q, &convert, &resample, &filter, &sink],
                )?;
            } else {
                chain(&p.0, &[&src, &q, &convert, &resample, &filter, &sink])?;
            }
            if !test {
                if is_monitor {
                    monitor = Some(src);
                } else {
                    mic = Some(src);
                }
            }
        }
        bridge = Some(pair);
    }
    Ok(Capture {
        pipeline: p,
        timing,
        bridge,
        acquisition,
        _input: input,
        first_video,
        first_audio,
        first_inputs,
        mic,
        monitor,
    })
}

#[cfg(feature = "portal")]
pub(crate) fn verify(input: VideoInput, valid: Arc<AtomicBool>) -> MediaResult<()> {
    gst::init().map_err(|e| e.to_string())?;
    let p = Running(gst::Pipeline::new());
    let src = input.source()?;
    src.set_property("num-buffers", 1i32);
    let raw = caps(gst::Caps::builder("video/x-raw").build())?;
    let sink = element("fakesink")?;
    sink.set_property("sync", false);
    let received = Arc::new(AtomicBool::new(false));
    mark_buffers(&sink, "sink", &received)?;
    chain(&p.0, &[&src, &raw, &sink])?;
    p.play()?;
    wait_eos(
        &p.0.bus().unwrap(),
        Instant::now() + Duration::from_secs(12),
        Some(&valid),
    )?;
    received
        .load(Ordering::Acquire)
        .then_some(())
        .ok_or("Authorized PipeWire node did not deliver a video frame".into())
}

#[cfg(any(feature = "portal", test))]
pub(crate) fn snapshot(
    input: VideoInput,
    valid: Arc<AtomicBool>,
    dir: &std::path::Path,
    region: Option<Region>,
) -> MediaResult<(PathBuf, u32, u32)> {
    gst::init().map_err(|e| e.to_string())?;
    use std::io::Read;
    storage::private_dir(dir, true)?;
    let name = storage::uuid();
    let (staged, f) = Scratch::new_file(dir.join(format!(".snapshot-{name}.part")))?;
    let p = Running(gst::Pipeline::new());
    let src = input.source()?;
    src.set_property("num-buffers", 1i32);
    let conv = element("videoconvert")?;
    let png = element("pngenc")?;
    png.set_property("snapshot", true);
    let sink = element("fdsink")?;
    sink.set_property("fd", f.as_raw_fd());
    chain(&p.0, &[&conv, &png, &sink])?;
    link_source(&p.0, &src, &conv, region)?;
    p.play()?;
    wait_eos(
        &p.0.bus().unwrap(),
        Instant::now() + Duration::from_secs(12),
        Some(&valid),
    )?;
    drop(p);
    let mut header = [0; 24];
    std::fs::File::open(&staged.0)
        .and_then(|mut f| f.read_exact(&mut header))
        .map_err(|e| e.to_string())?;
    if !header.starts_with(b"\x89PNG\r\n\x1a\n") || &header[12..16] != b"IHDR" {
        return Err("No valid PNG received".into());
    }
    if !valid.load(Ordering::Acquire) {
        return Err("Source revoked before screenshot completed".into());
    }
    let output = dir.join(format!("TinyClip-{name}.png"));
    storage::publish(&staged.0, &output)?;
    Ok((
        output,
        u32::from_be_bytes(header[16..20].try_into().unwrap()),
        u32::from_be_bytes(header[20..24].try_into().unwrap()),
    ))
}

pub(crate) fn finalize(take: &Take, expected: Option<std::time::Duration>) -> MediaResult<PathBuf> {
    let parts = take.segments()?;
    let mut consumed: Vec<_> = parts
        .iter()
        .map(|p| storage::ClosedFile::identify(p))
        .collect::<Result<_, _>>()?;
    let output = take.output();
    if output.try_exists().map_err(|e| e.to_string())? {
        return Err("Recording output already exists; refusing to overwrite".into());
    }
    {
        crate::disk_budget::require_remux_space(&take.dir, &parts)?;
        let (staged, file) =
            Scratch::new_file(take.dir.join(format!(".remux-{}.part", storage::uuid())))?;
        let origins = take.origins(&parts, &mut consumed, expected.is_some())?;
        let playable = crate::remux::write(&parts, origins.as_deref(), &file)?;
        if let Some(expected) = expected {
            if !crate::timing::enough(expected.as_secs_f64(), playable) {
                return Err(format!(
                    "Final MP4 covers {playable:.3}s of {:.3}s active; closed segments retained for recovery",
                    expected.as_secs_f64()
                ));
            }
        }
        storage::publish(&staged.0, &output)?;
    }
    // Publication has already been synced. A cleanup failure does not mean that
    // a caller should retry publishing (or overwrite its successful output).
    if let Err(e) = take.retire(&consumed) {
        eprintln!("Recording saved; could not retire all working files: {e}");
    }
    Ok(output)
}
