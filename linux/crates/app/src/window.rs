use crate::{
    APP_NAME,
    agent_ui::AgentControl,
    capture_bar::CaptureBar,
    capture_flow::{Action, Area},
    capture_panel::{Panel, add_window_action},
    preferences::Preferences,
};
use adw::prelude::*;
use gtk::glib;
use std::{cell::Cell, future::Future, rc::Rc, time::Duration};
use tinyclips_automation::ApiError;
use tinyclips_clips::ClipStore;
use tinyclips_media::MediaService;

fn preferences(prefs: &Preferences, agent: &AgentControl) -> adw::PreferencesDialog {
    let dialog = adw::PreferencesDialog::builder().title("Settings").build();
    let page = adw::PreferencesPage::new();
    let audio = adw::PreferencesGroup::builder()
        .title("Recording audio")
        .build();
    for (title, key) in [
        ("Microphone", "record-microphone"),
        ("System output audio", "record-audio"),
    ] {
        let row = adw::SwitchRow::builder().title(title).build();
        prefs.bind(key, &row, "active");
        audio.add(&row);
    }
    page.add(&audio);
    let countdown = adw::PreferencesGroup::builder().title("Countdown").build();
    let enabled = adw::SwitchRow::builder()
        .title("Count down before recording")
        .build();
    prefs.bind("video-countdown-enabled", &enabled, "active");
    countdown.add(&enabled);
    let seconds = adw::SpinRow::with_range(1.0, 10.0, 1.0);
    seconds.set_title("Duration (seconds)");
    prefs.bind("video-countdown-duration", &seconds, "value");
    enabled
        .bind_property("active", &seconds, "sensitive")
        .sync_create()
        .build();
    countdown.add(&seconds);
    page.add(&countdown);
    let automation = adw::PreferencesGroup::builder().title("Agent Control").description("Pair only trusted clients. To let them capture, choose a shared source from the main menu. Local captures do not grant agents access.").build();
    automation.add(&agent.panel);
    page.add(&automation);
    dialog.add(&page);
    dialog
}
fn action<F, T>(button: &gtk::Button, owner: &Rc<CaptureBar>, perform: F)
where
    F: Fn(Rc<CaptureBar>) -> T + 'static,
    T: Future<Output = Result<(), ApiError>> + 'static,
{
    let weak = Rc::downgrade(owner);
    let perform = Rc::new(perform);
    button.connect_clicked(move |_| {
        let Some(bar) = weak.upgrade() else {
            return;
        };
        let job = perform(bar.clone());
        glib::spawn_future_local(async move {
            if let Err(err) = job.await {
                bar.message(&err.message);
            }
            bar.refresh();
        });
    });
}

pub fn build(app: &adw::Application, self_test: bool) -> Result<Rc<CaptureBar>, String> {
    let store = ClipStore::open().map_err(|e| e.to_string())?;
    let prefs = Preferences::load()?;
    let agent = AgentControl::new();
    let settings = preferences(&prefs, &agent);
    let panel = Panel::new();
    let window = adw::ApplicationWindow::builder()
        .application(app)
        .title(APP_NAME)
        .default_width(416)
        .content(&panel.root)
        .build();
    let bar = Rc::new(CaptureBar {
        window,
        agent,
        store,
        prefs,
        media: MediaService::new(),
        settings,
        panel,
        intent: Cell::new(None),
        local_recording: Default::default(),
        last_error: Default::default(),
        pending: Cell::new(false),
        seconds: Cell::new(0),
        elapsed: Default::default(),
    });
    if self_test {
        bar.panel.root.set_sensitive(false);
        bar.message("GTK startup check. No capture, permission request, or agent listener.");
        return Ok(bar);
    }
    for (button, intent) in [
        (&bar.panel.screenshot, Some(Action::Screenshot)),
        (&bar.panel.record, Some(Action::Record)),
        (&bar.panel.back, None),
    ] {
        let weak = Rc::downgrade(&bar);
        button.connect_clicked(move |_| {
            if let Some(bar) = weak.upgrade() {
                bar.choose(intent);
            }
        });
    }
    for (button, area) in [
        (&bar.panel.region, Area::Region),
        (&bar.panel.screen, Area::Screen),
        (&bar.panel.window, Area::Window),
    ] {
        action(button, &bar, move |b| async move { b.capture(area).await });
    }
    action(&bar.panel.desktop_picker, &bar, |b| async move {
        b.desktop_screenshot().await
    });
    action(&bar.panel.pause, &bar, |b| async move {
        b.pause().await.map(|_| ())
    });
    action(&bar.panel.stop, &bar, |b| async move {
        b.stop().await.map(|_| ())
    });
    action(&bar.panel.library, &bar, |b| async move { b.library() });
    for (setting, share) in [(&bar.panel.share, true), (&bar.panel.release, false)] {
        let weak = Rc::downgrade(&bar);
        setting.connect_activate(move |_, _| {
            if let Some(b) = weak.upgrade() {
                glib::spawn_future_local(async move {
                    let result = if share {
                        b.share().await
                    } else {
                        b.revoke().await
                    };
                    if let Err(e) = result {
                        b.message(&e.message);
                    }
                    b.refresh();
                });
            }
        });
        bar.window.add_action(setting);
    }
    let weak = Rc::downgrade(&bar);
    add_window_action(&bar.window, "settings", move || {
        if let Some(bar) = weak.upgrade() {
            bar.open_settings();
        }
    });
    let weak = Rc::downgrade(&bar);
    add_window_action(&bar.window, "guide", move || {
        if let Some(bar) = weak.upgrade() {
            bar.open_guide();
        }
    });
    let key = gtk::EventControllerKey::new();
    let weak = Rc::downgrade(&bar);
    key.connect_key_pressed(move |_, key, _, _| {
        if let Some(b) = weak.upgrade().filter(|b| {
            key == gtk::gdk::Key::Escape
                && b.intent.get().is_some()
                && !b.pending.get()
                && b.window.visible_dialog().is_none()
        }) {
            b.choose(None);
            return glib::Propagation::Stop;
        }
        glib::Propagation::Proceed
    });
    bar.window.add_controller(key);
    bar.connect_agent();
    bar.refresh();
    let weak = Rc::downgrade(&bar);
    bar.window.connect_close_request(move |_| {
        if let Some(bar) = weak.upgrade() {
            if bar.pending.get() || bar.media.status().active() {
                bar.message(
                    "Finish the current operation or stop and save before closing Tiny Clips.",
                );
                return glib::Propagation::Stop;
            }
            bar.agent.disable();
        }
        glib::Propagation::Proceed
    });
    let weak = Rc::downgrade(&bar);
    glib::timeout_add_local(Duration::from_millis(350), move || {
        if let Some(bar) = weak.upgrade() {
            bar.refresh();
            glib::ControlFlow::Continue
        } else {
            glib::ControlFlow::Break
        }
    });
    let owner = Rc::clone(&bar);
    glib::spawn_future_local(async move {
        if let Err(e) = owner.recover().await {
            owner.message(&e.message);
        }
    });
    Ok(bar)
}
