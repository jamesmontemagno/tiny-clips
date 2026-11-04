use tinyclips_media::{Phase, SourceKind};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Action {
    Screenshot,
    Record,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Area {
    Region,
    Screen,
    Window,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Source {
    Shared,
    OneCapture(SourceKind),
}

impl Area {
    pub fn source(self, shared: Option<SourceKind>) -> Source {
        let wanted = match self {
            Self::Region => shared.unwrap_or(SourceKind::Monitor),
            Self::Screen => SourceKind::Monitor,
            Self::Window => SourceKind::Window,
        };
        if shared == Some(wanted) {
            Source::Shared
        } else {
            Source::OneCapture(wanted)
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Stage {
    Home,
    Targets(Action),
    Countdown,
    Starting,
    Recording,
    Paused,
    Saving,
}

impl Stage {
    pub fn resolve(action: Option<Action>, countdown: u32, phase: Phase) -> Self {
        match phase {
            Phase::Starting => Self::Starting,
            Phase::Recording => Self::Recording,
            Phase::Paused => Self::Paused,
            Phase::Finalizing => Self::Saving,
            Phase::Idle | Phase::Failed if countdown > 0 => Self::Countdown,
            Phase::Idle | Phase::Failed => action.map_or(Self::Home, Self::Targets),
        }
    }
    pub fn page(self) -> &'static str {
        match self {
            Self::Home => "home",
            Self::Targets(_) => "targets",
            _ => "transport",
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn capture_kind_precedes_target_and_transport_wins_even_for_an_agent_start() {
        for phase in [Phase::Idle, Phase::Failed] {
            assert_eq!(Stage::resolve(None, 0, phase), Stage::Home);
            for action in [Action::Screenshot, Action::Record] {
                assert_eq!(
                    Stage::resolve(Some(action), 0, phase),
                    Stage::Targets(action)
                );
                assert_eq!(Stage::resolve(Some(action), 3, phase), Stage::Countdown);
            }
        }
        for (phase, stage) in [
            (Phase::Starting, Stage::Starting),
            (Phase::Recording, Stage::Recording),
            (Phase::Paused, Stage::Paused),
            (Phase::Finalizing, Stage::Saving),
        ] {
            assert_eq!(Stage::resolve(Some(Action::Screenshot), 0, phase), stage);
            assert_eq!(stage.page(), "transport");
        }
    }
    #[test]
    fn only_a_matching_explicit_grant_skips_the_picker() {
        for area in [Area::Region, Area::Screen] {
            assert_eq!(area.source(None), Source::OneCapture(SourceKind::Monitor));
            assert_eq!(area.source(Some(SourceKind::Monitor)), Source::Shared);
        }
        assert_eq!(
            Area::Region.source(Some(SourceKind::Window)),
            Source::Shared
        );
        assert_eq!(
            Area::Screen.source(Some(SourceKind::Window)),
            Source::OneCapture(SourceKind::Monitor)
        );
        assert_eq!(
            Area::Window.source(None),
            Source::OneCapture(SourceKind::Window)
        );
        assert_eq!(
            Area::Window.source(Some(SourceKind::Monitor)),
            Source::OneCapture(SourceKind::Window)
        );
        assert_eq!(
            Area::Window.source(Some(SourceKind::Window)),
            Source::Shared
        );
    }
}
