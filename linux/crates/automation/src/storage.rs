use std::{
    fs::{self, DirBuilder, File, Metadata, OpenOptions, Permissions},
    io::{self, Read, Write},
    os::{
        fd::AsRawFd,
        unix::{
            fs::{DirBuilderExt, FileTypeExt, MetadataExt, OpenOptionsExt, PermissionsExt},
            net::UnixListener,
        },
    },
    path::{Component, Path, PathBuf},
};

use crate::{ApiError, Clients};

pub(crate) fn uid() -> u32 {
    unsafe { libc::geteuid() }
}

fn unsafe_path() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "Expected a private, user-owned, non-symlink path",
    )
}

pub(crate) fn random_key() -> io::Result<String> {
    let mut bytes = [0u8; 32];
    getrandom::fill(&mut bytes).map_err(|_| io::Error::other("OS randomness unavailable"))?;
    Ok(hex::encode(bytes))
}

fn clean(path: &Path) -> bool {
    path.is_absolute()
        && path
            .components()
            .all(|c| matches!(c, Component::RootDir | Component::Normal(_)))
}

fn real_dir(path: &Path) -> io::Result<Metadata> {
    if !clean(path) || path.canonicalize()? != path {
        return Err(unsafe_path());
    }
    let meta = fs::symlink_metadata(path)?;
    if !meta.is_dir() {
        return Err(unsafe_path());
    }
    Ok(meta)
}

// Some distributions make /home a root-managed link to /var/home. Resolve only
// system-controlled ancestor links, never a link at or beneath a user-writable
// prefix. Socket discovery deliberately uses the literal private XDG runtime path.
pub(crate) fn state_path(path: &Path) -> io::Result<PathBuf> {
    if !clean(path) || path.parent().is_none() {
        return Err(unsafe_path());
    }
    let mut result = PathBuf::from("/");
    let mut system = true;
    let mut parts = path
        .components()
        .filter_map(|c| match c {
            Component::Normal(s) => Some(s),
            _ => None,
        })
        .peekable();
    while let Some(part) = parts.next() {
        result.push(part);
        let mut m = match fs::symlink_metadata(&result) {
            Ok(m) => m,
            Err(e) if e.kind() == io::ErrorKind::NotFound => {
                system = false;
                continue;
            }
            Err(e) => return Err(e),
        };
        if m.is_symlink() {
            if !system || m.uid() != 0 || parts.peek().is_none() {
                return Err(unsafe_path());
            }
            result = result.canonicalize()?;
            m = fs::metadata(&result)?;
        }
        system &= m.uid() == 0 && m.mode() & 0o022 == 0;
    }
    Ok(result)
}

pub(crate) fn private_dir(path: &Path, create: bool) -> io::Result<()> {
    if !clean(path) {
        return Err(unsafe_path());
    }
    if create {
        // Walk existing ancestors before creation; never populate a symlink target.
        for ancestor in path.ancestors().collect::<Vec<_>>().into_iter().rev() {
            match fs::symlink_metadata(ancestor) {
                Ok(m) if !m.is_dir() => return Err(unsafe_path()),
                Ok(_) => {}
                Err(e) if e.kind() == io::ErrorKind::NotFound => {
                    if let Err(e) = DirBuilder::new().mode(0o700).create(ancestor) {
                        if e.kind() != io::ErrorKind::AlreadyExists {
                            return Err(e);
                        }
                    }
                    real_dir(ancestor)?;
                }
                Err(e) => return Err(e),
            }
        }
    }
    let m = real_dir(path)?;
    if m.uid() != uid() || m.mode() & 0o777 != 0o700 {
        return Err(unsafe_path());
    }
    Ok(())
}

pub(crate) fn check_input(path: &Path, roots: &[PathBuf]) -> Result<(), ApiError> {
    let denied = || {
        ApiError::new(
            "invalid_params",
            "Expected an existing file in the Tiny Clips library.",
        )
    };
    if !clean(path) || !roots.iter().any(|r| path.starts_with(r)) {
        return Err(denied());
    }
    let m = fs::symlink_metadata(path).map_err(|_| ApiError::code("not_found"))?;
    if !m.is_file()
        || m.uid() != uid()
        || m.nlink() != 1
        || path.canonicalize().ok().as_deref() != Some(path)
    {
        return Err(denied());
    }
    Ok(())
}

fn private_file(path: &Path, options: &mut OpenOptions) -> io::Result<File> {
    let file = options
        .mode(0o600)
        .custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK)
        .open(path)?;
    let m = file.metadata()?;
    if !m.is_file() || m.nlink() != 1 || m.uid() != uid() || m.mode() & 0o777 != 0o600 {
        return Err(unsafe_path());
    }
    Ok(file)
}

pub(crate) struct PrivateDir {
    pub path: PathBuf,
    _handle: File,
}

impl PrivateDir {
    pub fn open(path: &Path) -> io::Result<Self> {
        let before = real_dir(path)?;
        let handle = OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW)
            .open(path)?;
        let after = handle.metadata()?;
        if (before.dev(), before.ino()) != (after.dev(), after.ino())
            || after.uid() != uid()
            || after.mode() & 0o777 != 0o700
        {
            return Err(unsafe_path());
        }
        Ok(Self {
            path: PathBuf::from(format!("/proc/self/fd/{}", handle.as_raw_fd())),
            _handle: handle,
        })
    }
}

