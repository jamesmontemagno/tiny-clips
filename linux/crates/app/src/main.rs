mod agent_ui;
mod capture_bar;
mod capture_flow;
mod capture_panel;
mod elapsed;
mod portal;
mod preferences;
mod targets;
mod window;

use adw::prelude::*;
use gtk::{gio, glib};
use std::{
    cell::{Cell, RefCell},
    rc::Rc,
    time::Duration,
};

const APP_ID: &str = "com.refractored.tinyclips.Devel";
const APP_NAME: &str = "Tiny Clips (Preview)";
const USAGE: &str = "Usage: tinyclips-gnome [--self-test | --help]\n\
    No arguments: open the capture bar. Sharing begins only after user consent.\n\
    --self-test: map the window on a display and exit within 60 seconds.\n\
                 Checks GTK startup only; never requests a screenshot or starts Agent Control.";

fn main() -> glib::ExitCode {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    let self_test = match args.as_slice() {
        [] => false,
        [arg] if arg == "--self-test" => true,
        [arg] if arg == "--help" || arg == "-h" => {
            println!("{USAGE}");
            return glib::ExitCode::SUCCESS;
        }
        _ => {
            eprintln!("{USAGE}");
            return glib::ExitCode::new(2);
        }
    };

    if self_test {
        // Bound even a stuck toolkit/display initialization, before the GTK main loop runs.
        std::thread::spawn(|| {
            std::thread::sleep(Duration::from_secs(60));
            eprintln!("Self-test timed out; toolkit startup was not confirmed.");
            std::process::exit(1);
        });
    }
    glib::set_application_name(APP_NAME);
    let app = adw::Application::builder()
        .application_id(APP_ID)
        .flags(if self_test {
            gio::ApplicationFlags::NON_UNIQUE
        } else {
            gio::ApplicationFlags::empty()
        })
        .build();
    let mapped = Rc::new(Cell::new(false));
    let mapped_on_activate = Rc::clone(&mapped);
    let bar = Rc::new(RefCell::new(None::<Rc<capture_bar::CaptureBar>>));
    let removed = Rc::clone(&bar);
    app.connect_window_removed(move |_, window| {
        if removed
            .borrow()
            .as_ref()
            .is_some_and(|b| b.window.upcast_ref::<gtk::Window>() == window)
        {
            removed.take();
        }
    });
    let init_failed = Rc::new(Cell::new(false));
    let fail = Rc::clone(&init_failed);
    app.connect_activate(move |app| {
        if let Some(bar) = bar.borrow().as_ref() {
            bar.window.present();
            return;
        }
        let controller = match window::build(app, self_test) {
            Ok(controller) => controller,
            Err(err) => {
                eprintln!("Cannot start Tiny Clips: {err}");
                fail.set(true);
                app.quit();
                return;
            }
        };
        let window = controller.window.clone();
        bar.replace(Some(controller));
        if self_test {
            let mapped = Rc::clone(&mapped_on_activate);
            window.connect_map(move |window| {
                mapped.set(true);
                if let Some(app) = window.application() {
                    glib::idle_add_local_once(move || app.quit());
                }
            });
        }
        window.present();
    });
    let exit = app.run_with_args(&["tinyclips-gnome"]);
    if init_failed.get() {
        return glib::ExitCode::FAILURE;
    }
    if self_test && exit == glib::ExitCode::SUCCESS {
        if !mapped.get() {
            eprintln!("Self-test failed: the capture bar did not map.");
            return glib::ExitCode::FAILURE;
        }
        println!("Self-test passed: GTK/libadwaita window mapped; screenshot portal not tested.");
    }
    exit
}
