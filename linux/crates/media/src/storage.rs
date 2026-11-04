use crate::MediaResult;
use std::{
    fs::{self, DirBuilder, File, OpenOptions, Permissions},
    os::{
        fd::AsRawFd,
        unix::fs::{DirBuilderExt, MetadataExt, OpenOptionsExt, PermissionsExt},
    },
    path::{Path, PathBuf},
};

pub(crate) fn uuid() -> String {
    gst::glib::uuid_string_random().to_string()
}
fn io(e: std::io::Error) -> String {
    e.to_string()
}
pub(crate) fn private_dir(path: &Path, create: bool) -> MediaResult<()> {
    if !path.is_absolute()
        || path
            .components()
            .any(|c| matches!(c, std::path::Component::ParentDir))
    {
        return Err("A private absolute output directory is required".into());
    }
    if create {
        DirBuilder::new()
            .recursive(true)
            .mode(0o700)
            .create(path)
            .map_err(io)?;
    }
    let m = fs::symlink_metadata(path).map_err(io)?;
    // SAFETY: geteuid has no preconditions.
    if !m.is_dir() || m.mode() & 0o077 != 0 || m.uid() != unsafe { libc::geteuid() } {
        return Err(format!(
            "Output directory must be user-owned and private (0700): {}",
            path.display()
        ));
    }
    Ok(())
}

pub(crate) fn private_file(path: &Path) -> MediaResult<File> {
    OpenOptions::new()
        .create_new(true)
        .read(true)
        .write(true)
        .mode(0o600)
        .open(path)
        .map_err(io)
}
fn sync_dir(dir: &Path) -> MediaResult<()> {
    File::open(dir).and_then(|d| d.sync_all()).map_err(io)
}

pub(crate) fn publish(staged: &Path, dest: &Path) -> MediaResult<()> {
    File::open(staged).and_then(|f| f.sync_all()).map_err(io)?;
    // Unlike rename, link never overwrites an existing library entry.
    fs::hard_link(staged, dest).map_err(io)?;
    sync_dir(dest.parent().ok_or("Invalid library path")?)
}

