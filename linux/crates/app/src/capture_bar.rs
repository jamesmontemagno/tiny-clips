use crate::{
    agent_ui::AgentControl,
    capture_flow::{Action, Area, Source, Stage},
    capture_panel::{Panel, PanelState},
    portal,
    preferences::Preferences,
    targets,
};
use adw::prelude::*;
use gtk::{gdk, glib};
use serde_json::{Value, json};
use std::{
    cell::{Cell, RefCell},
    path::PathBuf,
    rc::Rc,
    time::{Duration, Instant},
};
use tinyclips_automation::{ApiError, Call};
use tinyclips_clips::{ClipStore, open_editor, open_library};
use tinyclips_media::{AudioOptions, MediaService, Phase, Region, SourceKind, Target};

const COMMANDS: &[&str] = &[
    "app.status",
    "app.open",
    "recording.status",
    "capture.targets",
    "capture.screenshot",
    "recording.startVideo",
    "recording.togglePause",
    "recording.stop",
    "settings.get",
    "settings.set",
];

type Reply = Result<Value, ApiError>;
fn busy() -> ApiError {
    ApiError::new("busy", "Finish the current operation first.")
}
fn failure(e: impl ToString) -> ApiError {
    ApiError::new("internal", e.to_string())
}
fn clip_error(e: tinyclips_clips::ClipError) -> ApiError {
    ApiError::new(e.code, e.message)
}

struct Lock<'a>(&'a Cell<bool>);
impl Drop for Lock<'_> {
    fn drop(&mut self) {
        self.0.set(false);
    }
}

pub struct CaptureBar {
    pub window: adw::ApplicationWindow,
    pub agent: Rc<AgentControl>,
    pub store: ClipStore,
    pub prefs: Preferences,
    pub media: MediaService,
    pub settings: adw::PreferencesDialog,
    pub panel: Panel,
    pub intent: Cell<Option<Action>>,
    pub local_recording: RefCell<Option<MediaService>>,
    pub last_error: RefCell<Option<String>>,
    pub pending: Cell<bool>,
    pub seconds: Cell<u32>,
    pub elapsed: RefCell<crate::elapsed::Elapsed>,
}

