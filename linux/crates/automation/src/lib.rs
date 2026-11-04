//! Opt-in Linux Agent Control. Receive `Event`s on the GTK main context and use
//! the same services as local gestures. No widget or capture backend runs here.
#![cfg(target_os = "linux")]

mod policy;
mod storage;
mod transport;

use async_channel::{Receiver, Sender};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::{
    collections::{BTreeMap, BTreeSet},
    io,
    net::Shutdown,
    os::unix::net::UnixStream,
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex, Weak,
        atomic::{AtomicBool, Ordering},
        mpsc::SyncSender,
    },
    time::{Duration, Instant},
};

use policy::CONTRACT;
use storage::{Audit, Bound, PrivateDir};

const APPROVAL_TIMEOUT: Duration = Duration::from_secs(120);
const MAX_PENDING: usize = 16;
const MAX_CLIENTS: usize = 128;

#[derive(Debug, Clone)]
pub struct ApiError {
    pub code: &'static str,
    pub message: String,
}

impl ApiError {
    pub fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
    fn code(code: &'static str) -> Self {
        Self::new(
            code,
            CONTRACT
                .errors
                .get(code)
                .map(String::as_str)
                .unwrap_or("Command failed."),
        )
    }
}

type Reply = Result<Value, ApiError>;

#[derive(Clone, Serialize, Deserialize)]
pub struct Client {
    /// Revocation handle; not a token, pairing ID, or token hash.
    pub id: String,
    pub name: String,
    pub scopes: Vec<String>,
}

type Clients = BTreeMap<String, Client>; // keys are SHA-256 digests of random bearer tokens

enum PairDecision {
    Pending,
    Approved(Client),
    Denied,
}
struct Pairing {
    name: String,
    scopes: Vec<String>,
    deadline: Instant,
    decision: PairDecision,
}

#[derive(Default)]
struct State {
    clients: Clients,
    pairings: BTreeMap<String, Pairing>,
    sockets: BTreeMap<u64, Arc<UnixStream>>,
}
struct Inner {
    enabled: AtomicBool,
    state: Mutex<State>,
    events: Sender<Event>,
    commands: BTreeSet<String>,
    roots: Vec<PathBuf>,
    dir: PrivateDir,
    audit: Mutex<Audit>,
}

pub enum Event {
    Pair {
        id: String,
        client_name: String,
        scopes: Vec<String>,
    },
    Approve(Approval),
    Execute(Call),
}

struct Ticket {
    inner: Weak<Inner>,
    socket: Arc<UnixStream>,
    hash: String,
    deadline: Instant,
}
impl Ticket {
    fn current(&self) -> Result<(), ApiError> {
        let inner = self
            .inner
            .upgrade()
            .ok_or_else(|| ApiError::code("automation_disabled"))?;
        let state = inner.state.lock().unwrap();
        if !inner.enabled.load(Ordering::Acquire) {
            return Err(ApiError::code("automation_disabled"));
        }
        if !state.clients.contains_key(&self.hash) {
            return Err(ApiError::code("unauthorized"));
        }
        if Instant::now() >= self.deadline || transport::closed(&self.socket) {
            return Err(ApiError::code("approval_denied"));
        }
        Ok(())
    }
}

pub struct Approval {
    pub client_name: String,
    pub command: String,
    pub params: Value,
    ticket: Arc<Ticket>,
    response: SyncSender<bool>,
}
impl Approval {
    /// Dismiss the prompt if the requester disconnected, was revoked, or timed out.
    pub fn active(&self) -> bool {
        self.ticket.current().is_ok()
    }
    pub fn decide(self, yes: bool) {
        let _ = self.response.try_send(yes && self.active());
    }
}

pub struct Call {
    pub command: String,
    pub params: Value,
    ticket: Arc<Ticket>,
    response: SyncSender<Reply>,
}
impl Call {
    /// Recheck immediately before applying work queued on the main context.
    pub fn active(&self) -> bool {
        self.ticket.current().is_ok()
    }
    pub fn reply(self, result: Result<Value, ApiError>) {
        let _ = self.response.try_send(result);
    }
}

pub struct Host {
    inner: Arc<Inner>,
    endpoint: PathBuf,
    bound: Mutex<Option<Bound>>,
}

