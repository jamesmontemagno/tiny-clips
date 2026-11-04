use crate::{ClipError, Region, Result};
use gtk::{
    gdk_pixbuf::{Pixbuf, PixbufLoader},
    glib,
    prelude::*,
};
use rustix::{
    fs::{self, AtFlags, Dir, Mode, OFlags, RenameFlags},
    io::Errno,
    process::geteuid,
};
use std::{
    ffi::OsStr,
    fs::File,
    io::{Read, Seek, Write},
    os::unix::fs::MetadataExt,
    path::{Component, Path, PathBuf},
    sync::Arc,
    time::{SystemTime, UNIX_EPOCH},
};

const MAX_PNG_BYTES: u64 = 160 * 1024 * 1024;
const MAX_PIXELS: u64 = 32_000_000;
const PNG_PREFIX: &[u8] = b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ClipKind {
    Screenshot,
    Video,
}
impl ClipKind {
    pub fn name(self) -> &'static str {
        match self {
            Self::Screenshot => "screenshot",
            Self::Video => "video",
        }
    }
    fn suffix(self) -> &'static str {
        match self {
            Self::Screenshot => "png",
            Self::Video => "mp4",
        }
    }
}
#[derive(Debug, Clone)]
pub struct Clip {
    pub path: PathBuf,
    pub kind: ClipKind,
    pub size_bytes: u64,
    pub created: SystemTime,
}
impl Clip {
    pub fn name(&self) -> &str {
        self.path.file_name().and_then(OsStr::to_str).unwrap_or("")
    }
    pub fn created_at(&self) -> String {
        self.created
            .duration_since(UNIX_EPOCH)
            .ok()
            .and_then(|d| glib::DateTime::from_unix_utc(d.as_secs().try_into().ok()?).ok())
            .and_then(|d| d.format("%Y-%m-%dT%H:%M:%SZ").ok())
            .map(|s| s.to_string())
            .unwrap_or_default()
    }
}
#[derive(Clone)]
struct Folder {
    path: PathBuf,
    fd: Arc<File>,
    kind: ClipKind,
}
#[derive(Clone)]
pub struct ClipStore {
    images: Folder,
    videos: Folder,
}

fn dir_flags() -> OFlags {
    OFlags::RDONLY | OFlags::DIRECTORY | OFlags::NOFOLLOW | OFlags::CLOEXEC
}
fn child_dir(parent: &File, name: &OsStr, create: bool) -> Result<File> {
    let open = || fs::openat(parent, name, dir_flags(), Mode::empty());
    match open() {
        Ok(fd) => Ok(fd.into()),
        Err(Errno::NOENT) if create => {
            match fs::mkdirat(parent, name, Mode::from_raw_mode(0o700)) {
                Ok(()) | Err(Errno::EXIST) => {}
                Err(e) => return Err(ClipError::internal(e)),
            }
            open().map(Into::into).map_err(ClipError::internal)
        }
        Err(e) => Err(ClipError::internal(e)),
    }
}
fn walk_dirs(path: &Path, create: bool) -> Result<File> {
    if !path.is_absolute()
        || path.parent().is_none()
        || path.components().any(|c| c == Component::ParentDir)
    {
        return Err(ClipError::invalid(
            "Expected an absolute XDG data directory.",
        ));
    }
    let mut fd =
        File::from(fs::open("/", dir_flags(), Mode::empty()).map_err(ClipError::internal)?);
    for c in path.components() {
        if let Component::Normal(name) = c {
            fd = child_dir(&fd, name, create)?;
        }
    }
    Ok(fd)
}
/// XDG_DATA_HOME is supplied by the runtime, not by a client. Resolve its existing prefix once
/// (Silverblue's /home and Flatpak's home mapping need not be physical paths). Only create the
/// missing suffix beneath a user-owned directory; never follow a link below the app data root.
fn data_home(base: &Path) -> Result<(PathBuf, File)> {
    if !base.is_absolute()
        || base.parent().is_none()
        || base.components().any(|c| c == Component::ParentDir)
    {
        return Err(ClipError::invalid(
            "Expected a non-root, absolute XDG data directory.",
        ));
    }
    let mut prefix = base;
    let real = loop {
        match prefix.canonicalize() {
            Ok(p) => break p,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                prefix = prefix
                    .parent()
                    .ok_or_else(|| ClipError::invalid("No XDG data parent."))?;
            }
            Err(e) => return Err(ClipError::internal(e)),
        }
    };
    let mut fd = walk_dirs(&real, false)?;
    let mut path = real;
    let owner_ok = |d: &File| -> Result<()> {
        let m = d.metadata().map_err(ClipError::internal)?;
        if m.uid() != geteuid().as_raw() || m.mode() & 0o002 != 0 {
            return Err(ClipError::new(
                "permission_required",
                "The XDG data directory must be user-owned and not world-writable.",
            ));
        }
        Ok(())
    };
    owner_ok(&fd)?;
    for c in base
        .strip_prefix(prefix)
        .map_err(ClipError::internal)?
        .components()
    {
        if let Component::Normal(name) = c {
            fd = child_dir(&fd, name, true)?;
            owner_ok(&fd)?;
            path.push(name);
        }
    }
    Ok((path, fd))
}

