use super::{Document, Fields};
use crate::{
    ClipError, ClipStore, Region, Result,
    editor_tools::{MAX_POINTS, Point, Tool, format_points, parse_points},
    geometry::{Fit, ImageDrag},
};
use adw::prelude::*;
use gtk::{accessible::Property, cairo, gdk, gdk_pixbuf::Pixbuf, glib, pango};
use std::{cell::RefCell, collections::VecDeque, rc::Rc};
#[path = "editor_options.rs"]
mod options;
use options::Options;

fn hint(k: Tool) -> &'static str {
    match k {
        Tool::Crop => "Drag around the area to keep.",
        Tool::Opaque => "Drag over sensitive details to cover them with solid black.",
        Tool::Blur | Tool::Pixelate => {
            "Drag over an area. Use opaque redaction for sensitive details."
        }
        Tool::Line | Tool::Arrow => "Drag from the start point to the endpoint.",
        Tool::Pen => "Drag to draw a freehand stroke.",
        Tool::Text => "Drag a text box, then enter your text.",
        Tool::Emoji => "Drag a box, then enter an emoji.",
        Tool::Number => "Drag to place a numbered callout.",
        Tool::Rectangle | Tool::Ellipse => "Drag to draw a shape.",
    }
}

// Inspector fields are inputs, not a draft. Only a completed drag or Set selection stages geometry.
#[derive(Debug, PartialEq)]
enum Selection {
    Box(Region),
    Path(String),
}
impl Selection {
    fn checked(self, k: Tool, w: i32, h: i32) -> Result<Self> {
        match &self {
            Self::Box(r) => {
                r.validate(w, h)?;
                if k == Tool::Crop && (r.width, r.height) == (w, h) {
                    return Err(ClipError::invalid(
                        "Choose a smaller area to crop the image.",
                    ));
                }
            }
            Self::Path(text) => {
                let p = parse_points(text)?;
                if p.iter()
                    .any(|&(x, y)| !(0.0..=w as f64).contains(&x) || !(0.0..=h as f64).contains(&y))
                {
                    return Err(ClipError::invalid(
                        "Points must be inside the image, in source pixels.",
                    ));
                }
                if p.iter().all(|point| *point == p[0]) || (k != Tool::Pen && p.len() != 2) {
                    return Err(ClipError::invalid(if k == Tool::Pen {
                        "A stroke needs at least two distinct points."
                    } else {
                        "A line needs exactly two distinct endpoints."
                    }));
                }
            }
        }
        Ok(self)
    }
}

