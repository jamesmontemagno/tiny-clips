//! Local GTK screenshot editing and a filesystem-backed PNG/MP4 library.
//!
//! Open a [`ClipStore`] once and give the media engine its private capture directories. Open the
//! editor/library on the GTK main thread. External inputs are restricted to the exact user-granted
//! portal image in [`ClipStore::import_portal_png`] or a trusted stream frame for [`choose_region`]
//! and [`ClipStore::save_granted_region`]. These APIs never accept paths from agents or command-line callers.
//! Edits always save a new PNG. Drag or enter source pixels to prepare an edit, then Apply;
//! the editor's dashed guide is never exported. Until a pending edit is applied or discarded,
//! export and clipboard actions are disabled. The local library searches its 500 most recent
//! items by name/kind in memory; agent clients can filter the same `library.list` result.
//!
//! Automation hosts must pair, authorize scopes, and audit before calling [`dispatch`]. Only
//! [`COMMANDS`] are implemented. All nine shared annotation kinds use source-image pixels;
//! unrelated tool fields and out-of-bounds geometry are errors. Only solid redaction guarantees
//! opacity. Blur/pixelation are visual effects, not a way to remove sensitive information.
mod annotation;
mod dispatch;
mod editor;
mod editor_tools;
mod geometry;
mod library;
mod store;

pub use dispatch::{COMMANDS, dispatch};
pub use editor::{choose_region, open_editor};
pub use geometry::Region;
pub use library::{copy_clip, open_library, reveal_clip};
pub use store::{Clip, ClipKind, ClipStore};

#[derive(Debug, Clone)]
pub struct ClipError {
    pub code: &'static str,
    pub message: String,
}

impl ClipError {
    fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
    fn invalid(message: impl Into<String>) -> Self {
        Self::new("invalid_params", message)
    }
    fn missing() -> Self {
        Self::new("not_found", "No such file in the local capture library.")
    }
    fn internal(err: impl std::fmt::Display) -> Self {
        Self::new("internal", err.to_string())
    }
}

impl std::fmt::Display for ClipError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message)
    }
}
impl std::error::Error for ClipError {}

type Result<T> = std::result::Result<T, ClipError>;