impl CaptureBar {
    fn idle(&self) -> bool {
        !self.pending.get() && !self.media.status().active()
    }
    fn lock_idle(&self) -> Result<Lock<'_>, ApiError> {
        if !self.idle() {
            return Err(busy());
        }
        self.pending.set(true);
        Ok(Lock(&self.pending))
    }
    fn state_name(&self) -> &'static str {
        if self.seconds.get() > 0 {
            return "countdown";
        }
        match self.media.status().phase {
            Phase::Idle | Phase::Failed => "idle",
            Phase::Recording => "recording",
            Phase::Paused => "paused",
            Phase::Starting | Phase::Finalizing => "processing",
        }
    }
    pub fn refresh(&self) {
        if self.agent.active() && self.settings.is_mapped() {
            self.agent.refresh_clients();
        }
        let status = self.media.status();
        if !status.active() {
            self.local_recording.take();
        }
        if *self.last_error.borrow() != status.error {
            if let Some(err) = &status.error {
                self.message(&format!("Recording stopped: {err}"));
            }
            self.last_error.replace(status.error);
        }
        let stage = Stage::resolve(self.intent.get(), self.seconds.get(), status.phase);
        if stage.page() == "transport" {
            self.intent.set(None);
        }
        let source = self.media.target().map(|t| match t.kind {
            SourceKind::Window => "Window shared",
            SourceKind::Monitor => "Screen shared",
        });
        let view = PanelState {
            stage,
            elapsed: self
                .elapsed
                .borrow_mut()
                .sample(status.phase, Instant::now()),
            countdown: self.seconds.get(),
            idle: self.idle(),
            shared: source,
            agent: self.agent.active(),
        };
        self.panel.update(
            view,
            self.window.is_active() && self.window.visible_dialog().is_none(),
        );
    }
    pub fn message(&self, msg: &str) {
        self.panel.message(msg);
    }
    pub fn choose(&self, action: Option<Action>) {
        if self.idle() {
            self.intent.set(action);
            self.message("");
            self.refresh();
        }
    }
    pub fn open_settings(&self) {
        self.window.present();
        self.settings.present(Some(&self.window));
    }
    pub fn open_guide(&self) {
        let dialog = adw::AlertDialog::builder().heading("Capturing with Tiny Clips")
            .body("Choose Screenshot or Record, then Region, Screen, or Window. GNOME asks for the source if needed. Region lets you draw on a frozen frame.\n\nPause or stop from the capture bar. Find saved images and videos in Clips Library.\n\nFor unattended capture, choose a reusable shared source from the main menu, then enable and pair Agent Control in Settings.").build();
        dialog.add_response("close", "Close");
        dialog.set_close_response("close");
        self.window.present();
        dialog.present(Some(&self.window));
    }

    pub async fn share(&self) -> Result<(), ApiError> {
        let _guard = self.lock_idle()?;
        self.message(
            "Select a screen or window in GNOME. Only that source will be available to Tiny Clips.",
        );
        self.media
            .authorize(self.window.upcast_ref(), None)
            .await
            .map_err(|m| ApiError::new("permission_required", m))?;
        self.message("Source shared. You can take a screenshot or start recording.");
        Ok(())
    }
    pub async fn revoke(&self) -> Result<(), ApiError> {
        let _guard = self.lock_idle()?;
        self.media.revoke().await.map_err(failure)?;
        self.message("Source access released.");
        Ok(())
    }

    fn edit_saved(&self, path: &std::path::Path) -> Result<(), ApiError> {
        self.intent.set(None);
        self.message("Screenshot saved to Clips Library.");
        open_editor(&self.window, &self.store, path).map_err(clip_error)
    }

    pub async fn desktop_screenshot(&self) -> Result<(), ApiError> {
        let _guard = self.lock_idle()?;
        self.message("");
        let file = portal::request_image(&self.window)
            .await
            .map_err(|e| ApiError::new("permission_required", e))?;
        let store = self.store.clone();
        let path = gtk::gio::spawn_blocking(move || store.import_portal_png(&file))
            .await
            .map_err(|_| failure("Image import worker failed"))?
            .map_err(clip_error)?;
        self.edit_saved(&path)
    }

    pub async fn capture(&self, area: Area) -> Result<(), ApiError> {
        let _guard = self.lock_idle()?;
        let action = self.intent.get().ok_or_else(busy)?;
        self.message("");
        let shared = self.media.target();
        let (source, id, temporary) = match area.source(shared.as_ref().map(|t| t.kind)) {
            Source::Shared => (
                self.media.clone(),
                shared
                    .ok_or_else(|| {
                        ApiError::new(
                            "permission_required",
                            "The shared source is no longer available.",
                        )
                    })?
                    .id,
                false,
            ),
            Source::OneCapture(kind) => {
                let source = self.media.single_capture();
                self.message(match kind {
                    SourceKind::Window => "Choose a window in the desktop picker.",
                    SourceKind::Monitor => "Choose a screen in the desktop picker.",
                });
                let target = source
                    .authorize(self.window.upcast_ref(), Some(kind))
                    .await
                    .map_err(|e| ApiError::new("permission_required", e))?;
                (source, target.id, true)
            }
        };
        self.message("");
        let mut region = None;
        if area == Area::Region {
            use std::os::unix::fs::PermissionsExt;
            let scratch = tempfile::Builder::new()
                .prefix("tinyclips-region-")
                .permissions(std::fs::Permissions::from_mode(0o700))
                .tempdir()
                .map_err(failure)?;
            let frame = source
                .snapshot(&id, scratch.path())
                .await
                .map_err(failure)?;
            // Selection and screenshot export must use the exact same frozen pixels.
            if temporary && action == Action::Screenshot {
                source.revoke().await.map_err(failure)?;
            }
            let Some(rect) = tinyclips_clips::choose_region(&self.window, &frame)
                .await
                .map_err(clip_error)?
            else {
                self.message("Region selection canceled.");
                return Ok(());
            };
            if action == Action::Screenshot {
                let store = self.store.clone();
                let out = gtk::gio::spawn_blocking(move || store.save_granted_region(&frame, rect))
                    .await
                    .map_err(|_| failure("Crop worker failed"))?
                    .map_err(clip_error)?;
                return self.edit_saved(&out);
            }
            region = Some(Region {
                x: rect.x.try_into().map_err(failure)?,
                y: rect.y.try_into().map_err(failure)?,
                width: rect.width.try_into().map_err(failure)?,
                height: rect.height.try_into().map_err(failure)?,
            });
        }
        if action == Action::Screenshot {
            let out = source
                .snapshot(&id, self.store.screenshots_dir())
                .await
                .map_err(failure)?;
            drop(source);
            self.edit_saved(&out)
        } else {
            self.begin_recording(&source, &id, region, None).await?;
            if temporary {
                self.local_recording.replace(Some(source));
            }
            self.intent.set(None);
            Ok(())
        }
    }

    async fn start_recording(
        &self,
        id: &str,
        region: Option<Region>,
        call: Option<&Call>,
    ) -> Reply {
        let _guard = self.lock_idle()?;
        self.begin_recording(&self.media, id, region, call).await
    }

    async fn begin_recording(
        &self,
        source: &MediaService,
        id: &str,
        region: Option<Region>,
        call: Option<&Call>,
    ) -> Reply {
        self.message("");
        for n in (1..=self.prefs.countdown()).rev() {
            self.seconds.set(n);
            self.refresh();
            glib::timeout_future(Duration::from_secs(1)).await;
            if self.seconds.get() == 0 {
                return Err(ApiError::new("busy", "Countdown canceled."));
            }
            if call.is_some_and(|c| !c.active()) {
                self.seconds.set(0);
                return Err(ApiError::new(
                    "unauthorized",
                    "Agent request is no longer active.",
                ));
            }
        }
        self.seconds.set(0);
        let audio = AudioOptions {
            microphone: self.prefs.microphone(),
            system: self.prefs.system_audio(),
        };
        source
            .start_region(id, self.store.videos_dir(), audio, region)
            .await
            .map_err(|e| {
                ApiError::new(
                    if source.target().is_some() {
                        "internal"
                    } else {
                        "permission_required"
                    },
                    e,
                )
            })?;
        Ok(json!({"state":"recording","kind":"video"}))
    }
    pub async fn stop(&self) -> Reply {
        if self.seconds.replace(0) > 0 {
            self.message("Countdown canceled.");
            return Ok(json!({"state":"idle"}));
        }
        if !matches!(self.media.status().phase, Phase::Recording | Phase::Paused) {
            return Err(busy());
        }
        let path = self.media.stop().await.map_err(failure)?;
        self.message("Recording saved to Clips Library.");
        Ok(json!({"state":"idle","path":path}))
    }
    pub async fn pause(&self) -> Reply {
        match self.media.status().phase {
            Phase::Recording => self.media.pause().await.map_err(failure)?,
            Phase::Paused => self.media.resume().await.map_err(failure)?,
            _ => return Err(busy()),
        }
        Ok(json!({"state":self.state_name()}))
    }
    fn media_error(&self, detail: String) -> ApiError {
        ApiError::new(
            if self.media.target().is_some() {
                "internal"
            } else {
                "permission_required"
            },
            detail,
        )
    }
    pub fn library(&self) -> Result<(), ApiError> {
        open_library(&self.window, &self.store).map_err(clip_error)
    }
    pub fn connect_agent(self: &Rc<Self>) {
        let owner = Rc::downgrade(self);
        self.agent.connect(
            &self.window,
            glib::user_config_dir().join("tinyclips-agent-control"),
            vec![
                self.store.screenshots_dir().into(),
                self.store.videos_dir().into(),
            ],
            COMMANDS
                .iter()
                .chain(tinyclips_clips::COMMANDS)
                .copied()
                .collect(),
            move |call| {
                let Some(bar) = owner.upgrade() else {
                    return;
                };
                glib::spawn_future_local(async move {
                    if !call.active() {
                        return;
                    }
                    let result = bar.execute(&call).await;
                    bar.refresh();
                    call.reply(result);
                });
            },
        );
    }

    async fn execute(&self, call: &Call) -> Reply {
        let p = &call.params;
        match call.command.as_str() {
            "app.status" => Ok(
                json!({"app":"TinyClips","platform":"linux","version":env!("CARGO_PKG_VERSION"),"build":"flatpak","recording":self.state_name(),"busy":!self.idle(),"automationEnabled":self.agent.active(),"screenRecordingPermission":self.media.target().is_some()}),
            ),
            "app.open" => {
                match p["window"].as_str() {
                    Some("settings") => self.open_settings(),
                    Some("guide") => self.open_guide(),
                    Some("library") => self.library()?,
                    _ => {
                        return Err(ApiError::new(
                            "invalid_params",
                            "window must be settings, guide, or library.",
                        ));
                    }
                }
                Ok(json!({"opened":true}))
            }
            "recording.status" => {
                let s = self.media.status();
                let mut r = json!({"state":self.state_name(),"busy":!self.idle()});
                if s.active() {
                    r["kind"] = json!("video");
                }
                if let Some(seconds) = self.elapsed.borrow_mut().sample(s.phase, Instant::now()) {
                    r["elapsedSeconds"] = json!(seconds);
                }
                if let Some(e) = s.error {
                    r["error"] = json!(e);
                }
                Ok(r)
            }
            "settings.get" => {
                self.prefs
                    .get(p, self.store.screenshots_dir(), self.store.videos_dir())
            }
            "settings.set" => self.prefs.set(p),
            "capture.targets" => self.targets(p),
            "recording.togglePause" => self.pause().await,
            "recording.stop" => self.stop().await,
            "capture.screenshot" | "recording.startVideo" => {
                let video = call.command == "recording.startVideo";
                let live = self.media.target();
                let region = targets::resolve(
                    p,
                    live.as_ref().map(|t| targets::Target {
                        kind: if t.kind == SourceKind::Monitor {
                            targets::Kind::Display
                        } else {
                            targets::Kind::Window
                        },
                        id: &t.id,
                        physical_size: physical_size(t),
                    }),
                    video,
                )?;
                let target =
                    live.ok_or_else(|| ApiError::new("permission_required", "No shared source."))?;
                let region = region.map(|r| Region {
                    x: r.x,
                    y: r.y,
                    width: r.width,
                    height: r.height,
                });
                if video {
                    return self.start_recording(&target.id, region, Some(call)).await;
                }
                let _guard = self.lock_idle()?;
                let path = self
                    .media
                    .snapshot_region(&target.id, self.store.screenshots_dir(), region)
                    .await
                    .map_err(|e| self.media_error(e))?;
                let (width, height) = gtk::gdk_pixbuf::Pixbuf::file_info(&path)
                    .map(|(_, w, h)| (w, h))
                    .ok_or_else(|| failure("Saved PNG has no readable dimensions."))?;
                Ok(
                    json!({"path":path,"width":width,"height":height,"mode":p.get("mode").and_then(Value::as_str).unwrap_or("screen")}),
                )
            }
            name if tinyclips_clips::COMMANDS.contains(&name) => {
                tinyclips_clips::dispatch(&self.store, &self.window, name, p)
                    .await
                    .map_err(clip_error)
            }
            _ => Err(ApiError::new(
                "not_supported",
                "Not available in the Linux preview.",
            )),
        }
    }
    fn targets(&self, p: &Value) -> Reply {
        let kind = match p.get("kind") {
            Some(v) => v.as_str(),
            None => Some("all"),
        };
        if !matches!(kind, Some("all" | "displays" | "windows")) {
            return Err(ApiError::new(
                "invalid_params",
                "kind must be all, displays or windows.",
            ));
        }
        let mut out = json!({});
        for (list, k) in [
            ("displays", SourceKind::Monitor),
            ("windows", SourceKind::Window),
        ] {
            if kind != Some("all") && kind != Some(list) {
                continue;
            }
            let items: Vec<_> = self
                .media
                .target()
                .filter(|t| t.kind == k)
                .into_iter()
                .map(|t| {
                    let mut item = json!({"id":t.id,"regionSupported":physical_size(&t).is_some()});
                    if k == SourceKind::Monitor {
                        item["name"] = json!("Shared screen");
                    } else {
                        item["title"] = json!("Shared window");
                    }
                    if let (Some(w), Some(h)) = (t.width, t.height) {
                        item["width"] = json!(w);
                        item["height"] = json!(h);
                    }
                    item
                })
                .collect();
            out[list] = json!(items);
        }
        Ok(out)
    }
    pub async fn recover(&self) -> Result<(), ApiError> {
        let _guard = self.lock_idle()?;
        let mut saved = 0;
        let mut skipped = 0;
        for dir in recovery_dirs(self.store.videos_dir())? {
            match tinyclips_media::recover(&dir).await {
                Ok(_) => saved += 1,
                Err(_) => skipped += 1,
            }
        }
        if saved + skipped > 0 {
            self.message(&format!("Recovered {saved} recording(s); {skipped} incomplete or unavailable take(s) retained."));
        }
        Ok(())
    }
}

