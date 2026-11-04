use std::{
    io::{self, BufRead, BufReader, Write},
    mem::MaybeUninit,
    net::Shutdown,
    os::{
        fd::AsRawFd,
        unix::net::{UnixListener, UnixStream},
    },
    path::Path,
    sync::{
        Arc,
        atomic::Ordering,
        mpsc::{self, RecvTimeoutError},
    },
    thread,
    time::{Duration, Instant},
};

use serde::Deserialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};

use crate::{
    APPROVAL_TIMEOUT, ApiError, Approval, Call, Client, Event, Inner, MAX_CLIENTS, MAX_PENDING,
    PairDecision, Pairing, Reply, Ticket,
    policy::{self, CONTRACT, Decision},
    storage,
};

const MAX_CONNECTIONS: usize = 16;
const MAX_FRAME: usize = 256 * 1024;
const FRAME_TIMEOUT: Duration = Duration::from_secs(15);
const CHECK_INTERVAL: Duration = Duration::from_millis(50);

fn hash(token: &str) -> String {
    hex::encode(Sha256::digest(token.as_bytes()))
}
fn internal(_: impl std::fmt::Display) -> ApiError {
    ApiError::code("internal")
}

fn poll(fd: i32, events: i16, timeout_ms: i32) -> io::Result<i16> {
    let mut p = libc::pollfd {
        fd,
        events,
        revents: 0,
    };
    if unsafe { libc::poll(&mut p, 1, timeout_ms) } < 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(p.revents)
}

pub(crate) fn closed(socket: &UnixStream) -> bool {
    // A write-half-close can mean "last request sent"; the client may still
    // be reading its response. HUP/ERR identify a fully lost local peer.
    match poll(socket.as_raw_fd(), 0, 0) {
        Ok(flags) => flags & (libc::POLLHUP | libc::POLLERR | libc::POLLNVAL) != 0,
        Err(e) => e.kind() != io::ErrorKind::Interrupted,
    }
}

fn same_uid(socket: &UnixStream) -> bool {
    let mut credentials = MaybeUninit::<libc::ucred>::uninit();
    let mut size = size_of::<libc::ucred>() as libc::socklen_t;
    // SO_PEERCRED is captured by the kernel at connect time, not supplied by the client.
    unsafe {
        libc::getsockopt(
            socket.as_raw_fd(),
            libc::SOL_SOCKET,
            libc::SO_PEERCRED,
            credentials.as_mut_ptr().cast(),
            &mut size,
        ) == 0
            && size as usize == size_of::<libc::ucred>()
            && credentials.assume_init().uid == storage::uid()
    }
}

pub(crate) fn listen(inner: Arc<Inner>, listener: UnixListener) {
    let mut next = 0u64;
    while inner.enabled.load(Ordering::Acquire) {
        match poll(listener.as_raw_fd(), libc::POLLIN, 100) {
            Ok(0) => continue,
            Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
            Err(_) => break,
            _ => {}
        }
        let Ok((stream, _)) = listener.accept() else {
            continue;
        };
        if !same_uid(&stream) {
            continue;
        }
        let socket = Arc::new(stream);
        let id = next;
        next = next.wrapping_add(1);
        {
            let mut state = inner.state.lock().unwrap();
            if !inner.enabled.load(Ordering::Acquire) {
                break;
            }
            if state.sockets.len() >= MAX_CONNECTIONS {
                continue;
            }
            state.sockets.insert(id, socket.clone());
        }
        let worker = inner.clone();
        if thread::Builder::new()
            .name("agent-request".into())
            .spawn(move || {
                let _ = serve(&worker, &socket);
                let _ = socket.shutdown(Shutdown::Both);
                worker.state.lock().unwrap().sockets.remove(&id);
            })
            .is_err()
        {
            inner.state.lock().unwrap().sockets.remove(&id);
        }
    }
}