fn require_private(fd: &File) -> Result<()> {
    let m = fd.metadata().map_err(ClipError::internal)?;
    if !m.is_dir() || m.uid() != geteuid().as_raw() || m.mode() & 0o077 != 0 {
        return Err(ClipError::new(
            "permission_required",
            "The Tiny Clips data directory must be owned by you and private (0700).",
        ));
    }
    Ok(())
}
fn check_dimensions(w: u32, h: u32) -> Result<()> {
    if w == 0 || h == 0 || w > 32767 || h > 32767 || u64::from(w) * u64::from(h) > MAX_PIXELS {
        Err(ClipError::invalid(
            "Image exceeds the supported 32 megapixel or 32767 pixel edge limit.",
        ))
    } else {
        Ok(())
    }
}
fn png_header(bytes: &[u8]) -> Result<(u32, u32)> {
    if bytes.len() < 24 || !bytes.starts_with(PNG_PREFIX) {
        return Err(ClipError::invalid("Not a PNG screenshot."));
    }
    let w = u32::from_be_bytes(bytes[16..20].try_into().unwrap());
    let h = u32::from_be_bytes(bytes[20..24].try_into().unwrap());
    check_dimensions(w, h)?;
    Ok((w, h))
}
/// Open exactly one regular, user-owned file relative to an already pinned directory.
fn open_regular(dir: &File, name: &OsStr) -> Result<(File, std::fs::Metadata)> {
    // Reject devices, links and FIFOs before opening. Recheck the descriptor to catch replacements.
    let s = fs::statat(dir, name, AtFlags::SYMLINK_NOFOLLOW).map_err(|_| ClipError::missing())?;
    if fs::FileType::from_raw_mode(s.st_mode) != fs::FileType::RegularFile || s.st_nlink != 1 {
        return Err(ClipError::missing());
    }
    let file = File::from(
        fs::openat(
            dir,
            name,
            OFlags::RDONLY | OFlags::NOFOLLOW | OFlags::NONBLOCK | OFlags::CLOEXEC,
            Mode::empty(),
        )
        .map_err(|_| ClipError::missing())?,
    );
    let m = file.metadata().map_err(|_| ClipError::missing())?;
    if !m.is_file()
        || m.nlink() != 1
        || m.uid() != geteuid().as_raw()
        || m.ino() != s.st_ino
        || m.dev() != s.st_dev
    {
        return Err(ClipError::missing());
    }
    Ok((file, m))
}

