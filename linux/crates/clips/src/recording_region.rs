use super::Fields;
use crate::{
    ClipError, ClipStore, Region, Result,
    geometry::{Fit, ImageDrag},
    library::window,
};
use gtk::{
    cairo, gdk,
    gdk_pixbuf::{Colorspace, Pixbuf},
    gio, glib,
    prelude::*,
};
use std::{
    cell::{Cell, RefCell},
    path::Path,
    rc::Rc,
};

struct Picker {
    fields: Fields,
    area: gtk::DrawingArea,
    confirm: gtk::Button,
    message: gtk::Label,
    width: i32,
    height: i32,
    edited: Cell<bool>,
    drag: Cell<Option<ImageDrag>>,
    guide: Cell<Option<Region>>,
}
impl Picker {
    fn selection(&self) -> Option<Region> {
        if self.drag.get().is_some() {
            self.guide.get()
        } else if self.edited.get() {
            self.fields.region().validate(self.width, self.height).ok()
        } else {
            None
        }
    }
    fn refresh(&self) {
        let region = self.selection();
        self.confirm
            .set_sensitive(self.drag.get().is_none() && region.is_some());
        if let Some(r) = region {
            self.message.set_text(&format!(
                "Selected {} × {} source pixels at ({}, {}).",
                r.width, r.height, r.x, r.y
            ));
        } else if self.edited.get() {
            self.message
                .set_text("Choose a nonempty region wholly inside this frame.");
        }
        self.area.queue_draw();
    }
    fn begin(&self, x: f64, y: f64) {
        self.guide.set(None);
        self.drag.set(
            Fit::new(
                self.area.width(),
                self.area.height(),
                self.width,
                self.height,
            )
            .and_then(|f| ImageDrag::begin(f, x, y)),
        );
        self.refresh();
    }
    fn motion(&self, dx: f64, dy: f64) {
        if let Some(d) = self.drag.get() {
            self.guide.set(d.region(dx, dy));
            self.refresh();
        }
    }
    fn finish(&self, dx: f64, dy: f64) {
        if let Some(d) = self.drag.take() {
            match d.region(dx, dy) {
                Some(r) => {
                    self.fields.set(r);
                    self.edited.set(true);
                }
                None => {
                    self.edited.set(false);
                    self.message
                        .set_text("No area selected. Drag a box or enter region coordinates.");
                }
            }
            self.guide.set(None);
            self.refresh();
        }
    }
}
struct CloseOnDrop(adw::Window);
impl Drop for CloseOnDrop {
    fn drop(&mut self) {
        self.0.destroy();
    }
}

