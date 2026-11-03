mod portal;
mod window;

use adw::prelude::*;
use gtk::{gio, glib};
use std::{cell::Cell, rc::Rc, time::Duration};

const APP_ID: &str = "com.refractored.tinyclips.Devel";
const APP_NAME: &str = "Tiny Clips Portal Probe";
const USAGE: &str = "Usage: tinyclips-portal-probe [--self-test | --help]\n\
    No arguments: open the developer probe. Only its button requests a screenshot.\n\
    --self-test: map the probe window on a display and exit within 15 seconds.\n\
                 Tests GTK/libadwaita startup only; never requests a screenshot.";

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
            std::thread::sleep(Duration::from_secs(15));
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
    app.connect_activate(move |app| {
        if let Some(window) = app.active_window() {
            window.present();
            return;
        }
        let window = window::build(app, self_test);
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
    let exit = app.run_with_args(&["tinyclips-portal-probe"]);
    if self_test && exit == glib::ExitCode::SUCCESS {
        if !mapped.get() {
            eprintln!("Self-test failed: the probe window did not map.");
            return glib::ExitCode::FAILURE;
        }
        println!("Self-test passed: GTK/libadwaita window mapped; screenshot portal not tested.");
    }
    exit
}
