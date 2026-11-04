use crate::{ClipError, Region, Result};
use gtk::{
    cairo::{Context, Format, ImageSurface, LineCap, LineJoin},
    gdk_pixbuf::{Colorspace, Pixbuf},
    pango,
    prelude::*,
};
use serde::Deserialize;
use serde_json::Value;
use std::f64::consts::TAU;

#[derive(Clone, Copy, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Bounds {
    x: f64,
    y: f64,
    width: f64,
    height: f64,
}
impl Bounds {
    fn pixels(self) -> Region {
        let x = self.x.floor() as i32;
        let y = self.y.floor() as i32;
        Region {
            x,
            y,
            width: (self.x + self.width).ceil() as i32 - x,
            height: (self.y + self.height).ceil() as i32 - y,
        }
    }
    fn has_area(self) -> bool {
        self.x + self.width > self.x && self.y + self.height > self.y
    }
}
impl From<Region> for Bounds {
    fn from(r: Region) -> Self {
        Self {
            x: r.x.into(),
            y: r.y.into(),
            width: r.width.into(),
            height: r.height.into(),
        }
    }
}
#[derive(Clone, Copy, Debug, Deserialize, PartialEq)]
#[serde(deny_unknown_fields)]
struct Point {
    x: f64,
    y: f64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "lowercase")]
enum Tool {
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Pen,
    Text,
    Number,
    Emoji,
    Redact,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "lowercase")]
