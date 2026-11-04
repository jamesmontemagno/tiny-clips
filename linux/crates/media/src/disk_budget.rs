use crate::MediaResult;
use std::{
    ffi::CString,
    mem::MaybeUninit,
    os::unix::ffi::OsStrExt,
    path::{Path, PathBuf},
};

const MIB: u64 = 1024 * 1024;

fn available(dir: &Path) -> MediaResult<u64> {
    let path = CString::new(dir.as_os_str().as_bytes()).map_err(|_| "Invalid capture directory")?;
    let mut stat = MaybeUninit::<libc::statvfs>::uninit();
    // statvfs writes the whole output structure on success; bavail excludes reserved blocks.
    if unsafe { libc::statvfs(path.as_ptr(), stat.as_mut_ptr()) } != 0 {
        return Err(format!(
            "Cannot check space for the recording: {}",
            std::io::Error::last_os_error()
        ));
    }
    let stat = unsafe { stat.assume_init() };
    Ok(stat.f_bavail.saturating_mul(stat.f_frsize))
}

fn remux_bytes(bytes: u64) -> u64 {
    bytes.saturating_add((bytes / 50).max(4 * MIB))
}

/// Keep space for a second copy at finalization and the next keyframe-aligned fragment.
#[derive(Default)]
pub(crate) struct DiskBudget {
    closed: u64,
    largest: u64,
}
impl DiskBudget {
    pub fn note_fragment(&mut self, size: u64) {
        self.closed = self.closed.saturating_add(size);
        self.largest = self.largest.max(size);
    }
    fn reserve(&self) -> u64 {
        remux_bytes(self.closed).saturating_add(self.largest.max(8 * MIB).saturating_mul(2))
    }
    pub fn can_continue(&self, dir: &Path) -> MediaResult<bool> {
        Ok(available(dir)? >= self.reserve())
    }
}

pub(crate) fn require_remux_space(dir: &Path, parts: &[PathBuf]) -> MediaResult<()> {
    let bytes = parts
        .iter()
        .try_fold(0u64, |total, path| {
            std::fs::symlink_metadata(path).map(|m| total.saturating_add(m.len()))
        })
        .map_err(|e| e.to_string())?;
    if available(dir)? < remux_bytes(bytes) {
        return Err("Not enough free space to finalize the MP4. Completed segments are retained; free space and reopen Tiny Clips to recover them.".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn keeps_room_for_final_output_and_inflight_fragment() {
        let mut b = DiskBudget::default();
        assert!(b.reserve() >= 20 * MIB);
        b.note_fragment(16 * MIB);
        b.note_fragment(12 * MIB);
        assert!(b.reserve() >= 28 * MIB + 2 * 16 * MIB);
        assert!(remux_bytes(28 * MIB) < b.reserve());
        b.note_fragment(u64::MAX);
        assert_eq!(b.reserve(), u64::MAX);
    }
}