pub(crate) struct Bound {
    path: PathBuf,
    inode: (u64, u64),
    _lock: File,
    _directory: PrivateDir,
}

impl Drop for Bound {
    fn drop(&mut self) {
        if fs::symlink_metadata(&self.path)
            .is_ok_and(|m| m.file_type().is_socket() && (m.dev(), m.ino()) == self.inode)
        {
            let _ = fs::remove_file(&self.path);
        }
    }
}

pub(crate) fn bind(runtime: &Path, id: &str) -> io::Result<(UnixListener, PathBuf, Bound)> {
    if !matches!(
        id,
        "com.refractored.tinyclips.Devel" | "com.refractored.tinyclips"
    ) {
        return Err(unsafe_path());
    }
    private_dir(runtime, false)?;
    let app = runtime.join("app");
    match DirBuilder::new().mode(0o700).create(&app) {
        Ok(()) => {}
        Err(e) if e.kind() == io::ErrorKind::AlreadyExists => {}
        Err(e) => return Err(e),
    }
    let m = real_dir(&app)?;
    if m.uid() != uid() || m.mode() & 0o022 != 0 {
        return Err(unsafe_path());
    }
    let dir = app.join(id);
    private_dir(&dir, true)?;
    // Pin the verified directory. AF_UNIX has no bindat; /proc/self/fd lets
    // bind/cleanup address that inode even if the directory name is replaced.
    let directory = PrivateDir::open(&dir)?;
    let pinned = &directory.path;
    let lock = private_file(
        &pinned.join("automation.lock"),
        OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false),
    )?;
    if unsafe { libc::flock(lock.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) } != 0 {
        return Err(io::Error::last_os_error());
    }
    let path = pinned.join("automation.sock");
    match fs::symlink_metadata(&path) {
        Ok(m) if m.file_type().is_socket() && m.uid() == uid() => {
            // Only an abandoned socket can be replaced; never unlink a live listener.
            let probe = socket2::Socket::new(socket2::Domain::UNIX, socket2::Type::STREAM, None)?;
            probe.set_nonblocking(true)?;
            match probe.connect(&socket2::SockAddr::unix(&path)?) {
                Err(e) if e.kind() == io::ErrorKind::ConnectionRefused => fs::remove_file(&path)?,
                _ => return Err(io::Error::from(io::ErrorKind::AddrInUse)),
            }
        }
        Ok(_) => return Err(unsafe_path()),
        Err(e) if e.kind() == io::ErrorKind::NotFound => {}
        Err(e) => return Err(e),
    }
    let listener = UnixListener::bind(&path)?;
    let m = fs::symlink_metadata(&path)?;
    let bound = Bound {
        path: path.clone(),
        inode: (m.dev(), m.ino()),
        _lock: lock,
        _directory: directory,
    };
    fs::set_permissions(&path, Permissions::from_mode(0o600))?;
    listener.set_nonblocking(true)?;
    Ok((listener, dir.join("automation.sock"), bound))
}

pub(crate) fn load(dir: &Path) -> io::Result<Clients> {
    let file = match private_file(
        &dir.join("automation-clients.json"),
        OpenOptions::new().read(true),
    ) {
        Ok(f) => f,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(Clients::new()),
        Err(e) => return Err(e),
    };
    if file.metadata()?.len() > 512 * 1024 {
        return Err(io::Error::from(io::ErrorKind::InvalidData));
    }
    let clients: Clients = serde_json::from_reader(file.take(512 * 1024))?;
    Ok(clients)
}

pub(crate) fn save(dir: &Path, clients: &Clients) -> io::Result<()> {
    let tmp = dir.join(format!(".clients-{}", random_key()?));
    let mut file = private_file(&tmp, OpenOptions::new().write(true).create_new(true))?;
    let result = (|| {
        serde_json::to_writer(&file, clients)?;
        file.write_all(b"\n")?;
        file.sync_all()?;
        fs::rename(&tmp, dir.join("automation-clients.json"))?;
        File::open(dir)?.sync_all()
    })();
    if result.is_err() {
        let _ = fs::remove_file(tmp);
    }
    result
}

pub(crate) struct Audit {
    file: File,
    path: PathBuf,
}

impl Audit {
    pub(crate) fn open(dir: &Path) -> io::Result<Self> {
        let path = dir.join("automation-audit.jsonl");
        let file = private_file(&path, OpenOptions::new().append(true).create(true))?;
        Ok(Self { file, path })
    }

    pub(crate) fn record(
        &mut self,
        client: Option<&str>,
        command: &str,
        outcome: &str,
    ) -> io::Result<()> {
        if self.file.metadata()?.len() >= 1024 * 1024 {
            fs::rename(&self.path, self.path.with_extension("jsonl.1"))?;
            self.file = private_file(&self.path, OpenOptions::new().append(true).create_new(true))?;
        }
        let ts = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs();
        let entry = serde_json::json!({"unixTime":ts, "uid":uid(), "clientId":client, "command":command, "outcome":outcome});
        let mut line = serde_json::to_vec(&entry)?;
        line.push(b'\n');
        self.file.write_all(&line)
    }
}
