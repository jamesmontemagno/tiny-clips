use std::time::{Duration, Instant};
use tinyclips_media::Phase;

/// The capture bar's monotonic elapsed estimate. Encoded timestamps remain authoritative.
#[derive(Default)]
pub struct Elapsed {
    accumulated: Duration,
    running_since: Option<Instant>,
}

impl Elapsed {
    pub fn sample(&mut self, phase: Phase, now: Instant) -> Option<u64> {
        match phase {
            Phase::Recording => {
                let start = *self.running_since.get_or_insert(now);
                Some((self.accumulated + now.saturating_duration_since(start)).as_secs())
            }
            Phase::Paused => {
                if let Some(start) = self.running_since.take() {
                    self.accumulated += now.saturating_duration_since(start);
                }
                Some(self.accumulated.as_secs())
            }
            _ => {
                *self = Self::default();
                None
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn thirty_minutes_is_not_a_cutoff_and_pauses_do_not_count() {
        let t = Instant::now();
        let mut clock = Elapsed::default();
        let sample = |clock: &mut Elapsed, phase, seconds| {
            clock.sample(phase, t + Duration::from_secs(seconds))
        };
        assert_eq!(sample(&mut clock, Phase::Recording, 0), Some(0));
        assert_eq!(sample(&mut clock, Phase::Paused, 1200), Some(1200));
        assert_eq!(sample(&mut clock, Phase::Paused, 1500), Some(1200));
        assert_eq!(sample(&mut clock, Phase::Recording, 1501), Some(1200));
        assert_eq!(sample(&mut clock, Phase::Recording, 2102), Some(1801));
        assert_eq!(sample(&mut clock, Phase::Idle, 2103), None);
        assert_eq!(sample(&mut clock, Phase::Recording, 4000), Some(0));
    }
}