impl Folder {
    fn ensure_current(&self) -> Result<()> {
        let root = walk_dirs(self.path.parent().ok_or_else(ClipError::missing)?, false)
            .map_err(|_| ClipError::missing())?;
        require_private(&root)?;
        let actual = child_dir(
            &root,
            self.path.file_name().ok_or_else(ClipError::missing)?,
            false,
        )
        .map_err(|_| ClipError::missing())?;
        require_private(&actual)?;
        let a = actual.metadata().map_err(ClipError::internal)?;
        let b = self.fd.metadata().map_err(ClipError::internal)?;
        if a.ino() != b.ino() || a.dev() != b.dev() {
            Err(ClipError::missing())
        } else {
            Ok(())
        }
    }
    fn valid_name(&self, name: &str) -> bool {
        !name.starts_with('.')
            && !name.contains('/')
            && !name.chars().any(char::is_control)
            && Path::new(name)
                .extension()
                .and_then(OsStr::to_str)
                .is_some_and(|s| s.eq_ignore_ascii_case(self.kind.suffix()))
    }
    fn open_clip(&self, name: &str) -> Result<(File, Clip)> {
        if !self.valid_name(name) {
            return Err(ClipError::missing());
        }
        let (mut file, m) = open_regular(&self.fd, name.as_ref())?;
        let mut head = [0u8; 24];
        file.read_exact(&mut head)
            .map_err(|_| ClipError::invalid("Empty or incomplete clip."))?;
        match self.kind {
            ClipKind::Screenshot => {
                png_header(&head)?;
                if m.len() > MAX_PNG_BYTES {
                    return Err(ClipError::invalid("PNG exceeds file size limit."));
                }
            }
            ClipKind::Video if &head[4..8] != b"ftyp" => {
                return Err(ClipError::invalid("Not an MP4 recording."));
            }
            _ => {}
        }
        file.rewind().map_err(ClipError::internal)?;
        let clip = Clip {
            path: self.path.join(name),
            kind: self.kind,
            size_bytes: m.len(),
            created: m.created().or_else(|_| m.modified()).unwrap_or(UNIX_EPOCH),
        };
        Ok((file, clip))
    }
}
impl ClipStore {
    pub fn open() -> Result<Self> {
        Self::at_data_home(&glib::user_data_dir())
    }
    fn at_data_home(base: &Path) -> Result<Self> {
        let (base, xdg) = data_home(base)?;
        let root = child_dir(&xdg, "tinyclips".as_ref(), true)?;
        require_private(&root)?;
        let dir = |name: &str, kind| -> Result<Folder> {
            let fd = child_dir(&root, name.as_ref(), true)?;
            require_private(&fd)?;
            Ok(Folder {
                path: base.join("tinyclips").join(name),
                fd: Arc::new(fd),
                kind,
            })
        };
        Ok(Self {
            images: dir("screenshots", ClipKind::Screenshot)?,
            videos: dir("videos", ClipKind::Video)?,
        })
    }
    pub fn screenshots_dir(&self) -> &Path {
        &self.images.path
    }
    pub fn videos_dir(&self) -> &Path {
        &self.videos.path
    }
    pub(crate) fn resolve(&self, path: &Path) -> Result<(File, Clip)> {
        let name = path
            .file_name()
            .and_then(OsStr::to_str)
            .ok_or_else(ClipError::missing)?;
        let dir = [&self.images, &self.videos]
            .into_iter()
            .find(|d| d.path.join(name).as_os_str() == path.as_os_str())
            .ok_or_else(ClipError::missing)?;
        dir.ensure_current()?;
        dir.open_clip(name)
    }
    pub fn load_png(&self, path: &Path) -> Result<Pixbuf> {
        let (file, clip) = self.resolve(path)?;
        if clip.kind != ClipKind::Screenshot {
            return Err(ClipError::invalid("The screenshot editor requires a PNG."));
        }
        Self::decode_png(file)
    }

    /// Import the *exact* local file returned by a user-initiated Screenshot portal response.
    /// The caller owns that grant: never accept a path from an agent, command line, or deep link here.
    /// No sibling is read and no access to the supplied location is retained.
    pub fn import_portal_png(&self, path: &Path) -> Result<PathBuf> {
        self.save_png(&Self::read_granted_png(path)?)
    }

