//! Lossless remux, one independently seekable keyframe fragment at a time.
//! A closing fragment may have an empty AAC track. Waiting for splitmuxsrc to
//! rendezvous all pads of such a fragment can deadlock; use actual media samples.
use crate::{
    MediaResult,
    engine::{Running, element, message_error},
};
use gst::prelude::*;
use std::{
    collections::VecDeque,
    fs::File,
    os::fd::AsRawFd,
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

const VIDEO: usize = 0;
const AUDIO: usize = 1;
const MAX_FRAGMENT: usize = 96 * 1024 * 1024;
#[derive(Default)]
struct Part {
    caps: [Option<gst::Caps>; 2],
    packets: [Vec<gst::Buffer>; 2],
    bytes: usize,
    error: Option<String>,
}
fn load(path: &Path) -> MediaResult<Part> {
    let capture = Arc::new(Mutex::new(Part::default()));
    let p = Running(gst::Pipeline::new());
    let file = element("filesrc")?;
    file.set_property("location", path.to_str().ok_or("Non-UTF8 recording path")?);
    let demux = element("qtdemux")?;
    p.0.add_many([&file, &demux]).map_err(|e| e.to_string())?;
    file.link(&demux).map_err(|e| e.to_string())?;
    let weak = p.0.downgrade();
    let saved = capture.clone();
    demux.connect_pad_added(move |_, pad| {
        let destination = saved.clone();
        let attach = || -> MediaResult<()> {
            let p = weak.upgrade().ok_or("Capture reader disappeared")?;
            let track = if pad.name().starts_with("video_") {
                VIDEO
            } else if pad.name().starts_with("audio_") {
                AUDIO
            } else {
                return Err("Unrecognized MP4 track".into());
            };
            let sink = element("fakesink")?;
            sink.set_property("sync", false);
            sink.set_property("async", false);
            sink.set_property("enable-last-sample", false);
            let sinkpad = sink.static_pad("sink").unwrap();
            sinkpad.add_probe(gst::PadProbeType::BUFFER, move |pad, info| {
                let mut part = destination.lock().unwrap();
                if part.error.is_some() {
                    return gst::PadProbeReturn::Drop;
                }
                let mut add = || -> MediaResult<()> {
                    let buffer = info.buffer().ok_or("Missing encoded sample")?;
                    if buffer.size() == 0 {
                        return Ok(());
                    }
                    if part.caps[track].is_none() {
                        let caps = pad.current_caps().ok_or("Missing encoded track caps")?;
                        let s = caps.structure(0).ok_or("Missing MP4 sample description")?;
                        if (track == VIDEO && s.name() != "video/x-h264")
                            || (track == AUDIO
                                && (s.name() != "audio/mpeg"
                                    || s.get::<i32>("mpegversion").ok() != Some(4)))
                        {
                            return Err("Fragment contains a non-H.264/AAC stream".into());
                        }
                        part.caps[track] = Some(caps);
                    }
                    let event = pad
                        .sticky_event::<gst::event::Segment>(0)
                        .ok_or("Missing sample time segment")?;
                    let segment = event
                        .segment()
                        .downcast_ref::<gst::ClockTime>()
                        .ok_or("Invalid sample time format")?;
                    let pts = buffer.pts().and_then(|t| segment.to_running_time(t));
                    // qtdemux may send decode-only preroll before a track edit. Our
                    // software encoders produce closed GOPs without reordered frames.
                    let Some(pts) = pts else { return Ok(()) };
                    let dts = buffer
                        .dts()
                        .and_then(|t| segment.to_running_time(t))
                        .unwrap_or(pts);
                    let size = buffer.size();
                    part.bytes = part.bytes.checked_add(size).ok_or("Fragment too large")?;
                    if part.bytes > MAX_FRAGMENT {
                        return Err(
                            "Encoded fragment exceeds the private remux memory limit".into()
                        );
                    }
                    let mut packet = buffer.clone();
                    let meta = packet.make_mut();
                    meta.set_pts(pts);
                    meta.set_dts(dts);
                    if meta.duration().is_none() {
                        return Err("Encoded sample has no duration".into());
                    }
                    part.packets[track].push(packet);
                    Ok(())
                };
                if let Err(e) = add() {
                    part.error = Some(e);
                }
                gst::PadProbeReturn::Ok
            });
            p.add(&sink).map_err(|e| e.to_string())?;
            pad.link(&sinkpad).map_err(|e| e.to_string())?;
            sink.sync_state_with_parent().map_err(|e| e.to_string())
        };
        if let Err(e) = attach() {
            saved.lock().unwrap().error = Some(e);
        }
    });
    p.play()?;
    let bus = p.0.bus().unwrap();
    let until = Instant::now() + Duration::from_secs(90);
    loop {
        if let Some(e) = capture.lock().unwrap().error.clone() {
            return Err(e);
        }
        if Instant::now() > until {
            return Err("Timed out demuxing a committed fragment".into());
        }
        if let Some(msg) = bus.timed_pop(gst::ClockTime::from_mseconds(100)) {
            match msg.view() {
                gst::MessageView::Eos(_) => break,
                gst::MessageView::Error(e) => return Err(message_error(e)),
                _ => (),
            }
        }
    }
    drop(p);
    let mut part = capture.lock().unwrap();
    if let Some(e) = part.error.take() {
        return Err(e);
    }
    if part.packets[VIDEO]
        .first()
        .is_none_or(|b| b.flags().contains(gst::BufferFlags::DELTA_UNIT))
    {
        return Err("Fragment does not start with a video keyframe".into());
    }
    Ok(std::mem::take(&mut *part))
}

struct Output {
    pipeline: Running,
    sources: [Option<gst::Element>; 2],
    bus: gst::Bus,
}
impl Output {
    fn new(file: &File, caps: &[Option<gst::Caps>; 2]) -> MediaResult<Self> {
        let p = Running(gst::Pipeline::new());
        let mux = element("mp4mux")?;
        let sink = element("fdsink")?;
        sink.set_property("fd", file.as_raw_fd());
        sink.set_property("sync", false);
        p.0.add_many([&mux, &sink]).map_err(|e| e.to_string())?;
        mux.link(&sink).map_err(|e| e.to_string())?;
        let mut sources = [None, None];
        for (i, caps) in caps.iter().enumerate() {
            if let Some(caps) = caps {
                let src = element("appsrc")?;
                src.set_property("caps", caps);
                src.set_property("format", gst::Format::Time);
                src.set_property("max-bytes", 8 * 1024 * 1024u64);
                p.0.add(&src).map_err(|e| e.to_string())?;
                let pad = mux
                    .request_pad_simple(if i == VIDEO { "video_%u" } else { "audio_%u" })
                    .ok_or("Missing MP4 mux pad")?;
                src.static_pad("src")
                    .unwrap()
                    .link(&pad)
                    .map_err(|e| e.to_string())?;
                sources[i] = Some(src);
            }
        }
        let bus = p.0.bus().unwrap();
        p.play()?;
        Ok(Self {
            pipeline: p,
            sources,
            bus,
        })
    }
    fn push(&self, track: usize, buffer: gst::Buffer) -> MediaResult<()> {
        let src = self.sources[track]
            .as_ref()
            .ok_or("Track layout changed during recording")?;
        let deadline = Instant::now() + Duration::from_secs(30);
        while src.property::<u64>("current-level-bytes") > 8 * 1024 * 1024 {
            if let Some(msg) = self
                .bus
                .timed_pop_filtered(gst::ClockTime::from_mseconds(5), &[gst::MessageType::Error])
            {
                if let gst::MessageView::Error(e) = msg.view() {
                    return Err(message_error(e));
                }
            }
            if Instant::now() > deadline {
                return Err("Remux writer stopped consuming samples".into());
            }
        }
        src.emit_by_name::<gst::FlowReturn>("push-buffer", &[&buffer])
            .into_result()
            .map(|_| ())
            .map_err(|e| e.to_string())
    }
    fn finish(self) -> MediaResult<()> {
        for src in self.sources.iter().flatten() {
            src.emit_by_name::<gst::FlowReturn>("end-of-stream", &[])
                .into_result()
                .map_err(|e| e.to_string())?;
        }
        let msg = self
            .bus
            .timed_pop_filtered(
                gst::ClockTime::from_seconds(90),
                &[gst::MessageType::Eos, gst::MessageType::Error],
            )
            .ok_or("Timed out closing seekable MP4")?;
        match msg.view() {
            gst::MessageView::Eos(_) => {
                drop(self.pipeline);
                Ok(())
            }
            gst::MessageView::Error(e) => Err(message_error(e)),
            _ => unreachable!(),
        }
    }
}

pub(super) fn write(paths: &[PathBuf], origins: Option<&[u64]>, file: &File) -> MediaResult<f64> {
    let mut first = Some(load(paths.first().ok_or("No fragments")?)?);
    let initial_caps = first.as_ref().unwrap().caps.clone();
    let audio = initial_caps[AUDIO].is_some();
    let output = Output::new(file, &initial_caps)?;
    let mut base = gst::ClockTime::ZERO;
    let mut ends = [gst::ClockTime::ZERO; 2];
    let mut pending: [VecDeque<gst::Buffer>; 2] = Default::default();
    let mut pending_bytes = 0usize;
    let mut written = [gst::ClockTime::ZERO; 2];
    for (index, path) in paths.iter().enumerate() {
        if let Some(stamps) = origins {
            base = gst::ClockTime::from_nseconds(stamps[index]);
        }
        let mut part = match first.take() {
            Some(part) => part,
            None => load(path)?,
        };
        for track in [VIDEO, AUDIO] {
            if let Some(c) = &part.caps[track] {
                let init = initial_caps[track]
                    .as_ref()
                    .ok_or("Unexpected new track during remux")?;
                let desc = |c: &gst::Caps| {
                    c.structure(0)
                        .and_then(|s| s.get::<gst::Buffer>("codec_data").ok())
                };
                let (a, b) = (desc(init), desc(c));
                if let (Some(a), Some(b)) = (a, b) {
                    if a.map_readable().map_err(|e| e.to_string())?.as_slice()
                        != b.map_readable().map_err(|e| e.to_string())?.as_slice()
                    {
                        return Err("Codec configuration changed between MP4 segments".into());
                    }
                }
            }
            for mut packet in part.packets[track].drain(..) {
                let b = packet.make_mut();
                let pts = base
                    .checked_add(b.pts().unwrap())
                    .ok_or("MP4 timestamp overflow")?;
                let dts = base
                    .checked_add(b.dts().unwrap())
                    .ok_or("MP4 timestamp overflow")?;
                b.set_pts(pts);
                b.set_dts(dts);
                ends[track] = ends[track].max(
                    pts.checked_add(b.duration().unwrap())
                        .ok_or("MP4 timestamp overflow")?,
                );
                pending_bytes += b.size();
                if pending_bytes > 2 * MAX_FRAGMENT {
                    return Err("A recording track stalled; original fragments retained".into());
                }
                pending[track].push_back(packet);
            }
        }
        base = ends[VIDEO];
        // Bound the playable interval by actual samples, not container-declared
        // duration of an empty terminal edit. Keep unmatched packets for the next
        // fragment. A partial A/V tail at final EOS is not stretched into silence.
        let limit = if audio {
            ends[VIDEO].min(ends[AUDIO])
        } else {
            ends[VIDEO]
        };
        loop {
            let next = [VIDEO, AUDIO]
                .into_iter()
                .filter_map(|i| pending[i].front().map(|b| (i, b)))
                .filter(|(_, b)| b.pts().unwrap() < limit)
                .min_by_key(|(_, b)| b.dts().unwrap())
                .map(|(i, _)| i);
            let Some(i) = next else { break };
            let b = pending[i].pop_front().unwrap();
            pending_bytes -= b.size();
            written[i] = written[i].max(b.pts().unwrap() + b.duration().unwrap());
            output.push(i, b)?;
        }
    }
    output.finish()?;
    eprintln!(
        "remux-timing fragments={} accumulated_video_s={:.3} accumulated_audio_end_s={:.3} mux_video_end_s={:.3} mux_audio_end_s={:.3}",
        paths.len(),
        crate::timing::sec(ends[VIDEO].nseconds()),
        crate::timing::sec(ends[AUDIO].nseconds()),
        crate::timing::sec(written[VIDEO].nseconds()),
        crate::timing::sec(written[AUDIO].nseconds())
    );
    Ok(crate::timing::sec(
        (if audio {
            written[VIDEO].min(written[AUDIO])
        } else {
            written[VIDEO]
        })
        .nseconds(),
    ))
}