// The portal does not identify a monitor by GDK object. Only claim a physical mapping when
// there is a single monitor and its scaled geometry agrees with the negotiated stream raster.
fn physical_size(t: &Target) -> Option<(u32, u32)> {
    if t.kind != SourceKind::Monitor {
        return None;
    }
    let monitors = gdk::Display::default()?.monitors();
    if monitors.n_items() != 1 {
        return None;
    }
    let monitor = monitors.item(0)?.downcast::<gdk::Monitor>().ok()?;
    let bounds = monitor.geometry();
    let scale = monitor.scale();
    let (w, h) = (t.width?, t.height?);
    ((f64::from(bounds.width()) * scale).round() == f64::from(w)
        && (f64::from(bounds.height()) * scale).round() == f64::from(h))
    .then_some((w, h))
}
fn recovery_dirs(root: &std::path::Path) -> Result<Vec<PathBuf>, ApiError> {
    let mut dirs = Vec::new();
    for entry in root.read_dir().map_err(failure)? {
        let entry = entry.map_err(failure)?;
        if entry.file_type().map_err(failure)?.is_dir()
            && entry
                .file_name()
                .to_str()
                .and_then(|s| s.strip_prefix(".take-"))
                .is_some_and(glib::uuid_string_is_valid)
        {
            dirs.push(entry.path());
        }
    }
    Ok(dirs)
}
