use crate::capture_flow::{Action, Stage};
use adw::prelude::*;
use gtk::gio;

pub struct PanelState<'a> {
    pub stage: Stage,
    pub elapsed: Option<u64>,
    pub countdown: u32,
    pub idle: bool,
    pub shared: Option<&'a str>,
    pub agent: bool,
}

pub struct Panel {
    pub root: gtk::Box,
    pub back: gtk::Button,
    pub screenshot: gtk::Button,
    pub record: gtk::Button,
    pub region: gtk::Button,
    pub screen: gtk::Button,
    pub window: gtk::Button,
    pub desktop_picker: gtk::Button,
    pub pause: gtk::Button,
    pub stop: gtk::Button,
    pub library: gtk::Button,
    pub share: gio::SimpleAction,
    pub release: gio::SimpleAction,
    pub notice: gtk::Label,
    stack: gtk::Stack,
    target_heading: gtk::Label,
    shared: gtk::Label,
    recording_state: gtk::Label,
    timer: gtk::Label,
    indicator: gtk::Image,
    footer: gtk::Box,
}

fn icon(name: &str, size: i32) -> gtk::Image {
    gtk::Image::builder()
        .icon_name(name)
        .pixel_size(size)
        .build()
}
fn section(title: &str) -> gtk::Label {
    let label = gtk::Label::builder().label(title).xalign(0.0).build();
    label.add_css_class("heading");
    label
}
fn page() -> gtk::Box {
    gtk::Box::builder()
        .orientation(gtk::Orientation::Vertical)
        .spacing(10)
        .margin_start(18)
        .margin_end(18)
        .margin_top(12)
        .margin_bottom(12)
        .build()
}
fn tiles() -> gtk::Box {
    gtk::Box::builder().spacing(8).homogeneous(true).build()
}
fn tile(name: &str, symbol: &str, subtitle: Option<&str>) -> gtk::Button {
    let button = gtk::Button::new();
    button.add_css_class("card");
    let content = gtk::Box::builder()
        .orientation(if subtitle.is_some() {
            gtk::Orientation::Horizontal
        } else {
            gtk::Orientation::Vertical
        })
        .spacing(8)
        .halign(gtk::Align::Center)
        .valign(gtk::Align::Center)
        .margin_top(12)
        .margin_bottom(12)
        .build();
    let image = icon(symbol, if subtitle.is_some() { 26 } else { 20 });
    if subtitle.is_some() {
        image.add_css_class(if symbol == "media-record-symbolic" {
            "error"
        } else {
            "accent"
        });
    }
    content.append(&image);
    let labels = gtk::Box::new(gtk::Orientation::Vertical, 2);
    let label = gtk::Label::with_mnemonic(name);
    label.set_mnemonic_widget(Some(&button));
    label.add_css_class("heading");
    if subtitle.is_some() {
        label.set_xalign(0.0);
    }
    labels.append(&label);
    if let Some(subtitle) = subtitle {
        let sub = gtk::Label::builder().label(subtitle).xalign(0.0).build();
        sub.add_css_class("caption");
        sub.add_css_class("dim-label");
        labels.append(&sub);
    }
    content.append(&labels);
    button.set_child(Some(&content));
    button.update_property(&[gtk::accessible::Property::Label(&name.replace('_', ""))]);
    button
}
fn set_text(label: &gtk::Label, text: &str) {
    if label.text().as_str() != text {
        label.set_label(text);
    }
}