enum Redaction {
    #[default]
    Blur,
    Pixelate,
    Solid,
}
#[derive(Clone, Copy, Debug, Default, Deserialize)]
#[serde(rename_all = "lowercase")]
enum Level {
    Light,
    #[default]
    Medium,
    Heavy,
}
impl Level {
    fn radius(self) -> usize {
        match self {
            Self::Light => 5,
            Self::Medium => 10,
            Self::Heavy => 20,
        }
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Spec {
    #[serde(rename = "type")]
    tool: Tool,
    bounds: Bounds,
    #[serde(default)]
    points: Vec<Point>,
    text: Option<String>,
    emoji: Option<String>,
    color: Option<String>,
    text_color: Option<String>,
    fill_color: Option<String>,
    line_width: Option<f64>,
    font_size: Option<f64>,
    #[serde(default)]
    redaction_style: Redaction,
    #[serde(default)]
    redaction_level: Level,
}
#[derive(Debug, Clone, Copy)]
struct Color(u32);
impl Color {
    const RED: Self = Self(0xffff0000);
    const WHITE: Self = Self(0xffffffff);
    fn parse(s: &str) -> Result<Self> {
        let digits = s
            .strip_prefix('#')
            .ok_or_else(|| ClipError::invalid("Colors must be #RRGGBB or #AARRGGBB."))?;
        if !matches!(digits.len(), 6 | 8) || !digits.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err(ClipError::invalid("Invalid hex color."));
        }
        let raw = u32::from_str_radix(digits, 16).map_err(ClipError::invalid_text)?;
        Ok(Self(if digits.len() == 6 {
            raw | 0xff000000
        } else {
            raw
        }))
    }
    fn set(self, c: &Context) {
        let [a, r, g, b] = self.0.to_be_bytes();
        c.set_source_rgba(
            r as f64 / 255.,
            g as f64 / 255.,
            b as f64 / 255.,
            a as f64 / 255.,
        );
    }
}
impl ClipError {
    fn invalid_text(e: impl std::fmt::Display) -> Self {
        Self::invalid(e.to_string())
    }
}

pub(crate) struct Mark {
    tool: Tool,
    b: Bounds,
    points: Vec<Point>,
    color: Color,
    fill: Option<Color>,
    text_color: Option<Color>,
    line: f64,
    font: f64,
    text: String,
    style: Redaction,
    level: Level,
}
impl Mark {
    #[cfg(test)]
    pub fn rectangle(region: Region, redact: bool) -> Self {
        Self {
            tool: if redact {
                Tool::Redact
            } else {
                Tool::Rectangle
            },
            b: region.into(),
            points: vec![],
            color: Color::RED,
            fill: None,
            text_color: None,
            line: 4.,
            font: 28.,
            text: String::new(),
            style: Redaction::Solid,
            level: Level::Medium,
        }
    }
    fn parse(s: Spec, w: i32, h: i32, next: &mut u32) -> Result<Self> {
        let b = s.bounds;
        let inside = |p: Point| {
            p.x.is_finite()
                && p.y.is_finite()
                && (0.0..=w as f64).contains(&p.x)
                && (0.0..=h as f64).contains(&p.y)
        };
        if !inside(Point { x: b.x, y: b.y })
            || b.width < 0.
            || b.height < 0.
            || !inside(Point {
                x: b.x + b.width,
                y: b.y + b.height,
            })
        {
            return Err(ClipError::invalid(
                "Annotation bounds are outside the image.",
            ));
        }
        let mut points = s.points;
        if points.len() > 10_000 || points.iter().any(|&p| !inside(p)) {
            return Err(ClipError::invalid("Invalid annotation points."));
        }
        let mut text = s.text.unwrap_or_default();
        let emoji = s.emoji.unwrap_or_default();
        if text.contains('\0')
            || emoji.contains('\0')
            || text.chars().count() > 2000
            || emoji.chars().count() > 32
        {
            return Err(ClipError::invalid(
                "Annotation text is invalid or too long.",
            ));
        }
        if !b.has_area() && !matches!(s.tool, Tool::Line | Tool::Arrow | Tool::Pen | Tool::Text) {
            return Err(ClipError::invalid(
                "This annotation requires positive width and height.",
            ));
        }
        match s.tool {
            Tool::Arrow | Tool::Line => {
                if points.is_empty() {
                    points.extend([
                        Point { x: b.x, y: b.y },
                        Point {
                            x: b.x + b.width,
                            y: b.y + b.height,
                        },
                    ]);
                }
                if points.len() != 2 || points.first() == points.last() {
                    return Err(ClipError::invalid(
                        "A line needs exactly two distinct endpoints.",
                    ));
                }
            }
            Tool::Pen if points.len() < 2 => {
                return Err(ClipError::invalid(
                    "A pen stroke needs at least two points.",
                ));
            }
            Tool::Text if text.trim().is_empty() => {
                return Err(ClipError::invalid("A text annotation needs text."));
            }
            Tool::Number => {
                let n = if text.is_empty() {
                    *next
                } else {
                    text.parse::<u32>()
                        .map_err(|_| ClipError::invalid("Invalid callout number."))?
                };
                if n > 9999 || (!text.is_empty() && !text.bytes().all(|b| b.is_ascii_digit())) {
                    return Err(ClipError::invalid(
                        "Callout must be a number from 0 to 9999.",
                    ));
                }
                text = n.to_string();
                *next = n + 1;
            }
            Tool::Emoji => {
                if !emoji.is_empty() {
                    text = emoji;
                }
                // GLib uses PCRE2's Unicode database, including ZWJ sequences, flags and keycaps.
                let is_emoji = gtk::glib::Regex::match_simple(
                    r"\A(?=\X\z)(?:\p{Extended_Pictographic}|\p{Regional_Indicator}|[#*0-9]\x{fe0f}?\x{20e3}).*\z",
                    text.as_str(),
                    gtk::glib::RegexCompileFlags::empty(),
                    gtk::glib::RegexMatchFlags::empty(),
                );
                if text.chars().count() > 32 || !is_emoji {
                    return Err(ClipError::invalid(
                        "An emoji annotation needs one emoji grapheme.",
                    ));
                }
            }
            _ => {}
        }
        let line = s.line_width.unwrap_or(6.);
        let font = s.font_size.unwrap_or(28.);
        if !(0.0..=200.).contains(&line) || !(1.0..=1000.).contains(&font) {
            return Err(ClipError::invalid("Invalid line or font size."));
        }
        let parse = |s: Option<String>| s.as_deref().map(Color::parse).transpose();
        Ok(Self {
            tool: s.tool,
            b,
            points,
            color: parse(s.color)?.unwrap_or(Color::RED),
            fill: parse(s.fill_color)?,
            text_color: parse(s.text_color)?,
            line,
            font,
            text,
            style: s.redaction_style,
            level: s.redaction_level,
        })
    }
}

pub(crate) fn parse_marks(v: &Value, w: i32, h: i32) -> Result<Vec<Mark>> {
    let a = v
        .as_array()
        .filter(|a| (1..=500).contains(&a.len()))
        .ok_or_else(|| ClipError::invalid("Supply 1 to 500 annotations."))?;
    let mut next = 1;
    let mut points = 0usize;
    let mut area = 0u64;
    a.iter()
        .map(|value| {
            if value
                .as_object()
                .is_none_or(|o| o.values().any(Value::is_null))
            {
                return Err(ClipError::invalid("Annotation fields cannot be null."));
            }
            let s = Spec::deserialize(value).map_err(ClipError::invalid_text)?;
            let optional: &[&str] = match s.tool {
                Tool::Rectangle | Tool::Ellipse => &["color", "fillColor", "lineWidth"],
                Tool::Arrow | Tool::Line | Tool::Pen => &["color", "lineWidth", "points"],
                Tool::Text => &["color", "textColor", "fillColor", "text", "fontSize"],
                Tool::Number => &["color", "textColor", "text"],
                Tool::Emoji => &["emoji", "text"],
                Tool::Redact if s.redaction_style == Redaction::Solid => &["redactionStyle"],
                Tool::Redact => &["redactionStyle", "redactionLevel"],
            };
            for key in value.as_object().unwrap().keys() {
                if !matches!(key.as_str(), "type" | "bounds") && !optional.contains(&key.as_str()) {
                    return Err(ClipError::invalid(format!(
                        "{key} does not apply to this annotation."
                    )));
                }
            }
            if s.tool == Tool::Emoji && s.emoji.is_some() && s.text.is_some() {
                return Err(ClipError::invalid("Supply emoji or text, not both."));
            }
            if s.tool == Tool::Text && s.text_color.is_some() && s.color.is_some() {
                return Err(ClipError::invalid(
                    "Supply textColor or color for the text foreground, not both.",
                ));
            }
            if s.tool == Tool::Number && s.text.as_deref() == Some("") {
                return Err(ClipError::invalid(
                    "A supplied callout number cannot be empty.",
                ));
            }
            let m = Mark::parse(s, w, h, &mut next)?;
            points += m.points.len();
            let r = m.b.pixels();
            area += r.width as u64 * r.height as u64;
            for pair in m.points.windows(2) {
                let length = (pair[1].x - pair[0].x).hypot(pair[1].y - pair[0].y);
                area += (length * m.line.max(1.)).ceil() as u64;
            }
            if points > 100_000 || area > 128_000_000 {
                return Err(ClipError::invalid(
                    "Annotation work exceeds the per-request limit.",
                ));
            }
            Ok(m)
        })
        .collect()
}

fn layout(c: &Context, text: &str, font: f64) -> pango::Layout {
    let l = pangocairo::functions::create_layout(c);
    let mut f = pango::FontDescription::from_string("Sans");
    f.set_absolute_size(font * pango::SCALE as f64);
    l.set_font_description(Some(&f));
    l.set_text(text);
    l
}
fn ellipse(c: &Context, b: Bounds) -> Result<()> {
    c.save().map_err(ClipError::internal)?;
    c.translate(b.x + b.width / 2., b.y + b.height / 2.);
    c.scale(b.width / 2., b.height / 2.);
    c.arc(0., 0., 1., 0., TAU);
    c.restore().map_err(ClipError::internal)
}
fn draw(c: &Context, m: &Mark) -> Result<()> {
    let b = m.b;
    m.color.set(c);
    c.set_line_width(m.line);
    c.set_line_join(LineJoin::Round);
    c.set_line_cap(LineCap::Round);
    match m.tool {
        Tool::Rectangle | Tool::Ellipse => {
            if m.tool == Tool::Ellipse {
                ellipse(c, b)?;
            } else {
                c.rectangle(b.x, b.y, b.width, b.height);
            }
            if let Some(color) = m.fill {
                color.set(c);
                c.fill_preserve().map_err(ClipError::internal)?;
                m.color.set(c);
            }
            c.stroke().map_err(ClipError::internal)?;
        }
        Tool::Arrow | Tool::Line | Tool::Pen => {
            let a = m.points[0];
            let z = m.points[m.points.len() - 1];
            c.move_to(a.x, a.y);
            if m.tool == Tool::Pen {
                for p in &m.points[1..] {
                    c.line_to(p.x, p.y);
                }
            } else {
                c.line_to(z.x, z.y);
            }
            c.stroke().map_err(ClipError::internal)?;
            if m.tool == Tool::Arrow && m.line > 0. {
                let dx = z.x - a.x;
                let dy = z.y - a.y;
                let len = dx.hypot(dy);
                let arrow = (m.line * 4.).max(8.).min(len * 0.6);
                let (ux, uy) = (dx / len, dy / len);
                c.move_to(z.x, z.y);
                c.line_to(
                    z.x - ux * arrow - uy * arrow * 0.45,
                    z.y - uy * arrow + ux * arrow * 0.45,
                );
                c.line_to(
                    z.x - ux * arrow + uy * arrow * 0.45,
                    z.y - uy * arrow - ux * arrow * 0.45,
                );
                c.close_path();
                c.fill().map_err(ClipError::internal)?;
            }
        }
        Tool::Text => {
            let l = layout(c, &m.text, m.font);
            if l.unknown_glyphs_count() > 0 {
                return Err(ClipError::new(
                    "not_supported",
                    "The installed fonts cannot render the supplied text.",
                ));
            }
            if b.width > 0. {
                l.set_width((b.width * pango::SCALE as f64) as i32);
                l.set_wrap(pango::WrapMode::WordChar);
            }
            let (lw, lh) = l.pixel_size();
            let bw = if b.width > 0. { b.width } else { lw as f64 };
            let bh = if b.height > 0. { b.height } else { lh as f64 };
            c.rectangle(b.x, b.y, bw, bh);
            c.clip();
            if let Some(color) = m.fill {
                color.set(c);
                c.paint().map_err(ClipError::internal)?;
            }
            m.text_color.unwrap_or(m.color).set(c);
            c.move_to(b.x, b.y);
            pangocairo::functions::show_layout(c, &l);
        }
        Tool::Number | Tool::Emoji => {
            let radius = b.width.min(b.height) / 2.;
            let cx = b.x + b.width / 2.;
            let cy = b.y + b.height / 2.;
            if m.tool == Tool::Number {
                c.arc(cx, cy, radius, 0., TAU);
                c.fill().map_err(ClipError::internal)?;
                m.text_color.unwrap_or(Color::WHITE).set(c);
            }
            let l = layout(c, &m.text, 48.);
            if l.unknown_glyphs_count() > 0 {
                return Err(ClipError::new(
                    "not_supported",
                    "The installed fonts cannot render this glyph.",
                ));
            }
            let (w, h) = l.pixel_size();
            let scale = (b.width / f64::from(w.max(1))).min(b.height / f64::from(h.max(1)));
            let scale = if m.tool == Tool::Number {
                scale
                    .min(radius * 1.6 / f64::from(w.max(1)))
                    .min(radius * 1.6 / f64::from(h.max(1)))
            } else {
                scale
            };
            c.translate(cx - w as f64 * scale / 2., cy - h as f64 * scale / 2.);
            c.scale(scale, scale);
            c.move_to(0., 0.);
            pangocairo::functions::show_layout(c, &l);
        }
        Tool::Redact => {
            let r = b.pixels();
            c.set_source_rgb(0., 0., 0.);
            c.rectangle(r.x.into(), r.y.into(), r.width.into(), r.height.into());
            c.fill().map_err(ClipError::internal)?;
        }
    }
    c.status().map_err(ClipError::internal)
}

/// A sliding box filter on one strided line of premultiplied pixels (including alpha).
fn blur_line(src: &[u8], dst: &mut [u8], n: usize, src_step: usize, dst_step: usize, r: usize) {
    let mut sum = [0u32; 4];
    let mut lo = 0;
    let mut hi = 0;
    for i in 0..n {
        let start = i.saturating_sub(r);
        let end = (i + r + 1).min(n);
        while hi < end {
            for ch in 0..4 {
                sum[ch] += src[hi * src_step + ch] as u32;
            }
            hi += 1;
        }
        while lo < start {
            for ch in 0..4 {
                sum[ch] -= src[lo * src_step + ch] as u32;
            }
            lo += 1;
        }
        for ch in 0..4 {
            dst[i * dst_step + ch] = (sum[ch] / (hi - lo) as u32) as u8;
        }
    }
}
fn redact(surface: &mut ImageSurface, m: &Mark) -> Result<()> {
    let r = m.b.pixels();
    let w = r.width as usize;
    let h = r.height as usize;
    let stride = surface.stride() as usize;
    let radius = m.level.radius();
    let mut buf = surface.data().map_err(ClipError::internal)?;
    let data = &mut buf[r.y as usize * stride + r.x as usize * 4..];
    if m.style == Redaction::Blur {
        let mut tmp = vec![0u8; w * h * 4];
        for y in 0..h {
            blur_line(&data[y * stride..], &mut tmp[y * w * 4..], w, 4, 4, radius);
        }
        for x in 0..w {
            blur_line(&tmp[x * 4..], &mut data[x * 4..], h, w * 4, stride, radius);
        }
    } else {
        for y in (0..h).step_by(radius) {
            for x in (0..w).step_by(radius) {
                let bottom = (y + radius).min(h);
                let right = (x + radius).min(w);
                let mut sum = [0u32; 4];
                for j in y..bottom {
                    for px in data[j * stride + x * 4..j * stride + right * 4].chunks_exact(4) {
                        for ch in 0..4 {
                            sum[ch] += px[ch] as u32;
                        }
                    }
                }
                let color = sum.map(|s| (s / ((bottom - y) * (right - x)) as u32) as u8);
                for j in y..bottom {
                    for px in data[j * stride + x * 4..j * stride + right * 4].chunks_exact_mut(4) {
                        px.copy_from_slice(&color);
                    }
                }
            }
        }
    }
    Ok(())
}

pub(crate) fn render(source: &Pixbuf, marks: &[Mark]) -> Result<Pixbuf> {
    let (w, h) = (source.width(), source.height());
    let mut surface = ImageSurface::create(Format::ARgb32, w, h).map_err(ClipError::internal)?;
    {
        let c = Context::new(&surface).map_err(ClipError::internal)?;
        c.set_source_pixbuf(source, 0., 0.);
        c.paint().map_err(ClipError::internal)?;
    }
    for m in marks {
        if m.tool == Tool::Redact && m.style != Redaction::Solid {
            redact(&mut surface, m)?;
        } else {
            draw(&Context::new(&surface).map_err(ClipError::internal)?, m)?;
        }
    }
    into_rgba(surface)
}

/// Cairo stores native-endian, premultiplied 0xAARRGGBB. Pixbuf exposes straight RGBA bytes.
/// Transfer the exclusively owned allocation in place, retaining its actual row stride.
fn into_rgba(surface: ImageSurface) -> Result<Pixbuf> {
    let (w, h, stride) = (surface.width(), surface.height(), surface.stride());
    let mut data = surface.take_data().map_err(ClipError::internal)?;
    for row in data.chunks_exact_mut(stride as usize) {
        for px in row[..w as usize * 4].chunks_exact_mut(4) {
            let [a, r, g, b] = u32::from_ne_bytes([px[0], px[1], px[2], px[3]]).to_be_bytes();
            let straight = |v: u8| match a {
                0 => 0,
                255 => v,
                _ => ((u32::from(v) * 255 + u32::from(a) / 2) / u32::from(a)).min(255) as u8,
            };
            px.copy_from_slice(&[straight(r), straight(g), straight(b), a]);
        }
    }
    Ok(Pixbuf::from_mut_slice(
        data,
        Colorspace::Rgb,
        true,
        8,
        w,
        h,
        stride,
    ))
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    fn source() -> Pixbuf {
        let p = Pixbuf::new(gtk::gdk_pixbuf::Colorspace::Rgb, true, 8, 8, 8).unwrap();
        p.fill(0xffffffff);
        p
    }
    fn pixel(p: &Pixbuf, x: usize, y: usize) -> [u8; 4] {
        let bytes = p.read_pixel_bytes();
        let i = y * p.rowstride() as usize + x * 4;
        bytes[i..i + 4].try_into().unwrap()
    }
    #[test]
    fn cairo_conversion_unpremultiplies_native_argb_without_reading_padding() {
        let bytes: Vec<u8> = [
            0x80402010_u32,
            0xff123456,
            0xdeadbeef,
            0x00000000,
            0x01010001,
            0xdeadbeef,
        ]
        .into_iter()
        .flat_map(u32::to_ne_bytes)
        .collect();
        let s = ImageSurface::create_for_data(bytes, Format::ARgb32, 2, 2, 12).unwrap();
        let p = into_rgba(s).unwrap();
        assert_eq!(pixel(&p, 0, 0), [128, 64, 32, 128]);
        assert_eq!(pixel(&p, 1, 0), [0x12, 0x34, 0x56, 255]);
        assert_eq!(pixel(&p, 0, 1), [0, 0, 0, 0]);
        assert_eq!(pixel(&p, 1, 1), [255, 0, 255, 1]);
    }
    #[test]
    fn transparent_preview_and_saved_png_have_identical_straight_rgba() {
        let src = Pixbuf::new(Colorspace::Rgb, true, 8, 3, 1).unwrap();
        src.fill(0x0000ff80);
        let marks=parse_marks(&json!([{"type":"rectangle","bounds":{"x":1,"y":0,"width":1,"height":1},"lineWidth":0,"fillColor":"#80ff0000"}]),3,1).unwrap();
        let applied = render(&src, &marks).unwrap();
        assert_eq!(pixel(&applied, 0, 0), [0, 0, 255, 128]);
        assert_eq!(pixel(&applied, 1, 0), [170, 0, 85, 192]);
        let png = applied.save_to_bufferv("png", &[]).unwrap();
        let decoded = Pixbuf::from_read(std::io::Cursor::new(png)).unwrap();
        assert_eq!(decoded.read_pixel_bytes(), applied.read_pixel_bytes());
    }
    #[test]
    fn redact_and_crop_do_not_modify_original() {
        let p = source();
        let r = Region {
            x: 2,
            y: 1,
            width: 3,
            height: 4,
        };
        let out = render(&p, &[Mark::rectangle(r, true)]).unwrap();
        assert_eq!(pixel(&p, 3, 1), [255; 4]);
        assert_eq!(pixel(&out, 1, 1), [255; 4]);
        assert_eq!(pixel(&out, 4, 4), [0, 0, 0, 255]);
        assert_eq!(pixel(&out, 5, 4), [255; 4]);
        let crop = r.crop(&out).unwrap();
        assert_eq!((crop.width(), crop.height()), (3, 4));
        assert_eq!(pixel(&crop, 2, 3), [0, 0, 0, 255]);
    }
    #[test]
    fn strict_batch_parsing() {
        for invalid in [
            json!([]),
            json!([{"type":"rectangle","bounds":{"x":-1,"y":0,"width":1,"height":1}}]),
            json!([{"type":"redact","bounds":{"x":0,"y":0,"width":9,"height":1}}]),
            json!([{"type":"rectangle","bounds":{"x":0,"y":0,"width":4,"height":4},"color":"#zz0000"}]),
            json!([{"type":"pen","bounds":{"x":0,"y":0,"width":4,"height":4},"points":[{"x":0,"y":0},{"x":4,"y":12}]}]),
            json!([{"type":"text","bounds":{"x":0,"y":0,"width":4,"height":4},"text":"a\u{0}b"}]),
            json!([{"type":"rectangle","bounds":{"x":0,"y":0,"width":4,"height":4},"outputPath":"evil"}]),
            json!([{"type":"rectangle","bounds":{"x":0,"y":0,"width":4,"height":4},"fontSize":12}]),
            json!([{"type":"redact","bounds":{"x":1,"y":8,"width":1,"height":1e-310}}]),
            json!([{"type":"line","bounds":{"x":0,"y":0,"width":3,"height":2},"points":[{"x":0,"y":0},{"x":2,"y":3},{"x":3,"y":1}]}]),
            json!([{"type":"emoji","bounds":{"x":0,"y":0,"width":4,"height":4},"emoji":"abc"}]),
        ] {
            assert!(parse_marks(&invalid, 8, 8).is_err());
        }
    }
    #[test]
    fn accepts_all_contract_tools_and_honors_fill_color() {
        let bounds = json!({"x":1,"y":1,"width":5,"height":5});
        let ops = json!([
            {"type":"rectangle","bounds":bounds,"lineWidth":0,"fillColor":"#0080ff"},
            {"type":"ellipse","bounds":bounds}, {"type":"arrow","bounds":bounds},
            {"type":"line","bounds":bounds}, {"type":"pen","bounds":bounds,"points":[{"x":1,"y":1},{"x":2,"y":4}]},
            {"type":"text","bounds":bounds,"text":"A"}, {"type":"number","bounds":bounds},
            {"type":"emoji","bounds":bounds,"emoji":"👀"}, {"type":"redact","bounds":bounds}
        ]);
        let marks = parse_marks(&ops, 8, 8).unwrap();
        assert_eq!(marks.len(), 9);
        let filled = render(&source(), &marks[..1]).unwrap();
        assert_eq!(pixel(&filled, 3, 3), [0, 128, 255, 255]);
        assert_eq!(pixel(&filled, 7, 7), [255; 4]);
    }
    #[test]
    fn effects_read_preceding_marks_and_stay_in_bounds() {
        let p = source();
        for style in ["blur", "pixelate"] {
            let ops=parse_marks(&json!([{"type":"redact","redactionStyle":"solid","bounds":{"x":1,"y":1,"width":2,"height":2}}, {"type":"redact","redactionStyle":style,"bounds":{"x":1,"y":1,"width":4,"height":5}}]),8,8).unwrap();
            let out = render(&p, &ops).unwrap();
            let v = pixel(&out, 1, 1);
            assert!(v[0] > 0 && v[0] < 255);
            assert_eq!(v[3], 255);
            assert_eq!(pixel(&out, 0, 0), [255; 4]);
        }
    }
}
