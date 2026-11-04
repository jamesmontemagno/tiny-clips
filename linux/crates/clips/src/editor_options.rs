use super::{Fields, Selection};
use crate::{
    Result,
    editor_tools::{COLORS, Edit, Tool},
};
use gtk::{gdk_pixbuf::Pixbuf, prelude::*};

struct OptionRow<T> {
    row: gtk::Box,
    input: T,
}
fn option<T: IsA<gtk::Widget>>(parent: &gtk::Box, caption: &str, input: T) -> OptionRow<T> {
    let row = gtk::Box::new(gtk::Orientation::Horizontal, 10);
    let label = gtk::Label::with_mnemonic(caption);
    label.set_mnemonic_widget(Some(&input));
    label.set_xalign(0.);
    label.set_hexpand(true);
    row.append(&label);
    row.append(&input);
    parent.append(&row);
    OptionRow { row, input }
}
fn spin(lo: f64, hi: f64, initial: f64) -> gtk::SpinButton {
    let spin = gtk::SpinButton::with_range(lo, hi, 1.);
    spin.set_numeric(true);
    spin.set_value(initial);
    spin
}

pub(super) struct Options {
    pub button: gtk::MenuButton,
    pub popover: gtk::Popover,
    pub select: gtk::Button,
    title: gtk::Label,
    detail: gtk::Expander,
    grid: gtk::Grid,
    fields: Fields,
    points: gtk::Entry,
    error: gtk::Label,
    color: OptionRow<gtk::DropDown>,
    fill: OptionRow<gtk::DropDown>,
    width: OptionRow<gtk::SpinButton>,
    font: OptionRow<gtk::SpinButton>,
    number: OptionRow<gtk::SpinButton>,
    level: OptionRow<gtk::DropDown>,
    text: OptionRow<gtk::Entry>,
}
impl Options {
    pub fn new() -> Self {
        let button = gtk::MenuButton::builder()
            .icon_name("emblem-system-symbolic")
            .build();
        button.update_property(&[gtk::accessible::Property::Label("Tool options")]);
        button.set_tooltip_text(Some("Tool options and precise coordinates"));
        let popover = gtk::Popover::new();
        button.set_popover(Some(&popover));
        let panel = gtk::Box::builder()
            .orientation(gtk::Orientation::Vertical)
            .spacing(8)
            .margin_start(8)
            .margin_end(8)
            .margin_top(6)
            .margin_bottom(6)
            .build();
        let scroll = gtk::ScrolledWindow::builder()
            .hscrollbar_policy(gtk::PolicyType::Never)
            .propagate_natural_width(true)
            .propagate_natural_height(true)
            .max_content_height(360)
            .child(&panel)
            .build();
        popover.set_child(Some(&scroll));
        let title = gtk::Label::builder().xalign(0.).build();
        title.add_css_class("heading");
        panel.append(&title);
        let text = option(&panel, "_Content", gtk::Entry::new());
        text.input.set_max_length(2000);
        let number = option(&panel, "_Number", spin(0., 9999., 1.));
        let colors = COLORS.map(|c| c.0);
        let color = option(&panel, "Co_lor", gtk::DropDown::from_strings(&colors));
        let mut fills = vec!["None"];
        fills.extend(colors);
        let fill = option(&panel, "_Fill", gtk::DropDown::from_strings(&fills));
        let width = option(&panel, "Stroke _width (px)", spin(1., 200., 4.));
        let font = option(&panel, "Font si_ze (px)", spin(1., 1000., 28.));
        let level = option(
            &panel,
            "Stren_gth",
            gtk::DropDown::from_strings(&["Light", "Medium", "Heavy"]),
        );
        level.input.set_selected(1);
        panel.append(&gtk::Separator::new(gtk::Orientation::Horizontal));
        let detail = gtk::Expander::with_mnemonic("_Precise coordinates (px)");
        panel.append(&detail);
        let bounds = gtk::Box::new(gtk::Orientation::Vertical, 8);
        bounds.set_margin_top(8);
        detail.set_child(Some(&bounds));
        let grid = gtk::Grid::builder()
            .column_spacing(6)
            .row_spacing(4)
            .build();
        bounds.append(&grid);
        let fields = Fields::new(&grid);
        let points = gtk::Entry::builder()
            .placeholder_text("x,y; x,y; …")
            .max_length(48000)
            .build();
        points.update_property(&[gtk::accessible::Property::Label(
            "Source-pixel points, separated by semicolons",
        )]);
        bounds.append(&points);
        let select = gtk::Button::with_mnemonic("_Set selection");
        select.set_halign(gtk::Align::End);
        bounds.append(&select);
        let error = gtk::Label::builder()
            .xalign(0.)
            .wrap(true)
            .max_width_chars(32)
            .accessible_role(gtk::AccessibleRole::Status)
            .visible(false)
            .build();
        error.add_css_class("error");
        panel.append(&error);
        Self {
            button,
            popover,
            select,
            title,
            detail,
            grid,
            fields,
            points,
            error,
            color,
            fill,
            width,
            font,
            number,
            level,
            text,
        }
    }
    pub fn configure(&self, k: Tool, name: &str) {
        self.title.set_text(&format!("{name} options"));
        self.grid.set_visible(!k.path());
        self.points.set_visible(k.path());
        self.color.row.set_visible(k.color());
        self.fill.row.set_visible(k.fill());
        self.width.row.set_visible(k.stroke());
        self.font.row.set_visible(k == Tool::Text);
        self.number.row.set_visible(k == Tool::Number);
        self.level.row.set_visible(k.strength());
        self.text
            .row
            .set_visible(matches!(k, Tool::Text | Tool::Emoji));
        self.text.input.set_text("");
        self.text
            .input
            .set_placeholder_text(Some(if k == Tool::Emoji {
                "Enter an emoji"
            } else {
                "Enter text"
            }));
        self.detail.set_expanded(false);
        self.error.set_visible(false);
    }
    pub fn sync(&self, pixels: &Pixbuf, selection: Option<&Selection>) {
        self.error.set_visible(false);
        self.fields.reset(pixels);
        self.points.set_text("");
        match selection {
            Some(Selection::Box(r)) => self.fields.set(*r),
            Some(Selection::Path(p)) => self.points.set_text(p),
            None => (),
        }
    }
    pub fn entered(&self, k: Tool) -> Selection {
        if k.path() {
            Selection::Path(self.points.text().to_string())
        } else {
            for s in &self.fields.0 {
                s.update();
            }
            Selection::Box(self.fields.region())
        }
    }
    pub fn error(&self, msg: &str) {
        self.error.set_text(msg);
        self.error.set_visible(true);
    }
    pub fn needs_content(&self, k: Tool) -> bool {
        matches!(k, Tool::Text | Tool::Emoji) && self.text.input.text().trim().is_empty()
    }
    pub fn request_content(&self) {
        self.detail.set_expanded(false);
        self.button.popup();
        self.text.input.grab_focus();
    }
    pub fn apply(&self, k: Tool, selection: &Selection, pixels: &Pixbuf) -> Result<Pixbuf> {
        for s in [&self.width.input, &self.font.input, &self.number.input] {
            s.update();
        }
        let (region, points) = match selection {
            Selection::Box(r) => (*r, ""),
            Selection::Path(p) => (Default::default(), p.as_str()),
        };
        let edit = Edit {
            tool: k,
            region,
            points,
            color: COLORS[self.color.input.selected() as usize].1,
            fill: self
                .fill
                .input
                .selected()
                .checked_sub(1)
                .map(|i| COLORS[i as usize].1),
            stroke: self.width.input.value(),
            font: self.font.input.value(),
            number: self.number.input.value_as_int(),
            content: &self.text.input.text(),
            level: ["light", "medium", "heavy"][self.level.input.selected() as usize],
        };
        let result = edit.apply(pixels)?;
        if k == Tool::Number {
            self.number
                .input
                .set_value(f64::from((edit.number + 1).min(9999)));
        }
        Ok(result)
    }
}