#[derive(Deserialize)]
struct Request {
    #[serde(default)]
    id: Value,
    command: String,
    token: Option<String>,
    #[serde(default = "empty_object")]
    params: Value,
}
fn empty_object() -> Value {
    json!({})
}

// Bound both memory and wall time: slowly dripping one byte per read cannot hold a slot.
fn frame(reader: &mut BufReader<&UnixStream>) -> io::Result<Option<Vec<u8>>> {
    let end = Instant::now() + FRAME_TIMEOUT;
    let mut bytes = Vec::new();
    loop {
        let now = Instant::now();
        if now >= end {
            return Err(io::Error::from(io::ErrorKind::TimedOut));
        }
        reader.get_ref().set_read_timeout(Some(end - now))?;
        let data = reader.fill_buf()?;
        if data.is_empty() {
            return if bytes.is_empty() {
                Ok(None)
            } else {
                Err(io::Error::from(io::ErrorKind::UnexpectedEof))
            };
        }
        let newline = data.iter().position(|b| *b == b'\n');
        let n = newline.unwrap_or(data.len());
        if bytes.len() + n > MAX_FRAME {
            return Err(io::Error::from(io::ErrorKind::InvalidData));
        }
        bytes.extend_from_slice(&data[..n]);
        reader.consume(n + usize::from(newline.is_some()));
        if newline.is_some() {
            return Ok(Some(bytes));
        }
    }
}

fn write_reply(mut socket: &UnixStream, id: Value, result: Reply) -> io::Result<()> {
    let packet = match result {
        Ok(result) => json!({"id":id,"ok":true,"result":result}),
        Err(e) => json!({"id":id,"ok":false,"error":{"code":e.code,"message":e.message}}),
    };
    let mut bytes = serde_json::to_vec(&packet)?;
    bytes.push(b'\n');
    socket.write_all(&bytes)
}

fn serve(inner: &Arc<Inner>, socket: &Arc<UnixStream>) -> io::Result<()> {
    socket.set_write_timeout(Some(FRAME_TIMEOUT))?;
    let mut reader = BufReader::new(socket.as_ref());
    while inner.enabled.load(Ordering::Acquire) {
        let data = match frame(&mut reader) {
            Ok(Some(v)) => v,
            Ok(None) => break,
            Err(e) => {
                if e.kind() == io::ErrorKind::InvalidData
                    || e.kind() == io::ErrorKind::UnexpectedEof
                {
                    inner
                        .audit
                        .lock()
                        .unwrap()
                        .record(None, "invalid", "invalid_params")?;
                    write_reply(socket, Value::Null, Err(ApiError::code("invalid_params")))?;
                }
                break;
            }
        };
        let req: Request = match serde_json::from_slice::<Request>(&data) {
            Ok(r)
                if r.params.is_object()
                    && (r.id.is_null() || r.id.is_string() || r.id.is_number()) =>
            {
                r
            }
            _ => {
                inner
                    .audit
                    .lock()
                    .unwrap()
                    .record(None, "invalid", "invalid_params")?;
                write_reply(socket, Value::Null, Err(ApiError::code("invalid_params")))?;
                continue;
            }
        };
        let digest = req.token.as_deref().filter(|s| s.len() == 64).map(hash);
        let client = digest
            .as_ref()
            .and_then(|key| inner.state.lock().unwrap().clients.get(key).cloned());
        let client_id = client.as_ref().map(|c| c.id.as_str());
        // Audit only contract names and server-generated identity IDs. Never echo a
        // caller-chosen command name, request id, token, path, or parameter into the log.
        let name = if policy::command(&req.command).is_some()
            || CONTRACT.session_commands.contains(&req.command)
        {
            &req.command
        } else {
            "unknown"
        };
        inner
            .audit
            .lock()
            .unwrap()
            .record(client_id, name, "received")?;
        let result = dispatch(inner, socket, &req, digest.as_deref(), client.as_ref());
        let outcome = match &result {
            Ok(_) => "ok",
            Err(e) if CONTRACT.errors.contains_key(e.code) => e.code,
            Err(_) => "internal",
        };
        inner
            .audit
            .lock()
            .unwrap()
            .record(client_id, name, outcome)?;
        write_reply(socket, req.id, result)?;
    }
    Ok(())
}

