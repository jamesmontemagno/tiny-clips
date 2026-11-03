use crate::{APP_NAME, portal};
use adw::prelude::*;
use gtk::{gdk, glib};
use std::{cell::RefCell, rc::Rc};

pub fn build(app: &adw::Application, self_test: bool) -> adw::ApplicationWindow {
    let body = gtk::Box::builder()
        .orientation(gtk::Orientation::Vertical)
        .spacing(12)
        .margin_top(16)
        .margin_bottom(16)
        .margin_start(18)
        .margin_end(18)
        .build();
    let intro = gtk::Label::builder()
        .label("Developer diagnostic. Ask the desktop for a screenshot, then preview only the image it returns.")
        .wrap(true)
        .xalign(0.0)
        .build();
    body.append(&intro);
    let button = gtk::Button::with_mnemonic("_Request screenshot…");
    button.set_halign(gtk::Align::Start);
    body.append(&button);
    let status = gtk::Label::builder()
        .accessible_role(gtk::AccessibleRole::Status)
        .label("Ready. No screenshot requested.")
        .wrap(true)
        .xalign(0.0)
        .build();
    body.append(&status);
    let picture = gtk::Picture::builder()
        .alternative_text("Preview of the screenshot returned by the desktop portal")
        .content_fit(gtk::ContentFit::Contain)
        .vexpand(true)
        .visible(false)
        .build();
    body.append(&picture);

    let content = gtk::Box::new(gtk::Orientation::Vertical, 0);
    content.append(&adw::HeaderBar::new());
    content.append(&body);
    let window = adw::ApplicationWindow::builder()
        .application(app)
        .title(APP_NAME)
        .default_width(520)
        .default_height(400)
        .content(&content)
        .build();
    if self_test {
        button.set_sensitive(false);
        status.set_label("Toolkit startup self-test. Screenshot portal disabled.");
        return window;
    }

    let pending = Rc::new(RefCell::new(None::<glib::JoinHandle<()>>));
    let active_request = Rc::clone(&pending);
    window.connect_close_request(move |_| {
        // Drop the Wayland parent handle while its native surface still exists.
        if let Some(task) = active_request.take() {
            task.abort();
        }
        glib::Propagation::Proceed
    });
    let weak_window = window.downgrade();
    button.connect_clicked(move |button| {
        let Some(window) = weak_window.upgrade() else {
            return;
        };
        button.set_sensitive(false);
        picture.set_paintable(None::<&gdk::Paintable>);
        picture.set_visible(false);
        status.set_label("Waiting for the desktop Screenshot portal…");
        let button = button.clone();
        let status = status.clone();
        let picture = picture.clone();
        pending.replace(Some(glib::spawn_future_local(async move {
            match portal::request_preview(&window).await {
                Ok(texture) => {
                    picture.set_paintable(Some(&texture));
                    picture.set_visible(true);
                    status.set_label("Screenshot received. Ready for another request.");
                }
                Err(message) => status.set_label(&message),
            }
            button.set_sensitive(true);
            button.grab_focus();
        })));
    });
    window
}