struct Drag {
    anchor: ImageDrag,
    points: Vec<Point>,
    overflow: bool,
}
impl Drag {
    fn update(&mut self, tool: Tool, dx: f64, dy: f64) {
        let Some(p) = self.anchor.point(dx, dy) else {
            return;
        };
        if self.overflow || self.points.last() == Some(&p) {
            return;
        }
        if tool != Tool::Pen {
            self.points.truncate(1);
        }
        if self.points.len() >= MAX_POINTS {
            self.overflow = true;
        } else {
            self.points.push(p);
        }
    }
}
struct Editor {
    store: ClipStore,
    doc: RefCell<Document>,
    area: gtk::DrawingArea,
    message: gtk::Label,
    dimensions: gtk::Label,
    tool: gtk::DropDown,
    options: Options,
    pending_actions: gtk::Box,
    apply: gtk::Button,
    discard: gtk::Button,
    undo: gtk::Button,
    save: gtk::Button,
    copy: gtk::Button,
    selection: RefCell<Option<Selection>>,
    drag: RefCell<Option<Drag>>,
}
impl Editor {
    fn kind(&self) -> Tool {
        Tool::MENU[self.tool.selected() as usize].0
    }
    fn status(&self, msg: &str) {
        self.message.set_text(msg);
        self.message.set_tooltip_text(Some(msg));
    }
    fn configure(&self) {
        let (k, name) = Tool::MENU[self.tool.selected() as usize];
        self.options.configure(k, name);
        self.status(hint(k));
    }
    fn can_export(&self) -> bool {
        self.selection.borrow().is_none() && self.drag.borrow().is_none()
    }
    fn buttons(&self) {
        let idle = self.can_export();
        let dragging = self.drag.borrow().is_some();
        self.pending_actions
            .set_visible(self.selection.borrow().is_some());
        self.pending_actions.set_sensitive(!dragging);
        self.options.button.set_sensitive(!dragging);
        self.tool.set_sensitive(idle);
        self.tool.set_tooltip_text(Some(if idle {
            "Choose a tool"
        } else {
            "Apply or discard the selection to switch tools"
        }));
        self.undo
            .set_sensitive(idle && !self.doc.borrow().undo.is_empty());
        self.save.set_sensitive(idle);
        self.copy.set_sensitive(idle);
    }
    fn reset(&self, msg: &str) {
        self.drag.take();
        self.selection.take();
        let doc = self.doc.borrow();
        self.dimensions.set_text(&format!(
            "{} × {} px",
            doc.pixels.width(),
            doc.pixels.height()
        ));
        self.status(msg);
        self.buttons();
        self.area.queue_draw();
    }
    fn stage(&self, input: Selection) -> Result<()> {
        let k = self.kind();
        let pixels = &self.doc.borrow().pixels;
        let selection = input.checked(k, pixels.width(), pixels.height())?;
        let name = Tool::MENU[self.tool.selected() as usize].1;
        self.status(&match &selection {
            Selection::Box(r) => format!("{name} selection · {} × {} px", r.width, r.height),
            Selection::Path(_) => format!("{name} selection ready"),
        });
        self.selection.replace(Some(selection));
        self.buttons();
        self.area.queue_draw();
        if self.options.needs_content(k) {
            self.options.request_content();
        } else {
            self.options.button.popdown();
            self.apply.grab_focus();
        }
        Ok(())
    }
    fn apply(&self) {
        if self.drag.borrow().is_some() {
            return;
        }
        let result = {
            let draft = self.selection.borrow();
            let Some(selection) = draft.as_ref() else {
                return;
            };
            self.options
                .apply(self.kind(), selection, &self.doc.borrow().pixels)
        };
        match result {
            Ok(p) => {
                self.doc.borrow_mut().commit(p);
                self.reset("Applied edit.");
                self.tool.grab_focus();
            }
            Err(e) => {
                self.status(&e.message);
                if matches!(self.kind(), Tool::Text | Tool::Emoji) {
                    self.options.request_content();
                    self.options.error(&e.message);
                }
            }
        }
    }
    fn undo(&self) {
        if self.can_export() && self.doc.borrow_mut().undo() {
            self.reset("Undid edit.");
        }
    }
    fn begin(&self, x: f64, y: f64) {
        self.drag.replace(None);
        let d = self.doc.borrow();
        let p = &d.pixels;
        let Some(f) = Fit::new(self.area.width(), self.area.height(), p.width(), p.height()) else {
            return;
        };
        if let Some(anchor) = ImageDrag::begin(f, x, y) {
            self.drag.replace(Some(Drag {
                anchor,
                points: vec![anchor.start],
                overflow: false,
            }));
            self.buttons();
            self.area.queue_draw();
        }
    }
    fn motion(&self, dx: f64, dy: f64) {
        if let Some(d) = self.drag.borrow_mut().as_mut() {
            d.update(self.kind(), dx, dy);
            self.area.queue_draw();
        }
    }
    fn finish(&self, dx: f64, dy: f64) {
        let Some(mut d) = self.drag.take() else {
            return;
        };
        let k = self.kind();
        d.update(k, dx, dy);
        if d.overflow {
            self.status("Stroke not selected: use shorter strokes (2048 points each).");
        } else if d.points.len() >= 2 {
            let a = d.points[0];
            let b = *d.points.last().unwrap();
            let input = if k.path() {
                Ok(Selection::Path(format_points(&d.points)))
            } else {
                Fit::region(a, b)
                    .map(Selection::Box)
                    .ok_or_else(|| ClipError::invalid("Drag a box with nonzero width and height."))
            };
            if let Err(e) = input.and_then(|input| self.stage(input)) {
                self.status(&e.message);
            }
        }
        self.buttons();
        self.area.queue_draw();
    }
    fn guides(&self, c: &cairo::Context, f: Fit) {
        let k = self.kind();
        let drag = self.drag.borrow();
        let selection = self.selection.borrow();
        if selection.is_none() && drag.is_none() {
            return;
        }
        if k.path() {
            let pts;
            let points = match drag.as_ref() {
                Some(d) => d.points.as_slice(),
                None => {
                    let Some(Selection::Path(text)) = selection.as_ref() else {
                        return;
                    };
                    let Ok(p) = parse_points(text) else { return };
                    pts = p;
                    &pts
                }
            };
            if points.len() < 2
                || points.iter().any(|&(x, y)| {
                    !(0.0..=f.width as f64).contains(&x) || !(0.0..=f.height as f64).contains(&y)
                })
            {
                return;
            }
            c.move_to(points[0].0, points[0].1);
            for &(x, y) in &points[1..] {
                c.line_to(x, y);
            }
            if k == Tool::Arrow {
                let (x, y) = *points.last().unwrap();
                c.new_sub_path();
                c.arc(x, y, 3. / f.scale, 0., std::f64::consts::TAU);
            }
        } else {
            let region = match drag.as_ref() {
                Some(d) => Fit::region(d.points[0], *d.points.last().unwrap()),
                None => match selection.as_ref() {
                    Some(Selection::Box(r)) => Some(*r),
                    _ => None,
                },
            };
            let Some(r) = region else {
                return;
            };
            if k == Tool::Crop {
                c.rectangle(0., 0., f.width.into(), f.height.into());
                c.rectangle(r.x.into(), r.y.into(), r.width.into(), r.height.into());
                c.set_fill_rule(cairo::FillRule::EvenOdd);
                c.set_source_rgba(0., 0., 0., 0.45);
                let _ = c.fill();
            }
            c.rectangle(r.x.into(), r.y.into(), r.width.into(), r.height.into());
        }
        c.set_line_width(2.5 / f.scale);
        c.set_source_rgb(0., 0., 0.);
        let _ = c.stroke_preserve();
        c.set_line_width(1.5 / f.scale);
        c.set_dash(&[5. / f.scale, 4. / f.scale], 0.);
        c.set_source_rgb(1., 1., 1.);
        let _ = c.stroke();
    }
    fn wire(e: &Rc<Self>) {
        macro_rules! on {
            ($source:expr, $signal:ident, $s:ident, $body:block) => {{
                let weak = Rc::downgrade(e);
                $source.$signal(move |_| {
                    if let Some($s) = weak.upgrade() $body
                });
            }};
        }
        on!(e.tool, connect_selected_notify, s, {
            s.configure();
        });
        on!(e.options.popover, connect_show, s, {
            s.options
                .sync(&s.doc.borrow().pixels, s.selection.borrow().as_ref());
        });
        on!(e.options.select, connect_clicked, s, {
            if let Err(e) = s.stage(s.options.entered(s.kind())) {
                s.options.error(&e.message);
            }
        });
        on!(e.apply, connect_clicked, s, {
            s.apply();
        });
        on!(e.discard, connect_clicked, s, {
            s.reset("Discarded selection.");
            s.tool.grab_focus();
        });
        on!(e.undo, connect_clicked, s, {
            s.undo();
        });
        on!(e.save, connect_clicked, s, {
            if s.can_export() {
                match s.store.save_png(&s.doc.borrow().pixels) {
                    Ok(p) => s.status(&format!("Saved a new PNG: {}", p.display())),
                    Err(err) => s.status(&err.message),
                }
            }
        });
        on!(e.copy, connect_clicked, s, {
            if s.can_export() {
                s.copy
                    .clipboard()
                    .set_texture(&gdk::Texture::for_pixbuf(&s.doc.borrow().pixels));
                s.status("Copied image to the clipboard.");
            }
        });
        let weak = Rc::downgrade(e);
        let mut cache: Option<(Pixbuf, cairo::Pattern)> = None;
        e.area.set_draw_func(move |_, c, w, h| {
            let Some(s) = weak.upgrade() else {
                return;
            };
            let doc = s.doc.borrow();
            let p = &doc.pixels;
            let Some(f) = Fit::new(w, h, p.width(), p.height()) else {
                return;
            };
            // GTK paints the themed canvas background; only the fitted source and guides belong here.
            c.translate(f.left, f.top);
            c.scale(f.scale, f.scale);
            c.rectangle(0., 0., p.width().into(), p.height().into());
            c.clip();
            // Pointer motion changes only the selection guide. Don't reconvert the entire frozen raster per event.
            if let Some((_, pat)) = cache.as_ref().filter(|(last, _)| last == p) {
                let _ = c.set_source(pat);
            } else {
                c.set_source_pixbuf(p, 0., 0.);
                cache = Some((p.clone(), c.source()));
            }
            let _ = c.paint();
            s.guides(c, f);
        });
        let g = gtk::GestureDrag::new();
        g.set_button(1);
        let weak = Rc::downgrade(e);
        g.connect_drag_begin(move |_, x, y| {
            if let Some(s) = weak.upgrade() {
                s.begin(x, y);
            }
        });
        let weak = Rc::downgrade(e);
        g.connect_drag_update(move |_, dx, dy| {
            if let Some(s) = weak.upgrade() {
                s.motion(dx, dy);
            }
        });
        let weak = Rc::downgrade(e);
        g.connect_drag_end(move |_, dx, dy| {
            if let Some(s) = weak.upgrade() {
                s.finish(dx, dy);
            }
        });
        let weak = Rc::downgrade(e);
        g.connect_cancel(move |_, _| {
            if let Some(s) = weak.upgrade() {
                s.drag.replace(None);
                s.buttons();
                s.area.queue_draw();
            }
        });
        e.area.add_controller(g);
    }
}

