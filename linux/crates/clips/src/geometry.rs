use crate::{ClipError, Result};
use gtk::gdk_pixbuf::Pixbuf;
use serde::Deserialize;

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Region {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

impl Region {
    pub(crate) fn validate(self, width: i32, height: i32) -> Result<Self> {
        if self.x < 0
            || self.y < 0
            || self.width <= 0
            || self.height <= 0
            || self.x > width - self.width
            || self.y > height - self.height
        {
            Err(ClipError::invalid(
                "The region must be non-empty and inside the image, in source pixels.",
            ))
        } else {
            Ok(self)
        }
    }

    pub fn crop(self, source: &Pixbuf) -> Result<Pixbuf> {
        let r = self.validate(source.width(), source.height())?;
        // Copy, rather than retaining a subpixbuf pointing into a large original image.
        source
            .new_subpixbuf(r.x, r.y, r.width, r.height)
            .copy()
            .ok_or_else(|| ClipError::internal("Could not allocate the cropped image."))
    }
}

/// Logical widget coordinates; GTK already accounts for display scaling. Only the fitted raster matters.
#[derive(Clone, Copy)]
pub(crate) struct Fit {
    pub left: f64,
    pub top: f64,
    pub scale: f64,
    pub width: i32,
    pub height: i32,
}
impl Fit {
    pub fn new(widget_w: i32, widget_h: i32, width: i32, height: i32) -> Option<Self> {
        if [widget_w, widget_h, width, height].into_iter().min()? <= 0 {
            return None;
        }
        let scale = (widget_w as f64 / width as f64).min(widget_h as f64 / height as f64);
        Some(Self {
            left: (widget_w as f64 - width as f64 * scale) / 2.0,
            top: (widget_h as f64 - height as f64 * scale) / 2.0,
            scale,
            width,
            height,
        })
    }
    pub fn point(self, x: f64, y: f64, clamp: bool) -> Option<(f64, f64)> {
        let u = (x - self.left) / self.scale;
        let v = (y - self.top) / self.scale;
        let (w, h) = (self.width as f64, self.height as f64);
        if !u.is_finite()
            || !v.is_finite()
            || (!clamp && !(0.0..=w).contains(&u))
            || (!clamp && !(0.0..=h).contains(&v))
        {
            return None;
        }
        Some((u.clamp(0.0, w), v.clamp(0.0, h)))
    }
    pub fn region(a: (f64, f64), b: (f64, f64)) -> Option<Region> {
        if a.0 == b.0 || a.1 == b.1 {
            return None;
        }
        let x = a.0.min(b.0).floor() as i32;
        let y = a.1.min(b.1).floor() as i32;
        Some(Region {
            x,
            y,
            width: a.0.max(b.0).ceil() as i32 - x,
            height: a.1.max(b.1).ceil() as i32 - y,
        })
    }
}

/// A drag starts on the image (not a letterbox). Clamp its endpoint to the same frozen raster.
#[derive(Clone, Copy)]
pub(crate) struct ImageDrag {
    fit: Fit,
    widget: (f64, f64),
    pub start: (f64, f64),
}
impl ImageDrag {
    pub fn begin(fit: Fit, x: f64, y: f64) -> Option<Self> {
        Some(Self {
            fit,
            widget: (x, y),
            start: fit.point(x, y, false)?,
        })
    }
    pub fn point(self, dx: f64, dy: f64) -> Option<(f64, f64)> {
        self.fit.point(self.widget.0 + dx, self.widget.1 + dy, true)
    }
    pub fn region(self, dx: f64, dy: f64) -> Option<Region> {
        Fit::region(self.start, self.point(dx, dy)?)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn letterbox_fractional_reverse_drag() {
        let f = Fit::new(500, 500, 800, 400).unwrap();
        assert_eq!(f.point(100., 124., false), None);
        let a = f.point(412.4, 300.2, false).unwrap();
        let b = f.point(25.1, 100., true).unwrap();
        assert_eq!(
            Fit::region(a, b),
            Some(Region {
                x: 40,
                y: 0,
                width: 620,
                height: 281
            })
        );
        assert!(Fit::region(a, a).is_none());
    }
    #[test]
    fn picker_drag_uses_frozen_pixels_and_never_selects_letterboxing() {
        let f = Fit::new(503, 375, 1920, 1080).unwrap();
        assert!(ImageDrag::begin(f, 1., 1.).is_none());
        let anchor = ImageDrag::begin(f, 250.5, 190.5).unwrap();
        let r = anchor.region(-650., -400.).unwrap();
        assert_eq!((r.x, r.y), (0, 0));
        assert_eq!(r.width, (250.5 / f.scale).ceil() as i32);
        assert_eq!(r.height, ((190.5 - f.top) / f.scale).ceil() as i32);
        assert!(anchor.region(0., 0.).is_none());
    }
    #[test]
    fn horizontal_padding_is_not_part_of_source() {
        let f = Fit::new(640, 400, 200, 800).unwrap();
        assert_eq!(f.point(0., 0., false), None);
        assert_eq!(
            Fit::region(
                f.point(270., 0., false).unwrap(),
                f.point(800., 420., true).unwrap()
            ),
            Some(Region {
                x: 0,
                y: 0,
                width: 200,
                height: 800
            })
        );
    }
    #[test]
    fn rejects_unsafe_regions() {
        assert!(
            Region {
                x: i32::MAX,
                y: 0,
                width: 10,
                height: 1
            }
            .validate(100, 100)
            .is_err()
        );
        assert!(
            Region {
                x: 0,
                y: 0,
                width: 100,
                height: 0
            }
            .validate(100, 100)
            .is_err()
        );
    }
}
