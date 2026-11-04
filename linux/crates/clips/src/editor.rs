use crate::{ClipStore, Region, Result};
use adw::prelude::*;
use gtk::gdk_pixbuf::Pixbuf;
use std::{collections::VecDeque, path::Path};
#[path = "recording_region.rs"]
mod recording_region;
#[path = "editor_ui.rs"]
mod ui;
pub use recording_region::choose_region;

struct Document {
    pixels: Pixbuf,
    undo: VecDeque<Pixbuf>,
    undo_bytes: usize,
}
fn pixel_cost(p: &Pixbuf) -> usize {
    p.rowstride() as usize * p.height() as usize
}
impl Document {
    fn commit(&mut self, after: Pixbuf) {
        let prev = std::mem::replace(&mut self.pixels, after);
        self.undo_bytes += pixel_cost(&prev);
        self.undo.push_back(prev);
        while self.undo.len() > 1 && (self.undo.len() > 32 || self.undo_bytes > 128 * 1024 * 1024) {
            self.undo_bytes -= pixel_cost(&self.undo.pop_front().unwrap());
        }
    }
    fn undo(&mut self) -> bool {
        if let Some(p) = self.undo.pop_back() {
            self.undo_bytes -= pixel_cost(&p);
            self.pixels = p;
            true
        } else {
            false
        }
    }
}
#[derive(Clone)]
struct Fields([gtk::SpinButton; 4]);
impl Fields {
    fn new(grid: &gtk::Grid) -> Self {
        Self(["_X", "_Y", "_Width", "_Height"].map(|text| {
            let i = match text {
                "_X" => 0,
                "_Y" => 1,
                "_Width" => 2,
                _ => 3,
            };
            let label = gtk::Label::with_mnemonic(text);
            label.set_halign(gtk::Align::Start);
            let spin = gtk::SpinButton::with_range(0., 32767., 1.);
            spin.set_digits(0);
            spin.set_numeric(true);
            spin.set_width_chars(5);
            label.set_mnemonic_widget(Some(&spin));
            grid.attach(&label, (i % 2) * 2, i / 2, 1, 1);
            grid.attach(&spin, (i % 2) * 2 + 1, i / 2, 1, 1);
            spin
        }))
    }
    fn region(&self) -> Region {
        let [x, y, width, height] = self.0.each_ref().map(|s| s.value_as_int());
        Region {
            x,
            y,
            width,
            height,
        }
    }
    fn set(&self, r: Region) {
        for (s, v) in self.0.iter().zip([r.x, r.y, r.width, r.height]) {
            s.set_value(v.into());
        }
    }
    fn reset(&self, p: &Pixbuf) {
        let (w, h) = (p.width(), p.height());
        for (i, (s, max)) in self.0.iter().zip([w - 1, h - 1, w, h]).enumerate() {
            s.set_range(if i < 2 { 0. } else { 1. }, max.into());
        }
        self.set(Region {
            x: 0,
            y: 0,
            width: w,
            height: h,
        });
    }
}
pub fn open_editor(parent: &impl IsA<gtk::Window>, store: &ClipStore, path: &Path) -> Result<()> {
    let pixels = store.load_png(path)?;
    let name = path
        .file_name()
        .and_then(|s| s.to_str())
        .unwrap_or("Screenshot");
    let parent: &gtk::Window = parent.as_ref();
    let win = adw::Window::builder()
        .title(format!("Screenshot Editor — {name}"))
        .transient_for(parent)
        .destroy_with_parent(true)
        .default_width(920)
        .default_height(550)
        .build();
    win.set_application(parent.application().as_ref());
    ui::install(&win, store, pixels, name);
    win.present();
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn undo_restores_crop_and_exact_pixels() {
        let p = Pixbuf::new(gtk::gdk_pixbuf::Colorspace::Rgb, true, 8, 10, 5).unwrap();
        p.fill(0x123456ff);
        let mut d = Document {
            pixels: p.clone(),
            undo: VecDeque::new(),
            undo_bytes: 0,
        };
        let r = Region {
            x: 2,
            y: 1,
            width: 4,
            height: 3,
        };
        let mark = crate::annotation::Mark::rectangle(r, true);
        d.commit(crate::annotation::render(&d.pixels, &[mark]).unwrap());
        let redacted = d.pixels.read_pixel_bytes();
        d.commit(r.crop(&d.pixels).unwrap());
        assert_eq!((d.pixels.width(), d.pixels.height()), (4, 3));
        assert!(d.undo());
        assert_eq!(d.pixels.read_pixel_bytes(), redacted);
        assert!(d.undo());
        assert_eq!(d.pixels.read_pixel_bytes(), p.read_pixel_bytes());
        assert!(!d.undo());
    }
}