fn shortcut(
    keys: &gtk::ShortcutController,
    trigger: &str,
    button: &gtk::Button,
    options: &Options,
) {
    let button = button.downgrade();
    let popover = options.popover.downgrade();
    keys.add_shortcut(gtk::Shortcut::new(
        gtk::ShortcutTrigger::parse_string(trigger),
        Some(gtk::CallbackAction::new(move |_, _| {
            // Let the inspector's text fields keep native Copy and Undo.
            if let Some(b) = button
                .upgrade()
                .filter(|b| b.is_sensitive() && b.is_mapped())
            {
                if !popover.upgrade().is_some_and(|p| p.is_mapped()) {
                    b.emit_clicked();
                    return glib::Propagation::Stop;
                }
            }
            glib::Propagation::Proceed
        })),
    ));
}

pub(super) fn install(win: &adw::Window, store: &ClipStore, pixels: Pixbuf, name: &str) {
    let body = gtk::Box::new(gtk::Orientation::Vertical, 0);
    let header = adw::HeaderBar::new();
    header.set_title_widget(Some(&adw::WindowTitle::new("Screenshot Editor", name)));
    body.append(&header);
    let tool = gtk::DropDown::from_strings(&Tool::MENU.map(|(_, name)| name));
    tool.update_property(&[Property::Label("Annotation tool")]);
    tool.set_tooltip_text(Some("Choose a tool"));
    let options = Options::new();
    let tool_group = gtk::Box::new(gtk::Orientation::Horizontal, 0);
    tool_group.add_css_class("linked");
    tool_group.append(&tool);
    tool_group.append(&options.button);
    header.pack_start(&tool_group);
    let undo = gtk::Button::from_icon_name("edit-undo-symbolic");
    undo.update_property(&[
        Property::Label("Undo last edit"),
        Property::KeyShortcuts("Control+z"),
    ]);
    undo.set_tooltip_text(Some("Undo last edit (Ctrl+Z)"));
    header.pack_start(&undo);
    let save = gtk::Button::with_mnemonic("_Save PNG");
    save.update_property(&[
        Property::Label("Save new PNG"),
        Property::KeyShortcuts("Control+s"),
    ]);
    save.set_tooltip_text(Some(
        "Save a new PNG to Clips Library (Ctrl+S). The original is never overwritten.",
    ));
    save.add_css_class("suggested-action");
    header.pack_end(&save);
    let copy = gtk::Button::from_icon_name("edit-copy-symbolic");
    copy.update_property(&[
        Property::Label("Copy image"),
        Property::KeyShortcuts("Control+c"),
    ]);
    copy.set_tooltip_text(Some("Copy image (Ctrl+C)"));
    header.pack_end(&copy);

    let area = gtk::DrawingArea::builder()
        .hexpand(true)
        .vexpand(true)
        .content_width(160)
        .content_height(100)
        .build();
    area.set_cursor_from_name(Some("crosshair"));
    area.update_property(&[Property::Label("Screenshot canvas"), Property::Description("Drag on the image or use precise coordinates in Tool options. Selection guides are not exported.")]);
    let frame = gtk::Frame::builder()
        .child(&area)
        .margin_start(8)
        .margin_end(8)
        .margin_top(8)
        .build();
    frame.add_css_class("view");
    frame.set_overflow(gtk::Overflow::Hidden);
    body.append(&frame);

    let footer = gtk::Box::builder()
        .orientation(gtk::Orientation::Horizontal)
        .spacing(10)
        .height_request(34)
        .margin_start(12)
        .margin_end(12)
        .margin_top(4)
        .margin_bottom(4)
        .build();
    let dimensions = gtk::Label::new(None);
    dimensions.add_css_class("caption");
    dimensions.add_css_class("numeric");
    dimensions.set_tooltip_text(Some("Image dimensions in source pixels"));
    footer.append(&dimensions);
    let divider = gtk::Separator::new(gtk::Orientation::Vertical);
    divider.set_margin_top(8);
    divider.set_margin_bottom(8);
    footer.append(&divider);
    let message = gtk::Label::builder()
        .hexpand(true)
        .xalign(0.)
        .ellipsize(pango::EllipsizeMode::End)
        .accessible_role(gtk::AccessibleRole::Status)
        .build();
    message.add_css_class("caption");
    footer.append(&message);
    let pending_actions = gtk::Box::new(gtk::Orientation::Horizontal, 6);
    let discard = gtk::Button::with_mnemonic("_Discard");
    let apply = gtk::Button::with_mnemonic("_Apply");
    apply.add_css_class("suggested-action");
    pending_actions.append(&discard);
    pending_actions.append(&apply);
    footer.append(&pending_actions);
    body.append(&footer);
    win.set_content(Some(&body));
    let keys = gtk::ShortcutController::new();
    shortcut(&keys, "<Control>z", &undo, &options);
    shortcut(&keys, "<Control>c", &copy, &options);
    shortcut(&keys, "<Control>s", &save, &options);
    win.add_controller(keys);
    let e = Rc::new(Editor {
        store: store.clone(),
        doc: RefCell::new(Document {
            pixels,
            undo: VecDeque::new(),
            undo_bytes: 0,
        }),
        area,
        message,
        dimensions,
        tool,
        options,
        pending_actions,
        apply,
        discard,
        undo,
        save,
        copy,
        selection: RefCell::new(None),
        drag: RefCell::new(None),
    });
    Editor::wire(&e);
    e.configure();
    e.reset(hint(e.kind()));
    win.connect_map(move |_| {
        e.tool.grab_focus();
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn full_frame_is_not_a_crop_but_can_be_deliberately_redacted() {
        let all = Region {
            x: 0,
            y: 0,
            width: 9,
            height: 7,
        };
        assert!(Selection::Box(all).checked(Tool::Crop, 9, 7).is_err());
        assert_eq!(
            Selection::Box(all).checked(Tool::Opaque, 9, 7).unwrap(),
            Selection::Box(all)
        );
        assert!(
            Selection::Box(Region { x: 1, ..all })
                .checked(Tool::Opaque, 9, 7)
                .is_err()
        );
    }
    #[test]
    fn only_complete_in_bounds_paths_stage_edits() {
        let path = |s: &str, k| Selection::Path(s.into()).checked(k, 12, 8);
        for s in ["", "1,1", "1,1; 1,1", "1,1; 13,1", "1,1; 3,3; 11,1"] {
            assert!(path(s, Tool::Line).is_err());
        }
        let reversed = "12,8; 0,0";
        assert_eq!(
            path(reversed, Tool::Arrow).unwrap(),
            Selection::Path(reversed.into())
        );
        assert!(path("2,3; 11,7; 2,3", Tool::Pen).is_ok());
    }
    #[test]
    fn stroke_tracks_fitted_source_in_both_directions() {
        let fit = Fit::new(320, 300, 640, 400).unwrap();
        let origin = (290., 160.);
        let anchor = ImageDrag::begin(fit, origin.0, origin.1).unwrap();
        let mut d = Drag {
            anchor,
            points: vec![anchor.start],
            overflow: false,
        };
        d.update(Tool::Pen, -60., 24.);
        d.update(Tool::Pen, -400., 270.);
        assert_eq!(d.points, vec![(580., 220.), (460., 268.), (0., 400.)]);
        d.update(Tool::Line, -80., 0.);
        assert_eq!(d.points, vec![(580., 220.), (420., 220.)]);
    }
}