/// Select a screenshot or recording region in source pixels on a frozen, authorized stream frame.
/// Call on GTK's main context with the app's own trusted snapshot path, never an agent argument.
/// This only reads that file. The caller owns its lifetime and must revalidate the live source
/// before use. Cancellation, Escape, and closing the picker return `Ok(None)`.
pub async fn choose_region(
    parent: &impl IsA<gtk::Window>,
    snapshot_path: &Path,
) -> Result<Option<Region>> {
    let path = snapshot_path.to_owned();
    let (bytes, width, height, stride, alpha) = gio::spawn_blocking(move || -> Result<_> {
        let p = ClipStore::read_granted_png(&path)?;
        Ok((
            p.read_pixel_bytes(),
            p.width(),
            p.height(),
            p.rowstride(),
            p.has_alpha(),
        ))
    })
    .await
    .map_err(|_| ClipError::internal("Could not load the frozen preview."))??;
    let pixels = Pixbuf::from_bytes(&bytes, Colorspace::Rgb, alpha, 8, width, height, stride);
    let (win, body) = window(parent, "Choose capture region", 780, 560);
    let _guard = CloseOnDrop(win.clone());
    win.set_modal(true);
    let area = gtk::DrawingArea::builder()
        .hexpand(true)
        .vexpand(true)
        .content_width(180)
        .content_height(140)
        .build();
    area.set_cursor_from_name(Some("crosshair"));
    area.update_property(&[gtk::accessible::Property::Label(
        "Frozen capture source. Drag a rectangle or use the pixel fields below.",
    )]);
    body.append(&area);
    let help = gtk::Label::new(Some(&format!(
        "Frozen source: {width} × {height} pixels. Drag a box, or enter its bounds below."
    )));
    help.set_wrap(true);
    help.set_xalign(0.);
    body.append(&help);
    let grid = gtk::Grid::builder()
        .column_spacing(8)
        .row_spacing(4)
        .build();
    body.append(&grid);
    let fields = Fields::new(&grid);
    fields.reset(&pixels);
    let actions = gtk::Box::new(gtk::Orientation::Horizontal, 8);
    body.append(&actions);
    let confirm = gtk::Button::with_mnemonic("_Use region");
    confirm.add_css_class("suggested-action");
    confirm.set_sensitive(false);
    actions.append(&confirm);
    let cancel = gtk::Button::with_mnemonic("_Cancel");
    actions.append(&cancel);
    let message = gtk::Label::new(Some("No region selected."));
    message.set_wrap(true);
    message.set_xalign(0.);
    body.append(&message);
    let state = Rc::new(Picker {
        fields,
        area,
        confirm,
        message,
        width,
        height,
        edited: Cell::new(false),
        drag: Cell::new(None),
        guide: Cell::new(None),
    });
    let weak = Rc::downgrade(&state);
    let mut pattern = None;
    state.area.set_draw_func(move |_, c, w, h| {
        let Some(s) = weak.upgrade() else {
            return;
        };
        let Some(f) = Fit::new(w, h, width, height) else {
            return;
        };
        c.set_source_rgb(0.12, 0.12, 0.12);
        let _ = c.paint();
        c.translate(f.left, f.top);
        c.scale(f.scale, f.scale);
        c.rectangle(0., 0., width.into(), height.into());
        c.clip();
        if let Some(p) = &pattern {
            let _ = c.set_source(p);
        } else {
            c.set_source_pixbuf(&pixels, 0., 0.);
            pattern = Some(c.source());
        }
        let _ = c.paint();
        if let Some(r) = s.selection() {
            c.rectangle(0., 0., width.into(), height.into());
            c.rectangle(r.x.into(), r.y.into(), r.width.into(), r.height.into());
            c.set_fill_rule(cairo::FillRule::EvenOdd);
            c.set_source_rgba(0., 0., 0., 0.45);
            let _ = c.fill();
            c.rectangle(r.x.into(), r.y.into(), r.width.into(), r.height.into());
            c.set_line_width(2.5 / f.scale);
            c.set_source_rgb(0., 0., 0.);
            let _ = c.stroke_preserve();
            c.set_line_width(1.5 / f.scale);
            c.set_dash(&[5. / f.scale, 4. / f.scale], 0.);
            c.set_source_rgb(1., 1., 1.);
            let _ = c.stroke();
        }
    });
    for field in &state.fields.0 {
        let weak = Rc::downgrade(&state);
        field.connect_value_changed(move |_| {
            if let Some(s) = weak.upgrade() {
                s.edited.set(true);
                s.refresh();
            }
        });
    }
    let gesture = gtk::GestureDrag::new();
    gesture.set_button(1);
    let weak = Rc::downgrade(&state);
    gesture.connect_drag_begin(move |_, x, y| {
        if let Some(s) = weak.upgrade() {
            s.begin(x, y);
        }
    });
    let weak = Rc::downgrade(&state);
    gesture.connect_drag_update(move |_, x, y| {
        if let Some(s) = weak.upgrade() {
            s.motion(x, y);
        }
    });
    let weak = Rc::downgrade(&state);
    gesture.connect_drag_end(move |_, x, y| {
        if let Some(s) = weak.upgrade() {
            s.finish(x, y);
        }
    });
    let weak = Rc::downgrade(&state);
    gesture.connect_cancel(move |_, _| {
        if let Some(s) = weak.upgrade() {
            s.drag.set(None);
            s.guide.set(None);
            s.refresh();
        }
    });
    state.area.add_controller(gesture);
    let chosen = Rc::new(Cell::new(None));
    let choose = chosen.clone();
    let window = win.downgrade();
    let weak = Rc::downgrade(&state);
    state.confirm.connect_clicked(move |_| {
        if let (Some(s), Some(w)) = (weak.upgrade(), window.upgrade()) {
            for spin in &s.fields.0 {
                spin.update();
            }
            if s.drag.get().is_none() {
                if let Some(r) = s.selection() {
                    choose.set(Some(r));
                    w.close();
                }
            }
        }
    });
    let window = win.downgrade();
    cancel.connect_clicked(move |_| {
        if let Some(w) = window.upgrade() {
            w.close();
        }
    });
    let keys = gtk::EventControllerKey::new();
    keys.set_propagation_phase(gtk::PropagationPhase::Capture);
    let window = win.downgrade();
    keys.connect_key_pressed(move |_, key, _, _| {
        if key == gdk::Key::Escape {
            if let Some(w) = window.upgrade() {
                w.close();
            }
            glib::Propagation::Stop
        } else {
            glib::Propagation::Proceed
        }
    });
    win.add_controller(keys);
    let focus = state.fields.0[0].clone();
    let result = gio::GioFuture::new(&win, move |w, _, reply| {
        let reply = Rc::new(RefCell::new(Some(reply)));
        let complete = move || {
            if let Some(tx) = reply.borrow_mut().take() {
                tx.resolve(chosen.take());
            }
        };
        let close = complete.clone();
        w.connect_close_request(move |_| {
            close();
            glib::Propagation::Proceed
        });
        let hide = complete.clone();
        w.connect_unmap(move |_| hide());
        w.connect_destroy(move |_| complete());
        w.present();
        focus.grab_focus();
    })
    .await;
    drop(state);
    Ok(result)
}