pub(crate) struct Take {
    pub dir: PathBuf,
    _lock: File,
    created: bool,
}
impl Take {
    pub fn new(root: &Path) -> MediaResult<Self> {
        private_dir(root, true)?;
        if !crate::disk_budget::DiskBudget::default().can_continue(root)? {
            return Err("Not enough free space to start a recoverable recording.".into());
        }
        let dir = root.join(format!(".take-{}", uuid()));
        DirBuilder::new().mode(0o700).create(&dir).map_err(io)?;
        sync_dir(root)?;
        let mut take = Self::existing(&dir)?;
        take.created = true;
        Ok(take)
    }
    pub fn existing(dir: &Path) -> MediaResult<Self> {
        let id = dir
            .file_name()
            .and_then(|s| s.to_str())
            .and_then(|s| s.strip_prefix(".take-"))
            .ok_or("Not a recording recovery directory")?;
        if !gst::glib::uuid_string_is_valid(id) {
            return Err("Not a recording recovery directory".into());
        }
        private_dir(dir.parent().ok_or("Missing recording library")?, false)?;
        private_dir(dir, false)?;
        let lock = OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW)
            .open(dir)
            .map_err(io)?;
        // SAFETY: the directory fd is owned by `lock`, which stays alive for the take.
        if unsafe { libc::flock(lock.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) } != 0 {
            return Err(format!(
                "Cannot lock recording (possibly active): {}",
                std::io::Error::last_os_error()
            ));
        }
        Ok(Self {
            dir: dir.to_owned(),
            _lock: lock,
            created: false,
        })
    }
    pub fn open_pattern(&self) -> MediaResult<String> {
        let base = self
            .dir
            .to_str()
            .ok_or("GStreamer requires a UTF-8 output directory")?;
        Ok(format!("{}/open-%08d.mp4", base.replace('%', "%%")))
    }
    pub fn closed(&self, path: &Path, origin: u64) -> MediaResult<()> {
        let number = path
            .file_name()
            .and_then(|n| n.to_str())
            .and_then(|n| n.strip_prefix("open-"))
            .and_then(|n| n.strip_suffix(".mp4"))
            .ok_or("Unexpected fragment name")?;
        if path.parent() != Some(self.dir.as_path())
            || !index_valid(number)
            || !fs::symlink_metadata(path).map_err(io)?.is_file()
        {
            return Err("Unexpected recording fragment".into());
        }
        fs::set_permissions(path, Permissions::from_mode(0o600)).map_err(io)?;
        // A variable-rate fragment may end before the next keyframe. Its local
        // MP4 sample durations cannot reconstruct the original gap on their own.
        use std::io::Write;
        let mut clock = private_file(&self.dir.join(format!("clock-{number}")))?;
        clock
            .write_all(&origin.to_be_bytes())
            .and_then(|_| clock.sync_all())
            .map_err(io)?;
        let ready = self.dir.join(format!("ready-{number}.mp4"));
        publish(path, &ready)?;
        fs::remove_file(path).map_err(io)
    }
    pub fn segments(&self) -> MediaResult<Vec<PathBuf>> {
        let mut out = Vec::new();
        for entry in fs::read_dir(&self.dir).map_err(io)? {
            let entry = entry.map_err(io)?;
            let name = entry.file_name();
            if let Some(number) = name
                .to_str()
                .and_then(|s| s.strip_prefix("ready-"))
                .and_then(|s| s.strip_suffix(".mp4"))
            {
                if !index_valid(number) || !entry.file_type().map_err(io)?.is_file() {
                    return Err("Invalid closed fragment".into());
                }
                out.push(entry.path());
            }
        }
        out.sort();
        if out.is_empty() {
            return Err("No fully closed keyframe segments available for recovery".into());
        }
        for (i, path) in out.iter().enumerate() {
            if path.file_name().and_then(|n| n.to_str())
                != Some(format!("ready-{i:08}.mp4").as_str())
            {
                return Err("Missing closed recording segment".into());
            }
        }
        Ok(out)
    }
    pub fn origins(
        &self,
        parts: &[PathBuf],
        consumed: &mut Vec<ClosedFile>,
        required: bool,
    ) -> MediaResult<Option<Vec<u64>>> {
        let mut stamps = Vec::new();
        for i in 0..parts.len() {
            let p = self.dir.join(format!("clock-{i:08}"));
            if !required && !p.try_exists().map_err(io)? {
                continue;
            }
            let m = fs::symlink_metadata(&p).map_err(io)?;
            if !m.is_file() || m.len() != 8 || m.nlink() != 1 {
                return Err("Invalid segment clock metadata".into());
            }
            let ns = u64::from_be_bytes(
                fs::read(&p)
                    .map_err(io)?
                    .try_into()
                    .map_err(|_| "Invalid segment clock length")?,
            );
            if stamps.last().is_some_and(|&prev| prev >= ns) {
                return Err("Non-monotonic original segment times".into());
            }
            stamps.push(ns);
            consumed.push(ClosedFile::identify(&p)?);
        }
        if stamps.is_empty() && !required {
            return Ok(None);
        }
        if stamps.len() != parts.len() {
            return Err("Incomplete segment clock metadata".into());
        }
        let first = stamps[0];
        for t in &mut stamps {
            *t -= first;
        }
        Ok(Some(stamps))
    }
    pub fn retired_location(&self) -> PathBuf {
        let name = self
            .dir
            .file_name()
            .unwrap()
            .to_str()
            .unwrap()
            .trim_start_matches(".take-");
        self.dir.with_file_name(format!(".recovered-{name}"))
    }
    pub fn retire(&self, consumed: &[ClosedFile]) -> MediaResult<()> {
        let root = self.dir.parent().unwrap();
        let mut retired = self.retired_location();
        // Reserve an empty, private destination. POSIX rename may replace only our
        // own empty directory, never a pre-existing recovery stash.
        loop {
            match DirBuilder::new().mode(0o700).create(&retired) {
                Ok(()) => break,
                Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {
                    retired = root.join(format!(".recovered-{}", uuid()))
                }
                Err(e) => return Err(io(e)),
            }
        }
        if let Err(e) = fs::rename(&self.dir, &retired) {
            let _ = fs::remove_dir(&retired);
            return Err(io(e));
        }
        sync_dir(root)?;
        // Uncommitted tails and anything not identical to the consumed input are
        // evidence, not garbage. In particular, never remove_dir_all on recovery.
        for file in consumed {
            let path = retired.join(&file.name);
            if let Ok(m) = fs::symlink_metadata(&path) {
                if m.is_file() && (m.dev(), m.ino(), m.len()) == file.identity {
                    fs::remove_file(path).map_err(io)?;
                }
            }
        }
        sync_dir(&retired)?;
        match fs::remove_dir(&retired) {
            Ok(()) => sync_dir(root),
            Err(e) if e.kind() == std::io::ErrorKind::DirectoryNotEmpty => Ok(()),
            Err(e) => Err(io(e)),
        }
    }
    pub fn output(&self) -> PathBuf {
        let id = self
            .dir
            .file_name()
            .unwrap()
            .to_str()
            .unwrap()
            .trim_start_matches(".take-");
        self.dir
            .parent()
            .unwrap()
            .join(format!("TinyClip-{id}.mp4"))
    }
}
impl Drop for Take {
    fn drop(&mut self) {
        // Preflight failures have no segments to recover. Never remove a non-empty take.
        if self.created {
            let _ = fs::remove_dir(&self.dir);
        }
    }
}
fn index_valid(s: &str) -> bool {
    s.len() == 8 && s.bytes().all(|b| b.is_ascii_digit())
}

pub(crate) struct ClosedFile {
    name: std::ffi::OsString,
    identity: (u64, u64, u64),
}
impl ClosedFile {
    pub fn identify(path: &Path) -> MediaResult<Self> {
        let m = fs::symlink_metadata(path).map_err(io)?;
        if !m.is_file() || m.len() == 0 {
            return Err("Invalid committed recording segment".into());
        }
        // A crash between committing ready-N and unlinking open-N can leave two
        // internal names for this inode. Reject hard links to anything outside it.
        if m.nlink() > 1 {
            let name = path
                .file_name()
                .and_then(|s| s.to_str())
                .and_then(|s| s.strip_prefix("ready-"))
                .ok_or("Invalid recording part name")?;
            let original = fs::symlink_metadata(path.with_file_name(format!("open-{name}"))).ok();
            if m.nlink() != 2
                || !original
                    .is_some_and(|o| o.is_file() && (o.dev(), o.ino()) == (m.dev(), m.ino()))
            {
                return Err("Refusing an externally linked recording fragment".into());
            }
        }
        Ok(Self {
            name: path
                .file_name()
                .ok_or("Missing recording part name")?
                .into(),
            identity: (m.dev(), m.ino(), m.len()),
        })
    }
}

pub(crate) struct Scratch(pub PathBuf);
impl Scratch {
    pub fn new_file(path: PathBuf) -> MediaResult<(Self, File)> {
        let file = private_file(&path)?;
        Ok((Self(path), file))
    }
}
impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}
