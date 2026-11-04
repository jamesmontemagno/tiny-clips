use crate::{
    ClipError, Region, Result,
    annotation::{parse_marks, render},
};
use gtk::gdk_pixbuf::Pixbuf;
use serde_json::{Value, json};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub(super) enum Tool {
    Crop,
    Rectangle,
    Opaque,
    Ellipse,
    Arrow,
    Line,
    Pen,
    Text,
    Number,
    Emoji,
    Blur,
    Pixelate,
}
impl Tool {
    pub const MENU: [(Self, &'static str); 12] = [
        (Self::Crop, "Crop"),
        (Self::Rectangle, "Rectangle"),
        (Self::Opaque, "Opaque redaction"),
        (Self::Ellipse, "Ellipse"),
        (Self::Arrow, "Arrow"),
        (Self::Line, "Line"),
        (Self::Pen, "Freehand pen"),
        (Self::Text, "Text"),
        (Self::Number, "Numbered callout"),
        (Self::Emoji, "Emoji"),
        (Self::Blur, "Blur"),
        (Self::Pixelate, "Pixelate"),
    ];
    pub fn path(self) -> bool {
        matches!(self, Self::Arrow | Self::Line | Self::Pen)
    }
    pub fn stroke(self) -> bool {
        self.path() || matches!(self, Self::Rectangle | Self::Ellipse)
    }
    pub fn color(self) -> bool {
        self.stroke() || matches!(self, Self::Text | Self::Number)
    }
    pub fn fill(self) -> bool {
        matches!(self, Self::Rectangle | Self::Ellipse | Self::Text)
    }
    pub fn strength(self) -> bool {
        matches!(self, Self::Blur | Self::Pixelate)
    }
}
pub(super) type Point = (f64, f64);
pub(super) const MAX_POINTS: usize = 2048;
pub(super) const COLORS: [(&str, &str); 6] = [
    ("Red", "#ff3030"),
    ("Blue", "#2673e8"),
    ("Green", "#178344"),
    ("Yellow", "#ffcc00"),
    ("Black", "#161616"),
    ("White", "#ffffff"),
];

pub(super) fn parse_points(input: &str) -> Result<Vec<Point>> {
    let mut points = Vec::new();
    for pair in input.split(';') {
        if points.len() == MAX_POINTS {
            return Err(ClipError::invalid(
                "A freehand stroke can contain at most 2048 points.",
            ));
        }
        let (x, y) = pair
            .split_once(',')
            .ok_or_else(|| ClipError::invalid("Enter points as x,y; x,y in source pixels."))?;
        let number = |s: &str| {
            s.trim()
                .parse::<f64>()
                .ok()
                .filter(|n| n.is_finite())
                .ok_or_else(|| ClipError::invalid("Point coordinates must be finite numbers."))
        };
        points.push((number(x)?, number(y)?));
    }
    if points.len() < 2 {
        return Err(ClipError::invalid(
            "Enter at least two points, separated by semicolons.",
        ));
    }
    Ok(points)
}
pub(super) fn format_points(points: &[Point]) -> String {
    points
        .iter()
        .map(|p| format!("{:.2},{:.2}", p.0, p.1))
        .collect::<Vec<_>>()
        .join("; ")
}

pub(super) struct Edit<'a> {
    pub tool: Tool,
    pub region: Region,
    pub points: &'a str,
    pub color: &'a str,
    pub fill: Option<&'a str>,
    pub stroke: f64,
    pub font: f64,
    pub content: &'a str,
    pub number: i32,
    pub level: &'a str,
}
impl Edit<'_> {
    /// Build exactly the same validated mark as editor.annotate, then flatten it once for preview/export.
    pub fn apply(&self, pixels: &Pixbuf) -> Result<Pixbuf> {
        if self.tool == Tool::Crop {
            return self.region.crop(pixels);
        }
        let mut mark = json!({});
        if self.tool.path() {
            let points = parse_points(self.points)?;
            let ((min_x, max_x), (min_y, max_y)) = points.iter().fold(
                (
                    (f64::INFINITY, f64::NEG_INFINITY),
                    (f64::INFINITY, f64::NEG_INFINITY),
                ),
                |((l, r), (t, b)), &(x, y)| ((l.min(x), r.max(x)), (t.min(y), b.max(y))),
            );
            mark["bounds"] = json!({"x":min_x,"y":min_y,"width":max_x-min_x,"height":max_y-min_y});
            mark["points"] =
                Value::Array(points.iter().map(|&(x, y)| json!({"x":x,"y":y})).collect());
        } else {
            let r = self.region.validate(pixels.width(), pixels.height())?;
            mark["bounds"] = json!({"x":r.x,"y":r.y,"width":r.width,"height":r.height});
        }
        mark["type"] = json!(match self.tool {
            Tool::Rectangle => "rectangle",
            Tool::Ellipse => "ellipse",
            Tool::Line => "line",
            Tool::Arrow => "arrow",
            Tool::Pen => "pen",
            Tool::Text => "text",
            Tool::Number => "number",
            Tool::Emoji => "emoji",
            Tool::Blur | Tool::Pixelate | Tool::Opaque => "redact",
            Tool::Crop => unreachable!(),
        });
        if self.tool.color() {
            mark["color"] = json!(self.color);
        }
        if self.tool.stroke() {
            mark["lineWidth"] = json!(self.stroke);
        }
        if self.tool.fill() {
            if let Some(fill) = self.fill {
                mark["fillColor"] = json!(fill);
            }
        }
        match self.tool {
            Tool::Text => {
                mark["text"] = json!(self.content);
                mark["fontSize"] = json!(self.font);
            }
            Tool::Emoji => mark["emoji"] = json!(self.content),
            Tool::Number => {
                mark["text"] = json!(self.number.to_string());
                // Keep the numeral legible on the light swatches in the editor palette.
                if matches!(self.color, "#ffcc00" | "#ffffff" | "#ff3030") {
                    mark["textColor"] = json!("#000000");
                }
            }
            Tool::Opaque => mark["redactionStyle"] = json!("solid"),
            Tool::Blur | Tool::Pixelate => {
                mark["redactionStyle"] = json!(if self.tool == Tool::Blur {
                    "blur"
                } else {
                    "pixelate"
                });
                mark["redactionLevel"] = json!(self.level);
            }
            _ => {}
        }
        let marks = parse_marks(&json!([mark]), pixels.width(), pixels.height())?;
        render(pixels, &marks)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    fn source() -> Pixbuf {
        let p = Pixbuf::new(gtk::gdk_pixbuf::Colorspace::Rgb, true, 8, 48, 32).unwrap();
        p.fill(0xffffffff);
        p
    }
    fn sample<'a>(tool: Tool, points: &'a str) -> Edit<'a> {
        Edit {
            tool,
            region: Region {
                x: 2,
                y: 3,
                width: 20,
                height: 18,
            },
            points,
            color: "#1b5cd1",
            fill: None,
            stroke: 2.,
            font: 12.,
            content: "abc",
            number: 2,
            level: "medium",
        }
    }
    #[test]
    fn keyboard_points_preserve_order_and_reject_invalid_input() {
        let p = [(17.25, 5.), (2.5, 9.75), (10., 2.)];
        assert_eq!(parse_points(&format_points(&p)).unwrap(), p);
        for bad in ["2,3", "0,0; x,2", "0,0; NaN,2", "0,2,3; 2,3"] {
            assert!(parse_points(bad).is_err());
        }
    }
    #[test]
    fn text_callout_and_ellipse_use_the_same_renderer_as_automation() {
        let src = source();
        let b = json!({"x":2,"y":3,"width":20,"height":18});
        for (tool, descriptor) in [
            (
                Tool::Text,
                json!({"type":"text","bounds":b,"text":"abc","color":"#1b5cd1","fontSize":12}),
            ),
            (
                Tool::Number,
                json!({"type":"number","bounds":b,"text":"2","color":"#1b5cd1"}),
            ),
            (
                Tool::Ellipse,
                json!({"type":"ellipse","bounds":b,"color":"#1b5cd1","lineWidth":2}),
            ),
        ] {
            let actual = sample(tool, "").apply(&src);
            let expected = render(&src, &parse_marks(&json!([descriptor]), 48, 32).unwrap());
            match (actual, expected) {
                (Ok(p), Ok(q)) => {
                    assert_eq!(p.read_pixel_bytes(), q.read_pixel_bytes());
                    assert_ne!(p.read_pixel_bytes(), src.read_pixel_bytes());
                }
                // A minimal headless host may lack fonts. The UI must fail just as explicitly as the API.
                (Err(e), Err(f)) if matches!(tool, Tool::Text | Tool::Number) => {
                    assert_eq!((e.code, f.code), ("not_supported", "not_supported"))
                }
                _ => panic!("Editor and automation renderers disagreed"),
            }
        }
        let mut light = sample(Tool::Number, "");
        light.color = "#ffffff";
        let expected = json!([{"type":"number","bounds":b,"text":"2","color":"#ffffff","textColor":"#000000"}]);
        if let Ok(applied) = light.apply(&src) {
            assert_eq!(
                applied.read_pixel_bytes(),
                render(&src, &parse_marks(&expected, 48, 32).unwrap())
                    .unwrap()
                    .read_pixel_bytes()
            );
        }
        let mut bad = sample(Tool::Text, "");
        bad.content = "";
        assert!(bad.apply(&src).is_err());
    }
    #[test]
    fn directed_pen_and_arrow_use_agent_pixels() {
        let src = source();
        for (tool, points, kind) in [
            (Tool::Arrow, "37,7; 4,7", "arrow"),
            (Tool::Pen, "37,7; 19,21; 4,7", "pen"),
        ] {
            let actual = sample(tool, points).apply(&src).unwrap();
            let expected = json!([{"type":kind,"points":parse_points(points).unwrap().into_iter().map(|(x,y)|json!({"x":x,"y":y})).collect::<Vec<_>>(),"bounds":{"x":4,"y":7,"width":33,"height":14},"color":"#1b5cd1","lineWidth":2}]);
            assert_eq!(
                actual.read_pixel_bytes(),
                render(&src, &parse_marks(&expected, 48, 32).unwrap())
                    .unwrap()
                    .read_pixel_bytes()
            );
            assert_ne!(actual.read_pixel_bytes(), src.read_pixel_bytes());
        }
        assert!(sample(Tool::Arrow, "1,1; 90,10").apply(&src).is_err());
    }
}
