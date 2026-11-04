use adw::prelude::*;
use gtk::glib;
use std::{cell::RefCell, path::PathBuf, rc::Rc, time::Duration};
use tinyclips_automation::{Approval, Call, Event, Host};

pub struct AgentControl {
    pub panel: gtk::Box,
    enabled: gtk::CheckButton,
    status: gtk::Label,
    clients: gtk::Box,
    visible_clients: RefCell<Vec<String>>,
    host: RefCell<Option<Rc<Host>>>,
}

impl AgentControl {
    pub fn new() -> Rc<Self> {
        let panel = gtk::Box::new(gtk::Orientation::Vertical, 6);
        let enabled = gtk::CheckButton::with_label("Allow local agents to control Tiny Clips");
        let status = gtk::Label::builder()
            .xalign(0.0)
            .wrap(true)
            .selectable(true)
            .label("Off. No automation listener.")
            .build();
        let clients = gtk::Box::new(gtk::Orientation::Vertical, 4);
        for item in [
            enabled.upcast_ref::<gtk::Widget>(),
            status.upcast_ref(),
            clients.upcast_ref(),
        ] {
            panel.append(item);
        }
        Rc::new(Self {
            panel,
            enabled,
            status,
            clients,
            visible_clients: RefCell::new(Vec::new()),
            host: RefCell::new(None),
        })
    }

    pub fn active(&self) -> bool {
        self.host.borrow().is_some()
    }
    pub fn disable(&self) {
        self.host.borrow_mut().take();
        self.enabled.set_active(false);
    }

    pub fn connect(
        self: &Rc<Self>,
        window: &adw::ApplicationWindow,
        data: PathBuf,
        roots: Vec<PathBuf>,
        commands: Vec<&'static str>,
        dispatch: impl Fn(Call) + 'static,
    ) {
        let owner = Rc::downgrade(self);
        let window = window.downgrade();
        let dispatch = Rc::new(dispatch);
        self.enabled.connect_toggled(move |toggle| {
            let Some(this) = owner.upgrade() else { return };
            if !toggle.is_active() {
                this.host.borrow_mut().take();
                this.status.set_label("Off. No automation listener.");
                this.refresh_clients();
                return;
            }
            let (host, events) = match Host::enable(crate::APP_ID, &data, &roots, &commands) {
                Ok(pair) => pair,
                Err(err) => {
                    toggle.set_active(false);
                    this.status
                        .set_label(&format!("Could not enable Agent Control: {err}"));
                    return;
                }
            };
            this.status
                .set_label(&format!("On • {}", host.endpoint().display()));
            let host = Rc::new(host);
            let weak_host = Rc::downgrade(&host);
            this.host.replace(Some(host));
            this.refresh_clients();
            let owner = owner.clone();
            let window = window.clone();
            let dispatch = dispatch.clone();
            glib::spawn_future_local(async move {
                while let Ok(event) = events.recv().await {
                    let (Some(this), Some(parent), Some(host)) =
                        (owner.upgrade(), window.upgrade(), weak_host.upgrade())
                    else {
                        break;
                    };
                    match event {
                        Event::Pair {
                            id,
                            client_name,
                            scopes,
                        } => this.pair(&parent, &host, id, client_name, scopes),
                        Event::Approve(request) => show_approval(&parent, request),
                        Event::Execute(call) if call.active() => dispatch(call),
                        _ => {}
                    }
                }
            });
        });
    }

