use crate::MediaResult;
use gst::prelude::*;
use std::{
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

#[derive(Clone, Default)]
struct Samples {
    first: Option<u64>,
    end: u64,
    last_pts: u64,
    covered: u64,
    largest_gap: u64,
    count: u64,
    regressions: u64,
    first_wall: Option<Instant>,
    last_wall: Option<Instant>,
}
impl Samples {
    fn add(&mut self, pts: u64, duration: u64) {
        let now = Instant::now();
        self.first_wall.get_or_insert(now);
        self.last_wall = Some(now);
        self.first.get_or_insert(pts);
        if self.count > 0 {
            self.largest_gap = self.largest_gap.max(pts.saturating_sub(self.end));
            if pts < self.last_pts {
                self.regressions += 1;
            }
        }
        let end = pts.saturating_add(duration);
        self.covered = self
            .covered
            .saturating_add(end.saturating_sub(self.end.max(pts)));
        self.end = self.end.max(end);
        self.last_pts = pts;
        self.count += 1;
    }
    fn span(&self) -> u64 {
        self.end.saturating_sub(self.first.unwrap_or(0))
    }
}
#[derive(Clone, Default)]
pub(crate) struct Timing(Arc<Mutex<Vec<(&'static str, Arc<Mutex<Samples>>)>>>);
impl Timing {
    pub fn attach(&self, name: &'static str, element: &gst::Element) -> MediaResult<()> {
        let samples = Arc::new(Mutex::new(Samples::default()));
        self.0.lock().unwrap().push((name, samples.clone()));
        let segment: Mutex<Option<gst::FormattedSegment<gst::ClockTime>>> = Mutex::new(None);
        element
            .static_pad("src")
            .ok_or("Missing timing probe pad")?
            .add_probe(
                gst::PadProbeType::BUFFER | gst::PadProbeType::EVENT_DOWNSTREAM,
                move |_, info| {
                    if let Some(gst::EventView::Segment(s)) = info.event().map(|e| e.view()) {
                        *segment.lock().unwrap() =
                            s.segment().downcast_ref::<gst::ClockTime>().cloned();
                    }
                    if let Some(b) = info.buffer() {
                        if let Some(pts) = b
                            .pts()
                            .and_then(|t| segment.lock().unwrap().as_ref()?.to_running_time(t))
                        {
                            samples.lock().unwrap().add(
                                pts.nseconds(),
                                b.duration().map(|t| t.nseconds()).unwrap_or(0),
                            );
                        }
                    }
                    gst::PadProbeReturn::Ok
                },
            )
            .ok_or("Cannot install capture timing probe")?;
        Ok(())
    }
    pub fn report(
        &self,
        active: Duration,
        wall: Duration,
        paused: Duration,
        pipeline_ns: u64,
        fragments: u32,
        split_sum: u64,
    ) {
        eprintln!(
            "capture-timing active_s={:.3} wall_s={:.3} paused_s={:.3} gst_running_s={:.3} closed_fragments={fragments} split_sum_s={:.3}",
            active.as_secs_f64(),
            wall.as_secs_f64(),
            paused.as_secs_f64(),
            sec(pipeline_ns),
            sec(split_sum)
        );
        for (name, s) in self.0.lock().unwrap().iter() {
            let s = s.lock().unwrap();
            eprintln!(
                "capture-track {name} packets={} first_pts_s={:.3} last_pts_s={:.3} end_s={:.3} span_s={:.3} packet_coverage_s={:.3} max_gap_s={:.3} regressions={} observed_wall_s={:.3}",
                s.count,
                sec(s.first.unwrap_or(0)),
                sec(s.last_pts),
                sec(s.end),
                sec(s.span()),
                sec(s.covered),
                sec(s.largest_gap),
                s.regressions,
                s.first_wall
                    .zip(s.last_wall)
                    .map(|(a, b)| (b - a).as_secs_f64())
                    .unwrap_or(0.0)
            );
        }
    }
    pub fn check(&self, active: Duration) -> MediaResult<()> {
        let need = active.as_secs_f64();
        for (name, s) in self.0.lock().unwrap().iter() {
            let s = s.lock().unwrap();
            let span = sec(s.span());
            if s.regressions != 0 || sec(s.largest_gap) > 2.0 {
                return Err(format!(
                    "Discontinuous capture clock for {name}: regressions={}, largest gap {:.3}s; retaining closed segments",
                    s.regressions,
                    sec(s.largest_gap)
                ));
            }
            if s.count == 0 || !enough(need, span) {
                return Err(format!(
                    "Capture coverage lost: {name} covers {span:.3}s of {need:.3}s active; closed segments retained for recovery"
                ));
            }
            if name.contains("audio") || matches!(*name, "microphone" | "system") {
                if !enough(need.max(span), sec(s.covered)) {
                    return Err(format!(
                        "Missing real audio samples: {name} supplied {:.3}s for {need:.3}s active; closed segments retained",
                        sec(s.covered)
                    ));
                }
            }
        }
        Ok(())
    }
}
pub(crate) fn enough(expected: f64, actual: f64) -> bool {
    actual + (expected * 0.01).clamp(0.5, 2.0) >= expected
}
pub(crate) fn sec(ns: u64) -> f64 {
    ns as f64 / 1_000_000_000.0
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn real_audio_coverage_is_not_the_difference_between_two_timestamps() {
        let timing = Timing::default();
        let mut sparse = Samples::default();
        for i in 0..80 {
            sparse.add(i * 100_000_000, 50_000_000);
        }
        let data = Arc::new(Mutex::new(sparse));
        // A real video frame can be displayed until the next acquisition, but
        // half a timeline of captured PCM cannot represent a full audio take.
        timing
            .0
            .lock()
            .unwrap()
            .push(("video_encoded", data.clone()));
        assert!(timing.check(Duration::from_secs(8)).is_ok());
        timing.0.lock().unwrap().push(("paired_audio_0", data));
        assert!(
            timing
                .check(Duration::from_secs(8))
                .unwrap_err()
                .contains("Missing real audio")
        );
        assert!(!enough(1800.0, 1095.210833));
    }
}