    /// Save only the chosen pixels of the app's frozen frame from an authorized local stream.
    /// The caller owns the snapshot and its lifetime. Never accept its path from an agent.
    pub fn save_granted_region(&self, trusted_snapshot: &Path, region: Region) -> Result<PathBuf> {
        let frozen = Self::read_granted_png(trusted_snapshot)?;
        self.save_png(&region.crop(&frozen)?)
    }

    /// A single trusted portal grant or an ephemeral frame from an already-authorized local stream.
    /// This is intentionally not exposed through any automation command.
    pub(crate) fn read_granted_png(path: &Path) -> Result<Pixbuf> {
        let parent = path
            .parent()
            .ok_or_else(|| ClipError::invalid("Missing local frame directory."))?;
        let name = path
            .file_name()
            .ok_or_else(|| ClipError::invalid("Missing local frame name."))?;
        if parent.join(name).as_os_str() != path.as_os_str() {
            return Err(ClipError::invalid("Invalid local frame location."));
        }
        // A portal can return a logical home/runtime location on systems where /home is a symlink.
        // Resolve only those OS-supplied prefixes. Everything from there to the grant remains no-follow.
        let mut physical = parent.to_owned();
        for base in [glib::user_runtime_dir(), glib::home_dir()] {
            if base.parent().is_none() {
                continue;
            }
            if let Ok(suffix) = parent.strip_prefix(&base) {
                physical = base
                    .canonicalize()
                    .map_err(ClipError::internal)?
                    .join(suffix);
                break;
            }
        }
        let dir = walk_dirs(&physical, false)?;
        let (file, _) = open_regular(&dir, name)
            .map_err(|_| ClipError::invalid("Could not open the single granted local frame."))?;
        Self::decode_png(file)
    }