impl Panel {
    pub fn new() -> Self {
        let back = gtk::Button::from_icon_name("go-previous-symbolic");
        back.set_tooltip_text(Some("Back to capture choices"));
        back.update_property(&[gtk::accessible::Property::Label("Back to capture choices")]);
        back.set_visible(false);
        let settings_menu = gio::Menu::new();
        settings_menu.append(Some("Settings…"), Some("win.settings"));
        settings_menu.append(Some("Quick guide"), Some("win.guide"));
        let sources = gio::Menu::new();
        sources.append(Some("Choose shared source…"), Some("win.share"));
        sources.append(Some("Release shared source"), Some("win.release"));
        settings_menu.append_section(Some("Reusable source"), &sources);
        let menu = gtk::MenuButton::builder()
            .icon_name("open-menu-symbolic")
            .menu_model(&settings_menu)
            .tooltip_text("Main menu")
            .build();
        menu.update_property(&[gtk::accessible::Property::Label("Main menu")]);
        let header = adw::HeaderBar::new();
        header.set_title_widget(Some(&adw::WindowTitle::new("Tiny Clips", "")));
        header.pack_start(&back);
        header.pack_end(&menu);
        let home = page();
        home.append(&section("New capture"));
        let actions = tiles();
        let screenshot = tile("_Screenshot", "camera-photo-symbolic", Some("Still image"));
        let record = tile("_Record", "media-record-symbolic", Some("Screen video"));
        actions.append(&screenshot);
        actions.append(&record);
        home.append(&actions);

        let targets = page();
        let target_heading = section("Screenshot");
        targets.append(&target_heading);
        let choices = tiles();
        let region = tile("_Region", "edit-select-all-symbolic", None);
        let screen = tile("_Screen", "video-display-symbolic", None);
        let window = tile("_Window", "window-new-symbolic", None);
        for b in [&region, &screen, &window] {
            choices.append(b);
        }
        targets.append(&choices);
        let desktop_picker = gtk::Button::with_label("Use desktop screenshot picker…");
        desktop_picker.add_css_class("flat");
        desktop_picker.add_css_class("caption");
        desktop_picker.set_halign(gtk::Align::Start);
        targets.append(&desktop_picker);

        let transport = page();
        let state = gtk::Box::builder()
            .spacing(8)
            .halign(gtk::Align::Center)
            .build();
        let indicator = icon("media-record-symbolic", 18);
        let recording_state = section("Recording");
        state.append(&indicator);
        state.append(&recording_state);
        transport.append(&state);
        let timer = gtk::Label::new(None);
        timer.add_css_class("title-1");
        timer.add_css_class("numeric");
        transport.append(&timer);
        let controls = gtk::Box::builder()
            .spacing(8)
            .halign(gtk::Align::Center)
            .build();
        let pause = gtk::Button::with_label("Pause");
        let stop = gtk::Button::with_label("Stop and save");
        stop.add_css_class("suggested-action");
        controls.append(&pause);
        controls.append(&stop);
        transport.append(&controls);
        let stack = gtk::Stack::builder()
            .vhomogeneous(false)
            .transition_type(gtk::StackTransitionType::Crossfade)
            .transition_duration(100)
            .build();
        for (p, n) in [
            (&home, "home"),
            (&targets, "targets"),
            (&transport, "transport"),
        ] {
            stack.add_named(p, Some(n));
        }
        let notice = gtk::Label::builder()
            .accessible_role(gtk::AccessibleRole::Status)
            .wrap(true)
            .max_width_chars(48)
            .xalign(0.0)
            .selectable(true)
            .margin_start(18)
            .margin_end(18)
            .margin_bottom(10)
            .visible(false)
            .build();
        let footer = gtk::Box::new(gtk::Orientation::Vertical, 0);
        footer.append(&gtk::Separator::new(gtk::Orientation::Horizontal));
        let nav = gtk::Box::builder()
            .spacing(6)
            .margin_start(10)
            .margin_end(14)
            .margin_top(4)
            .margin_bottom(4)
            .build();
        let library = gtk::Button::new();
        library.add_css_class("flat");
        let link = gtk::Box::new(gtk::Orientation::Horizontal, 7);
        link.append(&icon("folder-pictures-symbolic", 16));
        let lib_name = gtk::Label::with_mnemonic("_Clips Library");
        lib_name.set_mnemonic_widget(Some(&library));
        link.append(&lib_name);
        library.set_child(Some(&link));
        library.update_property(&[gtk::accessible::Property::Label("Clips Library")]);
        nav.append(&library);
        let shared = gtk::Label::builder()
            .xalign(1.0)
            .hexpand(true)
            .wrap(true)
            .build();
        shared.add_css_class("dim-label");
        shared.add_css_class("caption");
        nav.append(&shared);
        footer.append(&nav);
        let root = gtk::Box::new(gtk::Orientation::Vertical, 0);
        root.append(&header);
        root.append(&stack);
        root.append(&notice);
        root.append(&footer);
        Self {
            root,
            back,
            screenshot,
            record,
            region,
            screen,
            window,
            desktop_picker,
            pause,
            stop,
            library,
            share: gio::SimpleAction::new("share", None),
            release: gio::SimpleAction::new("release", None),
            notice,
            stack,
            target_heading,
            shared,
            recording_state,
            timer,
            indicator,
            footer,
        }
    }

    pub fn message(&self, msg: &str) {
        set_text(&self.notice, msg);
        self.notice.set_visible(!msg.is_empty());
    }