impl Host {
    /// Call only after a human enables Agent Control. State and library directories
    /// are app-owned private directories; never derive them from client input.
    pub fn enable(
        app_id: &str,
        state_dir: &Path,
        library_roots: &[PathBuf],
        commands: &[&str],
    ) -> io::Result<(Self, Receiver<Event>)> {
        let runtime = std::env::var_os("XDG_RUNTIME_DIR")
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "XDG_RUNTIME_DIR is not set"))?;
        Self::enable_at(
            Path::new(&runtime),
            app_id,
            state_dir,
            library_roots,
            commands,
        )
    }

    fn enable_at(
        runtime: &Path,
        app_id: &str,
        state_dir: &Path,
        library_roots: &[PathBuf],
        commands: &[&str],
    ) -> io::Result<(Self, Receiver<Event>)> {
        let mut supported = BTreeSet::new();
        for &name in commands {
            if policy::command(name).is_none() || matches!(name, "library.upload" | "studio.export")
            {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    format!("Unsupported Linux command: {name}"),
                ));
            }
            supported.insert(name.to_owned());
        }
        for root in library_roots {
            storage::private_dir(root, false)?;
        }
        let state_dir = storage::state_path(state_dir)?;
        storage::private_dir(&state_dir, true)?;
        let (listener, endpoint, bound) = storage::bind(runtime, app_id)?;
        let dir = PrivateDir::open(&state_dir)?;
        let clients = storage::load(&dir.path)?;
        let audit = Audit::open(&dir.path)?;
        let (events, rx) = async_channel::bounded(32);
        let inner = Arc::new(Inner {
            enabled: AtomicBool::new(true),
            state: Mutex::new(State {
                clients,
                ..State::default()
            }),
            events,
            commands: supported,
            roots: library_roots.to_vec(),
            dir,
            audit: Mutex::new(audit),
        });
        let worker = inner.clone();
        std::thread::Builder::new()
            .name("agent-listener".into())
            .spawn(move || transport::listen(worker, listener))?;
        Ok((
            Self {
                inner,
                endpoint,
                bound: Mutex::new(Some(bound)),
            },
            rx,
        ))
    }

    pub fn endpoint(&self) -> &Path {
        &self.endpoint
    }

    pub fn clients(&self) -> Vec<Client> {
        let state = self.inner.state.lock().unwrap();
        // The settings UI refreshes on approval, before the CLI's next poll.
        // An approved but unclaimed pairing must already be visible and revocable.
        let unclaimed = state.pairings.values().filter_map(|p| match &p.decision {
            PairDecision::Approved(c) if Instant::now() < p.deadline => Some(c),
            _ => None,
        });
        state.clients.values().chain(unclaimed).cloned().collect()
    }

    pub fn revoke(&self, id: &str) -> io::Result<()> {
        let mut state = self.inner.state.lock().unwrap();
        if !self.inner.enabled.load(Ordering::Acquire) {
            return Err(io::ErrorKind::NotConnected.into());
        }
        for p in state.pairings.values_mut() {
            if matches!(&p.decision, PairDecision::Approved(c) if c.id == id) {
                p.decision = PairDecision::Denied;
            }
        }
        let mut clients = state.clients.clone();
        clients.retain(|_, c| c.id != id);
        if clients.len() != state.clients.len() {
            if let Err(err) = storage::save(&self.inner.dir.path, &clients) {
                // Never report a durable revocation when writing it failed. Stop serving
                // immediately; the UI can surface the error and the user may retry.
                self.inner.enabled.store(false, Ordering::Release);
                drop(state);
                self.disable();
                return Err(err);
            }
            state.clients = clients;
        }
        Ok(())
    }

    /// None or an empty scope selection declines. Grants must be a subset of the request.
    pub fn decide_pair(&self, id: &str, granted: Option<Vec<String>>) -> bool {
        let mut state = self.inner.state.lock().unwrap();
        let Some(pair) = state.pairings.get_mut(id) else {
            return false;
        };
        if Instant::now() >= pair.deadline || !matches!(pair.decision, PairDecision::Pending) {
            return false;
        }
        pair.decision = match granted {
            Some(mut scopes)
                if !scopes.is_empty() && scopes.iter().all(|s| pair.scopes.contains(s)) =>
            {
                scopes.sort();
                scopes.dedup();
                let Ok(client_id) = storage::random_key() else {
                    return false;
                };
                PairDecision::Approved(Client {
                    id: client_id,
                    name: pair.name.clone(),
                    scopes,
                })
            }
            Some(scopes) if scopes.is_empty() => PairDecision::Denied,
            Some(_) => return false,
            None => PairDecision::Denied,
        };
        true
    }

    pub fn pair_pending(&self, id: &str) -> bool {
        self.inner
            .state
            .lock()
            .unwrap()
            .pairings
            .get(id)
            .is_some_and(|p| {
                Instant::now() < p.deadline && matches!(p.decision, PairDecision::Pending)
            })
    }

    /// Stops accepting work, releases the endpoint, cancels prompts and shuts down
    /// existing sockets without waiting on a client or joining a worker on the UI thread.
    pub fn disable(&self) {
        let mut state = self.inner.state.lock().unwrap();
        self.inner.enabled.store(false, Ordering::Release);
        state.pairings.clear();
        self.inner.events.close();
        for socket in state.sockets.values() {
            let _ = socket.shutdown(Shutdown::Both);
        }
        self.bound.lock().unwrap().take();
    }
}
impl Drop for Host {
    fn drop(&mut self) {
        self.disable();
    }
}

#[cfg(test)]
mod tests;