    fn decode_png(mut file: File) -> Result<Pixbuf> {
        if file.metadata().map_err(ClipError::internal)?.len() > MAX_PNG_BYTES {
            return Err(ClipError::invalid("PNG exceeds file size limit."));
        }
        // Force the PNG loader, not format sniffing. Validate the exact header supplied to it before allocation.
        let mut head = [0u8; 24];
        file.read_exact(&mut head).map_err(ClipError::internal)?;
        let (w, h) = png_header(&head)?;
        let loader = PixbufLoader::with_type("png").map_err(ClipError::internal)?;
        let decoded = (|| -> Result<()> {
            loader
                .write(&head)
                .map_err(|_| ClipError::invalid("Invalid PNG data."))?;
            let mut buf = [0u8; 32768];
            let mut remaining = MAX_PNG_BYTES - 24;
            loop {
                let n = file.read(&mut buf).map_err(ClipError::internal)?;
                if n == 0 {
                    break;
                }
                remaining = remaining
                    .checked_sub(n as u64)
                    .ok_or_else(|| ClipError::invalid("PNG exceeds file size limit."))?;
                loader
                    .write(&buf[..n])
                    .map_err(|_| ClipError::invalid("Invalid PNG data."))?;
            }
            Ok(())
        })();
        let closed = loader.close();
        decoded?;
        closed.map_err(|_| ClipError::invalid("Incomplete PNG data."))?;
        loader
            .pixbuf()
            .filter(|p| p.width() == w as i32 && p.height() == h as i32)
            .ok_or_else(|| ClipError::invalid("Could not decode PNG."))
    }
    pub fn list(&self, kind: Option<ClipKind>, limit: usize) -> Result<Vec<Clip>> {
        if !(1..=500).contains(&limit) {
            return Err(ClipError::invalid("limit must be between 1 and 500."));
        }
        let mut items = Vec::new();
        let sort = |items: &mut Vec<Clip>| {
            items.sort_unstable_by(|a, b| {
                b.created.cmp(&a.created).then_with(|| b.path.cmp(&a.path))
            })
        };
        for d in [&self.images, &self.videos] {
            if kind.is_some_and(|k| k != d.kind) {
                continue;
            }
            d.ensure_current()?;
            for entry in Dir::read_from(&*d.fd).map_err(ClipError::internal)? {
                let entry = entry.map_err(ClipError::internal)?;
                if let Some((_, clip)) = entry
                    .file_name()
                    .to_str()
                    .ok()
                    .and_then(|n| d.open_clip(n).ok())
                {
                    items.push(clip);
                }
                if items.len() == limit * 2 {
                    sort(&mut items);
                    items.truncate(limit);
                }
            }
        }
        sort(&mut items);
        items.truncate(limit);
        Ok(items)
    }
    pub fn save_png(&self, pixels: &Pixbuf) -> Result<PathBuf> {
        check_dimensions(pixels.width() as u32, pixels.height() as u32)?;
        let bytes = pixels
            .save_to_bufferv("png", &[])
            .map_err(ClipError::internal)?;
        self.images.ensure_current()?;
        let stamp = glib::DateTime::now_utc()
            .and_then(|t| t.format("%Y-%m-%d_%H-%M-%SZ"))
            .map_err(ClipError::internal)?;
        let name = format!("Screenshot-{stamp}-{}.png", glib::uuid_string_random());
        self.write_new(&name, &bytes)
    }
    fn write_new(&self, name: &str, bytes: &[u8]) -> Result<PathBuf> {
        let dir = &*self.images.fd;
        let partial = format!(".{name}.partial");
        let mut file = File::from(
            fs::openat(
                dir,
                partial.as_str(),
                OFlags::CREATE | OFlags::EXCL | OFlags::WRONLY | OFlags::CLOEXEC | OFlags::NOFOLLOW,
                Mode::from_raw_mode(0o600),
            )
            .map_err(ClipError::internal)?,
        );
        let result = (|| {
            file.write_all(bytes)
                .and_then(|_| file.sync_all())
                .map_err(ClipError::internal)?;
            fs::renameat_with(dir, partial.as_str(), dir, name, RenameFlags::NOREPLACE)
                .map_err(ClipError::internal)?;
            dir.sync_all().map_err(ClipError::internal)?;
            Ok(self.images.path.join(name))
        })();
        if result.is_err() {
            let _ = fs::unlinkat(dir, partial.as_str(), AtFlags::empty());
        }
        result
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{fs as stdfs, os::unix::fs::symlink};
    struct DataHome(PathBuf);
    impl DataHome {
        fn new() -> Self {
            use std::os::unix::fs::DirBuilderExt;
            let p = std::env::current_dir()
                .unwrap()
                .join(format!(".tinyclips-test-{}", glib::uuid_string_random()));
            stdfs::DirBuilder::new().mode(0o700).create(&p).unwrap();
            Self(p)
        }
    }
    impl Drop for DataHome {
        fn drop(&mut self) {
            let _ = stdfs::remove_dir_all(&self.0);
        }
    }
    fn sample() -> Pixbuf {
        let p = Pixbuf::new(gtk::gdk_pixbuf::Colorspace::Rgb, true, 8, 16, 8).unwrap();
        p.fill(0xfedcba80);
        p
    }
    #[test]
    fn private_publication_and_reopen() {
        let base = DataHome::new();
        let s = ClipStore::at_data_home(&base.0).unwrap();
        let src = sample();
        let path = s.save_png(&src).unwrap();
        let second = s.save_png(&src).unwrap();
        assert_ne!(path, second);
        assert_eq!(stdfs::metadata(&path).unwrap().mode() & 0o777, 0o600);
        assert_eq!(
            stdfs::metadata(s.screenshots_dir()).unwrap().mode() & 0o777,
            0o700
        );
        let before = stdfs::read(&path).unwrap();
        assert!(
            s.write_new(path.file_name().unwrap().to_str().unwrap(), b"different")
                .is_err()
        );
        assert_eq!(stdfs::read(&path).unwrap(), before);
        let reopened = ClipStore::at_data_home(&base.0).unwrap();
        assert_eq!(reopened.list(None, 20).unwrap().len(), 2);
        assert_eq!(
            reopened.load_png(&path).unwrap().read_pixel_bytes(),
            src.read_pixel_bytes()
        );
    }
    #[test]
    fn local_command_flow_returns_contract_results_and_preserves_original() {
        use crate::dispatch::dispatch_files;
        use serde_json::json;
        let home = DataHome::new();
        let s = ClipStore::at_data_home(&home.0).unwrap();
        let path = s.save_png(&sample()).unwrap();
        let original = stdfs::read(&path).unwrap();
        let listed =
            dispatch_files(&s, "library.list", &json!({"kind":"screenshot","limit":1})).unwrap();
        let item = &listed["items"][0];
        assert_eq!(item["path"], json!(path));
        assert_eq!(item["kind"], "screenshot");
        assert_eq!(item["sizeBytes"], original.len());
        assert!(item["createdAt"].as_str().unwrap().ends_with('Z'));
        let region = json!({"x":1,"y":0,"width":2,"height":1});
        let edited=dispatch_files(&s,"editor.annotate",&json!({"path":path,"annotations":[{"type":"redact","bounds":region,"redactionStyle":"solid"}]})).unwrap();
        assert_eq!(edited["annotationsApplied"], 1);
        assert_eq!(edited["sourcePath"], json!(path));
        let crop = dispatch_files(
            &s,
            "editor.crop",
            &json!({"path":edited["path"],"region":region}),
        )
        .unwrap();
        assert_eq!(
            (crop["width"].as_i64(), crop["height"].as_i64()),
            (Some(2), Some(1))
        );
        assert_eq!(crop["sourcePath"], edited["path"]);
        let p = s
            .load_png(Path::new(crop["path"].as_str().unwrap()))
            .unwrap();
        assert_eq!(p.read_pixel_bytes().as_ref(), &[0, 0, 0, 255, 0, 0, 0, 255]);
        assert!(
            dispatch_files(
                &s,
                "editor.crop",
                &json!({"path":path,"region":{"x":0,"y":0,"width":999999,"height":1}})
            )
            .is_err()
        );
        assert!(
            dispatch_files(
                &s,
                "editor.annotate",
                &json!({"path":path,"annotations":[]})
            )
            .is_err()
        );
        assert!(dispatch_files(&s, "library.list", &json!({"kind":"unknown"})).is_err());
        assert_eq!(stdfs::read(&path).unwrap(), original);
        assert_eq!(s.list(None, 10).unwrap().len(), 3);
    }
    #[test]
    fn rejects_traversal_symlinks_hardlinks_and_wrong_media() {
        let base = DataHome::new();
        let s = ClipStore::at_data_home(&base.0).unwrap();
        let p = s.save_png(&sample()).unwrap();
        let d = s.screenshots_dir();
        assert!(
            s.resolve(
                &d.join("..")
                    .join("screenshots")
                    .join(p.file_name().unwrap())
            )
            .is_err()
        );
        let outsider = base.0.join("private.png");
        stdfs::copy(&p, &outsider).unwrap();
        symlink(&outsider, d.join("link.png")).unwrap();
        stdfs::hard_link(&outsider, d.join("hard.png")).unwrap();
        stdfs::write(d.join("not.png"), b"not an image").unwrap();
        assert_eq!(s.list(None, 10).unwrap().len(), 1);
        assert!(s.resolve(&outsider).is_err());
        assert!(s.resolve(&d.join("link.png")).is_err());
        assert!(s.resolve(&d.join("hard.png")).is_err());
        stdfs::rename(d, base.0.join("moved")).unwrap();
        symlink(&base.0, d).unwrap();
        assert!(s.resolve(&p).is_err());
        assert!(s.save_png(&sample()).is_err());
        assert!(ClipStore::at_data_home(&base.0).is_err());
    }
    #[test]
    fn granted_region_publishes_only_selected_frozen_pixels() {
        let home = DataHome::new();
        let store = ClipStore::at_data_home(&home.0).unwrap();
        let snapshot = home.0.join("frozen.png");
        let src = sample();
        src.new_subpixbuf(4, 3, 1, 2).fill(0x123456ff);
        let bytes = src.save_to_bufferv("png", &[]).unwrap();
        stdfs::write(&snapshot, &bytes).unwrap();
        let r = Region {
            x: 4,
            y: 3,
            width: 2,
            height: 2,
        };
        assert!(
            store
                .save_granted_region(&snapshot, Region { x: 16, ..r })
                .is_err()
        );
        let saved = store.save_granted_region(&snapshot, r).unwrap();
        let items = store.list(None, 10).unwrap();
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].path, saved);
        let output = store.load_png(&saved).unwrap();
        assert_eq!((output.width(), output.height()), (2, 2));
        assert_eq!(
            output.read_pixel_bytes().as_ref(),
            &[0x12, 0x34, 0x56, 255, 0xfe, 0xdc, 0xba, 128].repeat(2)
        );
        assert_eq!(stdfs::read(&snapshot).unwrap(), bytes);
    }
    #[test]
    fn only_user_portal_import_accepts_an_external_local_png() {
        let home = DataHome::new();
        let store = ClipStore::at_data_home(&home.0).unwrap();
        let granted = home.0.join("granted");
        let src = sample();
        stdfs::write(&granted, src.save_to_bufferv("png", &[]).unwrap()).unwrap();
        assert!(store.load_png(&granted).is_err());
        let before = stdfs::read(&granted).unwrap();
        assert_eq!(
            ClipStore::read_granted_png(&granted)
                .unwrap()
                .read_pixel_bytes(),
            src.read_pixel_bytes()
        );
        assert_eq!(stdfs::read(&granted).unwrap(), before);
        assert!(store.list(None, 10).unwrap().is_empty());
        let copy = store.import_portal_png(&granted).unwrap();
        assert_eq!(copy.parent(), Some(store.screenshots_dir()));
        assert_eq!(
            store.load_png(&copy).unwrap().read_pixel_bytes(),
            src.read_pixel_bytes()
        );
        let alias = home.0.join("alias");
        symlink(&granted, &alias).unwrap();
        assert!(store.import_portal_png(&alias).is_err());
        let oversized = home.0.join("oversized");
        File::create(&oversized)
            .unwrap()
            .set_len(MAX_PNG_BYTES + 1)
            .unwrap();
        assert!(store.import_portal_png(&oversized).is_err());
        assert_eq!(store.list(None, 10).unwrap().len(), 1);
    }
    #[test]
    fn trusted_xdg_mapping_is_resolved_but_app_symlinks_are_not() {
        let home = DataHome::new();
        stdfs::create_dir_all(home.0.join("physical")).unwrap();
        symlink(home.0.join("physical"), home.0.join("home")).unwrap();
        let base = home.0.join("home/.var/app/id/data");
        let store = ClipStore::at_data_home(&base).unwrap();
        assert!(store.screenshots_dir().starts_with(home.0.join("physical")));
        assert!(store.save_png(&sample()).is_ok());
        stdfs::rename(base.join("tinyclips"), base.join("other")).unwrap();
        symlink(base.join("other"), base.join("tinyclips")).unwrap();
        assert!(ClipStore::at_data_home(&base).is_err());
        assert!(store.save_png(&sample()).is_err());
    }
    #[test]
    fn rejects_excessive_raster_before_decode() {
        let mut header = [0u8; 24];
        header[..16].copy_from_slice(PNG_PREFIX);
        header[16..20].copy_from_slice(&32000u32.to_be_bytes());
        header[20..24].copy_from_slice(&32000u32.to_be_bytes());
        assert!(png_header(&header).is_err());
    }
}
