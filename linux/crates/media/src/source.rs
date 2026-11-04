use crate::{
    AudioOptions, MediaResult, Region, Status, background,
    engine::{self, VideoInput},
    recorder::Recorder,
    storage::uuid,
};
use ashpd::{
    WindowIdentifier,
    desktop::{
        PersistMode, Session,
        screencast::{CursorMode, Screencast, SourceType},
    },
};
use futures_util::{StreamExt, pin_mut};
use gtk::glib;
use std::{
    cell::{Cell, RefCell},
    os::fd::OwnedFd,
    path::{Path, PathBuf},
    rc::Rc,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SourceKind {
    Monitor,
    Window,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Target {
    /// Ephemeral grant ID, never a compositor name, stable device ID, or PipeWire node ID.
    pub id: String,
    pub kind: SourceKind,
    /// Last negotiated source raster, never inferred from the portal's logical `size`.
    pub width: Option<u32>,
    pub height: Option<u32>,
}
type SessionRef = Rc<Session<'static, Screencast<'static>>>;
type Size = Arc<Mutex<Option<(u32, u32)>>>;
struct SessionLease {
    session: SessionRef,
    valid: Arc<AtomicBool>,
    watch: Option<glib::JoinHandle<()>>,
    closed: bool,
}
impl Drop for SessionLease {
    fn drop(&mut self) {
        self.valid.store(false, Ordering::Release);
        if let Some(watch) = self.watch.take() {
            watch.abort();
        }
        if !self.closed {
            let session = Rc::clone(&self.session);
            glib::spawn_future_local(async move {
                let _ = session.close().await;
            });
        }
    }
}
struct Grant {
    id: String,
    kind: SourceKind,
    lease: SessionLease,
    proxy: Rc<Screencast<'static>>,
    node: u32,
    fd: Option<OwnedFd>,
    size: Size,
}
impl Grant {
    fn target(&self) -> Target {
        let size = *self.size.lock().unwrap();
        Target {
            id: self.id.clone(),
            kind: self.kind,
            width: size.map(|s| s.0),
            height: size.map(|s| s.1),
        }
    }
}
#[derive(Default)]
struct Inner {
    grant: RefCell<Option<Grant>>,
    choosing: Cell<bool>,
    generation: Cell<u64>,
    recorder: Recorder,
}

/// GTK-main-context facade. Clones share one ephemeral grant and recorder.
#[derive(Clone, Default)]
pub struct MediaService(Rc<Inner>);
impl MediaService {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn target(&self) -> Option<Target> {
        self.0
            .grant
            .borrow()
            .as_ref()
            .filter(|g| g.lease.valid.load(Ordering::Acquire))
            .map(Grant::target)
    }
    pub fn status(&self) -> Status {
        self.0.recorder.status()
    }

    /// A local capture may share the recorder/transport controls, but not publish its
    /// user-granted source to agents. Dropping this service closes its own portal session.
    pub fn single_capture(&self) -> Self {
        Self(Rc::new(Inner {
            recorder: self.0.recorder.clone(),
            ..Inner::default()
        }))
    }

    /// Interactive only. `None` allows either source for explicit, reusable sharing.
    /// Never call from the agent endpoint, startup, retries, or a missing-grant fallback.
    pub async fn authorize(
        &self,
        parent: &gtk::Window,
        requested: Option<SourceKind>,
    ) -> MediaResult<Target> {
        if self.status().active() || self.0.choosing.replace(true) {
            return Err("Capture or source selection is active".into());
        }
        struct Choosing<'a>(&'a Cell<bool>);
        impl Drop for Choosing<'_> {
            fn drop(&mut self) {
                self.0.set(false);
            }
        }
        let _choosing = Choosing(&self.0.choosing);
        let generation = self.0.generation.get();
        let parent = WindowIdentifier::from_native(parent)
            .await
            .ok_or("Cannot identify the capture window on Wayland")?;
        let proxy = Rc::new(Screencast::new().await.map_err(portal_error)?);
        let allowed = match requested {
            Some(SourceKind::Monitor) => SourceType::Monitor.into(),
            Some(SourceKind::Window) => SourceType::Window.into(),
            None => SourceType::Monitor | SourceType::Window,
        };
        let types = proxy.available_source_types().await.map_err(portal_error)? & allowed;
        if types.is_empty() {
            return Err("The desktop does not offer the requested capture source.".into());
        }
        let modes = proxy.available_cursor_modes().await.map_err(portal_error)?;
        let cursor = if modes.contains(CursorMode::Embedded) {
            CursorMode::Embedded
        } else {
            CursorMode::Hidden
        };
        let session = Rc::new(proxy.create_session().await.map_err(portal_error)?);
        let valid = Arc::new(AtomicBool::new(true));
        let id = uuid();
        let mut lease = SessionLease {
            session: session.clone(),
            valid: valid.clone(),
            watch: None,
            closed: false,
        };
        // Subscribe before starting or returning a target: no grant may outlive a missed Closed.
        let (ready, receiving) = async_channel::bounded(1);
        let listening = session.clone();
        let alive = valid.clone();
        let weak = Rc::downgrade(&self.0);
        let watching = id.clone();
        lease.watch = Some(glib::spawn_future_local(async move {
            let stream = match listening.receive_closed().await {
                Ok(stream) => stream,
                Err(e) => {
                    let _ = ready.try_send(Err(portal_error(e)));
                    return;
                }
            };
            pin_mut!(stream);
            let _ = ready.try_send(Ok(()));
            let _ = stream.next().await;
            alive.store(false, Ordering::Release);
            if let Some(inner) = weak.upgrade() {
                let mut grant = inner.grant.borrow_mut();
                if let Some(g) = grant.as_mut().filter(|g| g.id == watching) {
                    g.lease.closed = true;
                    g.fd.take();
                }
            }
        }));
        receiving
            .recv()
            .await
            .map_err(|_| "Session monitor could not start")??;
        proxy
            .select_sources(&session, cursor, types, false, None, PersistMode::DoNot)
            .await
            .map_err(portal_error)?
            .response()
            .map_err(portal_error)?;
        let response = proxy
            .start(&session, Some(&parent))
            .await
            .map_err(portal_error)?
            .response()
            .map_err(portal_error)?;
        let [stream] = response.streams() else {
            return Err("Portal did not grant exactly one source".into());
        };
        let kind = match stream.source_type() {
            Some(SourceType::Monitor) => SourceKind::Monitor,
            Some(SourceType::Window) => SourceKind::Window,
            _ => {
                return Err(
                    "Cannot verify whether the authorized source is a monitor or window".into(),
                );
            }
        };
        if requested.is_some_and(|expected| kind != expected) {
            return Err(
                "The desktop returned a different source type. Nothing was captured.".into(),
            );
        }
        let node = stream.pipe_wire_node_id();
        let size = Arc::new(Mutex::new(None));
        let fd = proxy
            .open_pipe_wire_remote(&session)
            .await
            .map_err(portal_error)?;
        let verify = VideoInput::PipeWire {
            fd,
            node,
            dimensions: size.clone(),
        };
        let verify_valid = valid.clone();
        background(move || engine::verify(verify, verify_valid)).await?;
        let fd = proxy
            .open_pipe_wire_remote(&session)
            .await
            .map_err(portal_error)?;
        if !valid.load(Ordering::Acquire) || generation != self.0.generation.get() {
            return Err("Source was revoked during authorization".into());
        }
        let grant = Grant {
            id,
            kind,
            lease,
            proxy,
            node,
            fd: Some(fd),
            size,
        };
        let target = grant.target();
        self.0.grant.replace(Some(grant));
        Ok(target)
    }
    pub async fn revoke(&self) -> MediaResult<()> {
        self.0
            .generation
            .set(self.0.generation.get().wrapping_add(1));
        let old = self.0.grant.take();
        if let Some(mut g) = old {
            g.lease.valid.store(false, Ordering::Release);
            if let Some(w) = g.lease.watch.take() {
                w.abort();
            }
            if !g.lease.closed {
                g.lease.session.close().await.map_err(portal_error)?;
                g.lease.closed = true;
            }
        }
        Ok(())
    }
    async fn source(
        &self,
        id: &str,
        region: Option<Region>,
    ) -> MediaResult<(VideoInput, Arc<AtomicBool>)> {
        if self.0.choosing.get() {
            return Err("Source selection in progress".into());
        }
        let (session, proxy, node, fd, valid, dimensions) = {
            let mut g = self.0.grant.borrow_mut();
            let g = g
                .as_mut()
                .filter(|g| g.id == id && g.lease.valid.load(Ordering::Acquire))
                .ok_or("Source is not authorized; select a source in the app")?;
            if let Some(region) = region {
                region.validate(*g.size.lock().unwrap())?;
            }
            (
                g.lease.session.clone(),
                g.proxy.clone(),
                g.node,
                g.fd.take(),
                g.lease.valid.clone(),
                g.size.clone(),
            )
        };
        // dup() would share the previous PipeWire protocol socket. Each new pipeline
        // needs an independent remote for this existing session, not a new picker.
        let fd = match fd {
            Some(fd) => fd,
            None => proxy.open_pipe_wire_remote(&session).await.map_err(|e| {
                valid.store(false, Ordering::Release);
                portal_error(e)
            })?,
        };
        if !valid.load(Ordering::Acquire) {
            return Err("ScreenCast source is no longer authorized".into());
        }
        Ok((
            VideoInput::PipeWire {
                fd,
                node,
                dimensions,
            },
            valid,
        ))
    }
    pub async fn snapshot(&self, id: &str, dir: &Path) -> MediaResult<PathBuf> {
        self.snapshot_region(id, dir, None).await
    }
    pub async fn snapshot_region(
        &self,
        id: &str,
        dir: &Path,
        region: Option<Region>,
    ) -> MediaResult<PathBuf> {
        let (input, valid) = self.source(id, region).await?;
        let dir = dir.to_owned();
        background(move || engine::snapshot(input, valid, &dir, region).map(|v| v.0)).await
    }
    pub async fn start(&self, id: &str, dir: &Path, audio: AudioOptions) -> MediaResult<()> {
        self.start_region(id, dir, audio, None).await
    }
    pub async fn start_region(
        &self,
        id: &str,
        dir: &Path,
        audio: AudioOptions,
        region: Option<Region>,
    ) -> MediaResult<()> {
        if self.status().active() {
            return Err("Recording already active".into());
        }
        let (input, valid) = self.source(id, region).await?;
        self.0
            .recorder
            .start(input, valid, dir, audio, 10, region)
            .await
    }
    pub async fn pause(&self) -> MediaResult<()> {
        self.0.recorder.pause().await
    }
    pub async fn resume(&self) -> MediaResult<()> {
        self.0.recorder.resume().await
    }
    pub async fn stop(&self) -> MediaResult<PathBuf> {
        self.0.recorder.stop().await
    }
}
fn portal_error(error: ashpd::Error) -> String {
    format!("ScreenCast portal: {error}")
}