    pub fn update(&self, state: PanelState<'_>, focus: bool) {
        let PanelState {
            stage,
            elapsed,
            countdown,
            idle,
            shared,
            agent,
        } = state;
        let name = stage.page();
        let changed = self.stack.visible_child_name().as_deref() != Some(name);
        self.back.set_visible(matches!(stage, Stage::Targets(_)));
        self.back.set_sensitive(idle);
        self.footer.set_visible(name != "transport" || agent);
        self.library.set_visible(name != "transport");
        self.share.set_enabled(idle);
        self.release.set_enabled(idle && shared.is_some());
        let privacy = match (shared, agent) {
            (Some(s), true) => format!("{s} · Agent Control on"),
            (Some(s), false) => s.to_owned(),
            (None, true) => "Agent Control on".into(),
            (None, false) => String::new(),
        };
        set_text(&self.shared, &privacy);
        for button in [
            &self.screenshot,
            &self.record,
            &self.region,
            &self.screen,
            &self.window,
            &self.desktop_picker,
        ] {
            button.set_sensitive(idle);
        }
        if let Stage::Targets(action) = stage {
            set_text(
                &self.target_heading,
                match action {
                    Action::Screenshot => "Take a screenshot",
                    Action::Record => "Record a video",
                },
            );
            self.desktop_picker
                .set_visible(action == Action::Screenshot);
        }
        if name == "transport" {
            let (text, glyph) = match stage {
                Stage::Countdown => ("Starting in", "alarm-symbolic"),
                Stage::Starting => ("Starting recorder…", "media-record-symbolic"),
                Stage::Recording => ("Recording", "media-record-symbolic"),
                Stage::Paused => ("Paused", "media-playback-pause-symbolic"),
                _ => ("Saving recording…", "document-save-symbolic"),
            };
            set_text(&self.recording_state, text);
            if self.indicator.icon_name().as_deref() != Some(glyph) {
                self.indicator.set_icon_name(Some(glyph));
            }
            if stage == Stage::Recording {
                self.indicator.add_css_class("error");
            } else {
                self.indicator.remove_css_class("error");
            }
            let time = if stage == Stage::Countdown {
                countdown.to_string()
            } else {
                elapsed.map_or_else(String::new, |s| format!("{}:{:02}", s / 60, s % 60))
            };
            set_text(&self.timer, &time);
            self.timer.set_visible(!time.is_empty());
            let running = matches!(stage, Stage::Paused | Stage::Recording);
            self.pause.set_visible(running);
            self.stop.set_visible(running || stage == Stage::Countdown);
            self.pause.set_label(if stage == Stage::Paused {
                "Resume"
            } else {
                "Pause"
            });
            self.stop.set_label(if stage == Stage::Countdown {
                "Cancel countdown"
            } else {
                "Stop and save"
            });
        }
        if changed {
            self.stack.set_visible_child_name(name);
            if focus {
                match stage {
                    Stage::Home => {
                        self.screenshot.grab_focus();
                    }
                    Stage::Targets(_) => {
                        self.region.grab_focus();
                    }
                    Stage::Countdown | Stage::Recording | Stage::Paused => {
                        self.stop.grab_focus();
                    }
                    _ => (),
                }
            }
        }
    }
}

pub fn add_window_action(
    window: &adw::ApplicationWindow,
    name: &str,
    callback: impl Fn() + 'static,
) {
    let a = gio::SimpleAction::new(name, None);
    a.connect_activate(move |_, _| callback());
    window.add_action(&a);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    #[ignore = "requires a graphical GTK session; no capture or consent is requested"]
    fn gtk_pages_show_only_actions_for_the_current_stage() {
        adw::init().unwrap();
        let panel = Panel::new();
        let update = |stage, elapsed| {
            panel.update(
                PanelState {
                    stage,
                    elapsed,
                    countdown: 3,
                    idle: stage.page() != "transport",
                    shared: None,
                    agent: false,
                },
                false,
            )
        };
        update(Stage::Home, None);
        assert_eq!(panel.stack.visible_child_name().as_deref(), Some("home"));
        assert!(panel.screenshot.is_sensitive() && panel.record.is_sensitive());
        for (action, desktop_option) in [(Action::Screenshot, true), (Action::Record, false)] {
            update(Stage::Targets(action), None);
            assert_eq!(panel.stack.visible_child_name().as_deref(), Some("targets"));
            assert!(
                panel.region.is_sensitive()
                    && panel.screen.is_sensitive()
                    && panel.window.is_sensitive()
            );
            assert_eq!(
                panel.desktop_picker.property::<bool>("visible"),
                desktop_option
            );
        }
        update(Stage::Countdown, None);
        assert_eq!(panel.stop.label().as_deref(), Some("Cancel countdown"));
        assert!(!panel.pause.property::<bool>("visible"));
        update(Stage::Recording, Some(1801));
        assert_eq!(
            panel.stack.visible_child_name().as_deref(),
            Some("transport")
        );
        assert_eq!(panel.timer.text().as_str(), "30:01");
        assert_eq!(panel.pause.label().as_deref(), Some("Pause"));
        assert!(panel.stop.property::<bool>("visible"));
        update(Stage::Paused, Some(1801));
        assert_eq!(panel.pause.label().as_deref(), Some("Resume"));
        update(Stage::Saving, None);
        assert!(!panel.stop.property::<bool>("visible"));
        update(Stage::Home, None);
        assert_eq!(panel.stack.visible_child_name().as_deref(), Some("home"));
    }
}