fn dispatch(
    inner: &Arc<Inner>,
    socket: &Arc<UnixStream>,
    req: &Request,
    digest: Option<&str>,
    client: Option<&Client>,
) -> Reply {
    let enabled = inner.enabled.load(Ordering::Acquire);
    if !enabled {
        return Err(ApiError::code("automation_disabled"));
    }
    let decision = policy::decide(enabled, client.map(|c| c.scopes.as_slice()), &req.command);
    if client.is_some()
        && policy::command(&req.command).is_some()
        && !inner.commands.contains(&req.command)
    {
        return Err(ApiError::code("not_supported"));
    }
    if let Decision::Deny(code) = decision {
        return Err(ApiError::code(code));
    }
    match req.command.as_str() {
        "session.hello" => {
            let names: Vec<_> = CONTRACT
                .session_commands
                .iter()
                .chain(inner.commands.iter())
                .collect();
            let build = if std::env::var_os("FLATPAK_ID").is_some() {
                "flatpak"
            } else {
                "direct"
            };
            return Ok(json!({
                "app": "TinyClips", "version": env!("CARGO_PKG_VERSION"),
                "platform": "linux", "build": build, "protocolVersion": CONTRACT.protocol_version,
                "automationEnabled": true, "commands": names
            }));
        }
        "session.pair" => return pair(inner, &req.params),
        "session.pairStatus" => return pair_status(inner, &req.params),
        _ => {}
    }
    let client = client.ok_or_else(|| ApiError::code("unauthorized"))?;
    let digest = digest.ok_or_else(|| ApiError::code("unauthorized"))?;
    policy::validate(&req.command, &req.params)?;
    if let Some(path) = req.params["path"].as_str() {
        storage::check_input(Path::new(path), &inner.roots)?;
    }
    let ticket = |lifetime| {
        Arc::new(Ticket {
            inner: Arc::downgrade(inner),
            socket: socket.clone(),
            hash: digest.to_owned(),
            deadline: Instant::now() + lifetime,
        })
    };
    if decision == Decision::Approve {
        let t = ticket(APPROVAL_TIMEOUT);
        t.current()?;
        let (response, rx) = mpsc::sync_channel(1);
        let approval = Approval {
            client_name: client.name.clone(),
            command: req.command.clone(),
            params: req.params.clone(),
            ticket: t.clone(),
            response,
        };
        inner
            .events
            .try_send(Event::Approve(approval))
            .map_err(|_| ApiError::code("busy"))?;
        if !wait(rx, &t, "approval_denied")? {
            return Err(ApiError::code("approval_denied"));
        }
        t.current()?;
    }
    let lifetime = if req.command == "editor.trim" {
        Duration::from_secs(7200)
    } else {
        Duration::from_secs(150)
    };
    let t = ticket(lifetime);
    t.current()?;
    // The target may have changed while the user was considering the prompt.
    if let Some(path) = req.params["path"].as_str() {
        storage::check_input(Path::new(path), &inner.roots)?;
    }
    let (response, rx) = mpsc::sync_channel(1);
    let call = Call {
        command: req.command.clone(),
        params: req.params.clone(),
        ticket: t.clone(),
        response,
    };
    inner
        .events
        .try_send(Event::Execute(call))
        .map_err(|_| ApiError::code("busy"))?;
    wait(rx, &t, "internal")?
}

fn wait<T>(rx: mpsc::Receiver<T>, ticket: &Ticket, dropped: &'static str) -> Result<T, ApiError> {
    loop {
        ticket.current()?;
        match rx.recv_timeout(CHECK_INTERVAL) {
            Ok(v) => {
                ticket.current()?;
                return Ok(v);
            }
            Err(RecvTimeoutError::Disconnected) => return Err(ApiError::code(dropped)),
            Err(RecvTimeoutError::Timeout) => {}
        }
    }
}

