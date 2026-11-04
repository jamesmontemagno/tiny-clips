//! ScreenCast authorization is intentionally separate from all unattended operations.
mod disk_budget;
mod engine;
mod pcm;
mod recorder;
mod remux;
#[cfg(feature = "portal")]
mod source;
mod storage;
#[cfg(test)]
mod tests;
mod timing;

pub use recorder::{Phase, Status};
#[cfg(feature = "portal")]
pub use source::{MediaService, SourceKind, Target};
use std::{
    path::{Path, PathBuf},
    sync::{Arc, atomic::AtomicBool},
};

pub type MediaResult<T> = Result<T, String>;

/// Half-open rectangle in the authorized stream's raster, not desktop logical space.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Region {
    pub x: u32,
    pub y: u32,
    pub width: u32,
    pub height: u32,
}
impl Region {
    pub fn validate(self, dimensions: Option<(u32, u32)>) -> MediaResult<()> {
        let limit = i32::MAX as u32;
        let (w, h) = dimensions.unwrap_or((limit, limit));
        if self.width == 0
            || self.height == 0
            || self
                .x
                .checked_add(self.width)
                .is_none_or(|v| v > w.min(limit))
            || self
                .y
                .checked_add(self.height)
                .is_none_or(|v| v > h.min(limit))
        {
            return Err("Region is outside the authorized source raster".into());
        }
        Ok(())
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct AudioOptions {
    pub microphone: bool,
    pub system: bool,
}
impl AudioOptions {
    fn any(self) -> bool {
        self.microphone || self.system
    }
}

async fn background<T: Send + 'static>(
    task: impl FnOnce() -> MediaResult<T> + Send + 'static,
) -> MediaResult<T> {
    let (tx, rx) = async_channel::bounded(1);
    std::thread::Builder::new()
        .name("tinyclips-media-io".into())
        .spawn(move || {
            let _ = tx.send_blocking(task());
        })
        .map_err(|e| e.to_string())?;
    rx.recv()
        .await
        .map_err(|_| "Media worker stopped unexpectedly".to_owned())?
}

/// Discover interrupted, non-active recordings beneath a trusted private video library.
/// No capture, permission prompt, or media rendering is performed.
pub async fn recoverable_takes(library: &Path) -> MediaResult<Vec<PathBuf>> {
    let library = library.to_path_buf();
    background(move || {
        storage::private_dir(&library, false)?;
        let mut found = Vec::new();
        for entry in std::fs::read_dir(library).map_err(|e| e.to_string())? {
            let entry = entry.map_err(|e| e.to_string())?;
            if !entry.file_type().map_err(|e| e.to_string())?.is_dir() {
                continue;
            }
            if let Ok(take) = storage::Take::existing(&entry.path()) {
                if !take.output().try_exists().map_err(|e| e.to_string())?
                    && take.segments().is_ok()
                {
                    found.push(take.dir.clone());
                }
            }
        }
        found.sort();
        Ok(found)
    })
    .await
}

/// Remux closed keyframe segments after an interrupted take. Never opens the portal.
/// The caller selects a take directory inside its own private video library.
pub async fn recover(take_dir: &Path) -> MediaResult<PathBuf> {
    let dir = take_dir.to_owned();
    background(move || {
        gst::init().map_err(|e| e.to_string())?;
        let take = storage::Take::existing(&dir)?;
        engine::finalize(&take, None)
    })
    .await
}

/// Synthetic-only diagnostics. Does not create a desktop or PulseAudio source.
#[doc(hidden)]
pub async fn synthetic_smoke(
    dir: &Path,
    seconds: u32,
    audio: AudioOptions,
    pause: bool,
) -> MediaResult<PathBuf> {
    synthetic_check(dir, seconds, audio, pause, 0).await
}
#[doc(hidden)]
pub async fn synthetic_check(
    dir: &Path,
    seconds: u32,
    audio: AudioOptions,
    pause: bool,
    video_delay_ms: u32,
) -> MediaResult<PathBuf> {
    if video_delay_ms > 2000 {
        return Err("Stress delay must be 0..2000ms".into());
    }
    if !(1..=86_400).contains(&seconds) {
        return Err("Duration must be between 1 and 86400 seconds".into());
    }
    let recorder = recorder::Recorder::default();
    recorder
        .start(
            if video_delay_ms == 0 {
                engine::VideoInput::Synthetic
            } else {
                engine::VideoInput::SyntheticSlow(video_delay_ms)
            },
            Arc::new(AtomicBool::new(true)),
            dir,
            audio,
            if seconds >= 60 { 10 } else { 2 },
            None,
        )
        .await?;
    let first = std::time::Duration::from_secs(seconds.into()) / 2;
    let wait = |duration| {
        let recorder = recorder.clone();
        background(move || {
            let until = std::time::Instant::now() + duration;
            while std::time::Instant::now() < until {
                let status = recorder.status();
                if !status.active() {
                    return Err(status
                        .error
                        .unwrap_or_else(|| "Recorder unexpectedly stopped".into()));
                }
                std::thread::sleep(
                    until
                        .saturating_duration_since(std::time::Instant::now())
                        .min(std::time::Duration::from_millis(250)),
                );
            }
            Ok(())
        })
    };
    wait(first).await?;
    if pause {
        recorder.pause().await?;
        wait(std::time::Duration::from_millis(300)).await?;
        recorder.resume().await?;
    }
    wait(first).await?;
    recorder.stop().await
}

#[cfg(test)]
async fn delay(duration: std::time::Duration) -> MediaResult<()> {
    background(move || {
        std::thread::sleep(duration);
        Ok(())
    })
    .await
}
