use crate::{Clip, ClipError, ClipKind, ClipStore, Result, open_editor};
use adw::prelude::*;
use ashpd::{
    WindowIdentifier,
    desktop::open_uri::{OpenDirectoryRequest, OpenFileRequest},
};
use gtk::{gdk, gio, glib};
use std::{
    cell::RefCell,
    path::{Path, PathBuf},
    rc::Rc,
};

pub fn copy_clip(store: &ClipStore, window: &impl IsA<gtk::Window>, path: &Path) -> Result<()> {
    let (_file, clip) = store.resolve(path)?;
    let files = gdk::FileList::from_array(&[gio::File::for_path(&clip.path)]);
    let mut formats = vec![gdk::ContentProvider::for_value(&files.to_value())];
    if clip.kind == ClipKind::Screenshot {
        let image = gdk::Texture::for_pixbuf(&store.load_png(path)?);
        formats.push(gdk::ContentProvider::for_value(&image.to_value()));
    }
    window
        .as_ref()
        .clipboard()
        .set_content(Some(&gdk::ContentProvider::new_union(&formats)))
        .map_err(ClipError::internal)
}

/// OpenDirectory takes the file descriptor to reveal, not the pathname of its parent.
pub async fn reveal_clip(
    store: &ClipStore,
    window: &impl IsA<gtk::Window>,
    path: &Path,
) -> Result<()> {
    let (file, _) = store.resolve(path)?;
    let parent: &gtk::Window = window.as_ref();
    OpenDirectoryRequest::default()
        .identifier(WindowIdentifier::from_native(parent).await)
        .send(&file)
        .await
        .and_then(|r| r.response())
        .map_err(ClipError::internal)
}
async fn open_clip(store: &ClipStore, win: &adw::Window, path: &Path) -> Result<()> {
    let (file, clip) = store.resolve(path)?;
    match clip.kind {
        ClipKind::Screenshot => open_editor(win, store, path),
        ClipKind::Video => OpenFileRequest::default()
            .identifier(WindowIdentifier::from_native(win).await)
            .writeable(false)
            .send_file(&file)
            .await
            .and_then(|r| r.response())
            .map_err(ClipError::internal),
    }
}