fn pair(inner: &Inner, params: &Value) -> Reply {
    let name = params["clientName"]
        .as_str()
        .map(str::trim)
        .filter(|s| {
            !s.is_empty()
                && s.chars().count() <= 100
                && !s.chars().any(|c| {
                    c.is_control() || matches!(c,'\u{202a}'..='\u{202e}' | '\u{2066}'..='\u{2069}')
                })
        })
        .ok_or_else(|| ApiError::code("invalid_params"))?;
    let requested = params["scopes"]
        .as_array()
        .filter(|s| !s.is_empty() && s.len() <= CONTRACT.scopes.len())
        .ok_or_else(|| ApiError::code("invalid_params"))?;
    let mut scopes = Vec::new();
    for scope in requested {
        let s = scope
            .as_str()
            .filter(|s| CONTRACT.scopes.contains_key(*s))
            .ok_or_else(|| ApiError::code("invalid_params"))?;
        if inner
            .commands
            .iter()
            .any(|n| policy::command(n).is_some_and(|c| c.scope == s))
        {
            scopes.push(s.to_owned());
        }
    }
    scopes.sort();
    scopes.dedup();
    if scopes.is_empty() {
        return Err(ApiError::new(
            "not_supported",
            "No available commands use the requested scopes.",
        ));
    }
    let id = storage::random_key().map_err(internal)?;
    let mut state = inner.state.lock().unwrap();
    if !inner.enabled.load(Ordering::Acquire) {
        return Err(ApiError::code("automation_disabled"));
    }
    let now = Instant::now();
    state.pairings.retain(|_, p| now < p.deadline);
    if state.pairings.len() >= MAX_PENDING || state.clients.len() >= MAX_CLIENTS {
        return Err(ApiError::code("busy"));
    }
    let event = Event::Pair {
        id: id.clone(),
        client_name: name.to_owned(),
        scopes: scopes.clone(),
    };
    state.pairings.insert(
        id.clone(),
        Pairing {
            name: name.to_owned(),
            scopes,
            deadline: now + APPROVAL_TIMEOUT,
            decision: PairDecision::Pending,
        },
    );
    if inner.events.try_send(event).is_err() {
        state.pairings.remove(&id);
        return Err(ApiError::code("busy"));
    }
    Ok(json!({"pairingId":id}))
}

fn pair_status(inner: &Inner, params: &Value) -> Reply {
    let id = params["pairingId"]
        .as_str()
        .ok_or_else(|| ApiError::code("invalid_params"))?;
    let mut state = inner.state.lock().unwrap();
    if !inner.enabled.load(Ordering::Acquire) {
        return Err(ApiError::code("automation_disabled"));
    }
    let pair = state
        .pairings
        .get(id)
        .ok_or_else(|| ApiError::code("not_found"))?;
    if Instant::now() >= pair.deadline {
        state.pairings.remove(id);
        return Ok(json!({"status":"denied"}));
    }
    if matches!(pair.decision, PairDecision::Pending) {
        return Ok(json!({"status":"pending"}));
    }
    let pair = state.pairings.remove(id).unwrap();
    let PairDecision::Approved(client) = pair.decision else {
        return Ok(json!({"status":"denied"}));
    };
    if state.clients.len() >= MAX_CLIENTS {
        return Err(ApiError::code("busy"));
    }
    // Mint only on the successful one-time poll. No recoverable token is kept in
    // the pending queue, on disk, or in the audit trail, even between approval and poll.
    let token = storage::random_key().map_err(internal)?;
    let mut clients = state.clients.clone();
    let scopes = client.scopes.clone();
    clients.insert(hash(&token), client);
    storage::save(&inner.dir.path, &clients).map_err(internal)?;
    state.clients = clients;
    Ok(json!({"status":"approved","token":token,"scopes":scopes}))
}