    fn pair(
        self: &Rc<Self>,
        parent: &adw::ApplicationWindow,
        host: &Rc<Host>,
        id: String,
        name: String,
        scopes: Vec<String>,
    ) {
        if !host.pair_pending(&id) {
            return;
        }
        let dialog = approval_dialog("Pair a local agent", "Pair");
        dialog.set_body(&format!(
            "{name} requests these permissions. Select only the ones you want to grant."
        ));
        let area = gtk::Box::new(gtk::Orientation::Vertical, 4);
        dialog.set_extra_child(Some(&area));
        let mut checks = Vec::new();
        for scope in scopes {
            let check = gtk::CheckButton::with_label(&scope);
            check.set_active(matches!(
                scope.as_str(),
                "read" | "capture" | "record" | "edit" | "export"
            ));
            area.append(&check);
            checks.push((scope, check));
        }
        let weak = Rc::downgrade(host);
        let request_id = id.clone();
        expire_dialog(&dialog, move || {
            weak.upgrade().is_some_and(|h| h.pair_pending(&request_id))
        });
        let weak = Rc::downgrade(host);
        let owner = Rc::downgrade(self);
        dialog.connect_response(None, move |_, response| {
            if let Some(host) = weak.upgrade() {
                let grant = (response == "allow").then(|| {
                    checks
                        .iter()
                        .filter(|(_, c)| c.is_active())
                        .map(|(s, _)| s.clone())
                        .collect()
                });
                host.decide_pair(&id, grant);
                if let Some(this) = owner.upgrade() {
                    this.refresh_clients();
                }
            }
        });
        dialog.present(Some(parent));
    }

    pub fn refresh_clients(self: &Rc<Self>) {
        let host = self.host.borrow().clone();
        let clients = host.as_ref().map(|host| host.clients()).unwrap_or_default();
        let ids: Vec<_> = clients.iter().map(|client| client.id.clone()).collect();
        if *self.visible_clients.borrow() == ids {
            return;
        }
        self.visible_clients.replace(ids);
        while let Some(child) = self.clients.first_child() {
            self.clients.remove(&child);
        }
        let Some(host) = host else {
            return;
        };
        for client in clients {
            let button = gtk::Button::with_label(&format!(
                "Revoke {} ({})",
                client.name,
                client.scopes.join(", ")
            ));
            let owner = Rc::downgrade(self);
            let weak = Rc::downgrade(&host);
            button.connect_clicked(move |_| {
                if let (Some(owner), Some(host)) = (owner.upgrade(), weak.upgrade()) {
                    if let Err(err) = host.revoke(&client.id) {
                        owner
                            .status
                            .set_label(&format!("Could not revoke client: {err}"));
                    }
                    owner.refresh_clients();
                }
            });
            self.clients.append(&button);
        }
    }
}

fn approval_dialog(title: &str, yes: &str) -> adw::AlertDialog {
    let dialog = adw::AlertDialog::builder()
        .heading(title)
        .body_use_markup(false)
        .build();
    dialog.add_responses(&[("deny", "Deny"), ("allow", yes)]);
    dialog.set_default_response(Some("deny"));
    dialog.set_close_response("deny");
    dialog
}

fn expire_dialog(dialog: &adw::AlertDialog, active: impl Fn() -> bool + 'static) {
    let weak = dialog.downgrade();
    glib::timeout_add_local(Duration::from_millis(250), move || {
        if let Some(dialog) = weak.upgrade().filter(|d| d.is_visible()) {
            if active() {
                return glib::ControlFlow::Continue;
            }
            dialog.force_close();
        }
        glib::ControlFlow::Break
    });
}

fn show_approval(parent: &adw::ApplicationWindow, approval: Approval) {
    if !approval.active() {
        return;
    }
    let dialog = approval_dialog("Approve sensitive action", "Allow once");
    let target = approval.params["path"]
        .as_str()
        .unwrap_or("current capture");
    dialog.set_body(&format!(
        "{} requests {} for {target}.",
        approval.client_name, approval.command
    ));
    let pending = Rc::new(RefCell::new(Some(approval)));
    let watch = Rc::clone(&pending);
    expire_dialog(&dialog, move || {
        watch.borrow().as_ref().is_some_and(|v| v.active())
    });
    dialog.connect_response(None, move |_, response| {
        if let Some(action) = pending.take() {
            action.decide(response == "allow");
        }
    });
    dialog.present(Some(parent));
}