pub(crate) fn window(
    parent: &impl IsA<gtk::Window>,
    title: &str,
    w: i32,
    h: i32,
) -> (adw::Window, gtk::Box) {
    let parent: &gtk::Window = parent.as_ref();
    let win = adw::Window::builder()
        .title(title)
        .transient_for(parent)
        .default_width(w)
        .default_height(h)
        .build();
    win.set_application(parent.application().as_ref());
    win.set_destroy_with_parent(true);
    let outer = gtk::Box::new(gtk::Orientation::Vertical, 0);
    outer.append(&adw::HeaderBar::new());
    let body = gtk::Box::new(gtk::Orientation::Vertical, 8);
    body.set_margin_top(8);
    body.set_margin_bottom(12);
    body.set_margin_start(12);
    body.set_margin_end(12);
    outer.append(&body);
    win.set_content(Some(&outer));
    (win, body)
}
fn matches_query(name: &str, kind: ClipKind, query: &str) -> bool {
    query.is_empty() || name.to_lowercase().contains(query) || kind.name().contains(query)
}
struct Library {
    store: ClipStore,
    paths: RefCell<Vec<PathBuf>>,
    recent: RefCell<Vec<Clip>>,
    search: gtk::SearchEntry,
    loading: std::cell::Cell<bool>,
    list: gtk::ListBox,
    status: gtk::Label,
}
impl Library {
    fn refresh(self: &Rc<Self>) {
        if self.loading.replace(true) {
            return;
        }
        self.status.set_text("Loading recent captures…");
        let store = self.store.clone();
        let weak = Rc::downgrade(self);
        glib::spawn_future_local(async move {
            let result = gio::spawn_blocking(move || store.list(None, 500)).await;
            let Some(state) = weak.upgrade() else {
                return;
            };
            state.loading.set(false);
            match result {
                Ok(Ok(clips)) => {
                    state.recent.replace(clips);
                    state.show();
                }
                Ok(Err(e)) => state.status.set_text(&e.message),
                Err(_) => state.status.set_text("Could not read the clips library."),
            }
        });
    }
    fn show(&self) {
        let previous = self.selected();
        let clips = self.recent.borrow();
        let query = self.search.text().trim().to_lowercase();
        while let Some(row) = self.list.first_child() {
            self.list.remove(&row);
        }
        self.paths.borrow_mut().clear();
        for c in clips
            .iter()
            .filter(|c| matches_query(c.name(), c.kind, &query))
        {
            self.add_row(c);
        }
        let visible = self.paths.borrow();
        self.status
            .set_text(&match (clips.len(), query.is_empty()) {
                (0, _) => "No captures yet. New screenshots and recordings appear here.".into(),
                (n, false) => format!("{} matches among {n} recent captures.", visible.len()),
                (500, true) => "Showing the 500 most recent captures.".into(),
                (n, true) => format!("{n} local capture{}.", if n == 1 { "" } else { "s" }),
            });
        let index = previous
            .and_then(|p| visible.iter().position(|path| path == &p))
            .unwrap_or(0);
        if let Some(r) = self.list.row_at_index(index as i32) {
            self.list.select_row(Some(&r));
        }
    }
    fn add_row(&self, clip: &Clip) {
        let label = gtk::Label::new(Some(&format!(
            "{}\n{}   ·   {}   ·   {} KiB",
            clip.name(),
            clip.kind.name(),
            clip.created_at(),
            clip.size_bytes.div_ceil(1024)
        )));
        label.set_halign(gtk::Align::Start);
        label.set_xalign(0.);
        label.set_ellipsize(gtk::pango::EllipsizeMode::Middle);
        label.set_margin_top(6);
        label.set_margin_bottom(6);
        label.set_margin_start(6);
        self.paths.borrow_mut().push(clip.path.clone());
        self.list.append(&label);
    }
    fn selected(&self) -> Option<PathBuf> {
        let idx = self.list.selected_row()?.index();
        self.paths.borrow().get(idx as usize).cloned()
    }
}
pub fn open_library(parent: &impl IsA<gtk::Window>, store: &ClipStore) -> Result<()> {
    let (win, body) = window(parent, "Clips Library", 660, 490);
    let search = gtk::SearchEntry::new();
    search.set_placeholder_text(Some("Search up to 500 recent captures by name or kind"));
    search.update_property(&[gtk::accessible::Property::Label(
        "Search recent captures by name or kind",
    )]);
    body.append(&search);
    let list = gtk::ListBox::new();
    list.set_selection_mode(gtk::SelectionMode::Single);
    list.set_activate_on_single_click(false);
    list.add_css_class("boxed-list");
    list.update_property(&[gtk::accessible::Property::Label(
        "Local screenshots and recordings",
    )]);
    let scroll = gtk::ScrolledWindow::builder()
        .child(&list)
        .vexpand(true)
        .hscrollbar_policy(gtk::PolicyType::Never)
        .build();
    body.append(&scroll);
    let actions = gtk::Box::new(gtk::Orientation::Horizontal, 6);
    body.append(&actions);
    let status = gtk::Label::new(None);
    status.set_wrap(true);
    status.set_xalign(0.);
    body.append(&status);
    let lib = Rc::new(Library {
        store: store.clone(),
        paths: RefCell::new(Vec::new()),
        recent: RefCell::new(Vec::new()),
        search: search.clone(),
        loading: std::cell::Cell::new(false),
        list: list.clone(),
        status,
    });
    let owner = Rc::downgrade(&lib);
    search.connect_search_changed(move |_| {
        if let Some(lib) = owner.upgrade() {
            lib.show();
        }
    });
    let mut selection_actions = vec![];
    for (i, label) in ["_Open", "_Reveal", "_Copy", "Re_fresh"]
        .into_iter()
        .enumerate()
    {
        let button = gtk::Button::with_mnemonic(label);
        actions.append(&button);
        if i < 3 {
            selection_actions.push(button.clone());
        }
        let state = lib.clone();
        let weak = win.downgrade();
        button.connect_clicked(move |_| {
            let Some(win) = weak.upgrade() else {
                return;
            };
            if i == 3 {
                state.refresh();
                return;
            }
            let Some(path) = state.selected() else {
                return;
            };
            let state = state.clone();
            glib::MainContext::default().spawn_local(async move {
                let result = match i {
                    0 => open_clip(&state.store, &win, &path).await,
                    1 => reveal_clip(&state.store, &win, &path).await,
                    _ => copy_clip(&state.store, &win, &path),
                };
                match result {
                    Ok(()) => state.status.set_text(
                        [
                            "Opened capture.",
                            "Revealed capture.",
                            "Copied capture to the clipboard.",
                        ][i],
                    ),
                    Err(e) => state.status.set_text(&e.message),
                }
            });
        });
    }
    let open = selection_actions[0].downgrade();
    list.connect_row_activated(move |_, _| {
        if let Some(button) = open.upgrade() {
            button.emit_clicked();
        }
    });
    let weak_actions: Vec<_> = selection_actions.iter().map(|b| b.downgrade()).collect();
    list.connect_row_selected(move |_, r| {
        for b in &weak_actions {
            if let Some(b) = b.upgrade() {
                b.set_sensitive(r.is_some());
            }
        }
    });
    for b in selection_actions {
        b.set_sensitive(false);
    }
    lib.refresh();
    win.present();
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn search_matches_name_or_kind_in_memory() {
        assert!(matches_query("Demo.PNG", ClipKind::Screenshot, "demo"));
        assert!(matches_query(
            "Demo.PNG",
            ClipKind::Screenshot,
            "screenshot"
        ));
        assert!(!matches_query("Demo.PNG", ClipKind::Screenshot, "video"));
        assert!(matches_query("Clip.mp4", ClipKind::Video, ""));
    }
}
