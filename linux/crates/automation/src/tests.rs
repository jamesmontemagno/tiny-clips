use super::*;
use serde_json::json;
use std::{
    fs::{self, DirBuilder},
    io::{BufRead, BufReader, Write},
    os::unix::{
        fs::{DirBuilderExt, MetadataExt, PermissionsExt, symlink},
        net::UnixListener,
    },
    thread,
};

const APP: &str = "com.refractored.tinyclips.Devel";

/// Run with TINYCLIPS_TEST_CLI=/absolute/path/to/tinyclips and Python 3 on PATH.
/// No real user's socket, credentials, preferences, or capture services are used.
#[test]
#[ignore = "requires the published Linux .NET CLI (TINYCLIPS_TEST_CLI) and Python 3"]
fn published_cli_and_mcp_use_live_linux_host() {
    use std::{
        os::unix::process::CommandExt,
        process::{Child, Command},
    };
    struct Children(Child);
    impl Drop for Children {
        fn drop(&mut self) {
            if matches!(self.0.try_wait(), Ok(None)) {
                // Kill only this test's dedicated group (Python and its CLI child).
                unsafe {
                    libc::kill(-(self.0.id() as i32), libc::SIGKILL);
                }
                let _ = self.0.wait();
            }
        }
    }
    let timeout = std::env::var("TINYCLIPS_TEST_IO_TIMEOUT_SECONDS")
        .ok()
        .map(|s| s.parse::<u64>().unwrap())
        .unwrap_or(90);
    assert!((1..=900).contains(&timeout));
    let cli =
        fs::canonicalize(std::env::var_os("TINYCLIPS_TEST_CLI").expect("Set TINYCLIPS_TEST_CLI"))
            .unwrap();
    let d = Directory::new();
    let (host, rx) = d.start(&["app.status", "settings.set"]);
    assert!(
        host.endpoint().as_os_str().len() <= 107,
        "Run from a shorter working directory for Linux UDS discovery"
    );
    let mut child = Children(
        Command::new("python3")
            .process_group(0)
            .arg("-c")
            .arg(include_str!("cli_interop.py"))
            .arg(cli)
            .env("XDG_RUNTIME_DIR", &d.0)
            .env("HOME", &d.0)
            .env("TINYCLIPS_AGENT_CONFIG", d.0.join("agent.json"))
            .env("DOTNET_BUNDLE_EXTRACT_BASE_DIR", d.0.join("dotnet-extract"))
            .env_remove("TINYCLIPS_AUTOMATION_ENDPOINT")
            .env_remove("TINYCLIPS_TOKEN")
            .env("TINYCLIPS_REQUEST_TIMEOUT_SECONDS", timeout.to_string())
            .env("TINYCLIPS_TEST_IO_TIMEOUT_SECONDS", timeout.to_string())
            .spawn()
            .unwrap(),
    );
    let (mut paired, mut calls) = (0, 0);
    let deadline = Instant::now() + Duration::from_secs(3 * timeout + 90);
    loop {
        while let Ok(event) = rx.try_recv() {
            match event {
                Event::Pair {
                    id,
                    client_name,
                    scopes,
                } => {
                    assert_eq!(client_name, "transport fixture");
                    assert_eq!(scopes, ["read", "settings"]);
                    assert_eq!(paired, 0);
                    assert!(host.decide_pair(&id, Some(vec!["read".into()])));
                    paired += 1;
                }
                Event::Execute(call) => {
                    assert!(call.active());
                    assert_eq!(call.command, "app.status");
                    calls += 1;
                    call.reply(Ok(json!({"app":"transport fixture", "platform":"linux"})));
                }
                Event::Approve(a) => {
                    a.decide(false);
                    panic!("Unexpected sensitive action");
                }
            }
        }
        if let Some(status) = child.0.try_wait().unwrap() {
            assert!(status.success(), "interop client: {status}");
            break;
        }
        assert!(Instant::now() < deadline, "interop run timed out");
        thread::sleep(Duration::from_millis(20));
    }
    assert_eq!((paired, calls), (1, 1));
}
struct Directory(PathBuf);
impl Directory {
    fn new() -> Self {
        let p = std::env::current_dir().unwrap().join(format!(
            ".agent-test-{}",
            &storage::random_key().unwrap()[..12]
        ));
        DirBuilder::new().mode(0o700).create(&p).unwrap();
        Self(p)
    }
    fn start(&self, commands: &[&str]) -> (Host, Receiver<Event>) {
        let lib = self.0.join("library");
        storage::private_dir(&lib, true).unwrap();
        Host::enable_at(&self.0, APP, &self.0.join("state"), &[lib], commands).unwrap()
    }
}
impl Drop for Directory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn connect(path: &Path) -> UnixStream {
    // Keep the UDS address below the kernel's length limit even in deep CI checkouts.
    let cwd = std::env::current_dir().unwrap();
    let s = UnixStream::connect(path.strip_prefix(&cwd).unwrap_or(path)).unwrap();
    s.set_read_timeout(Some(Duration::from_secs(4))).unwrap();
    s
}
fn read_line(s: &UnixStream) -> Value {
    let mut line = String::new();
    BufReader::new(s).read_line(&mut line).unwrap();
    serde_json::from_str(&line).unwrap()
}
fn request(host: &Host, command: &str, params: Value, token: Option<&str>) -> UnixStream {
    let mut s = connect(host.endpoint());
    writeln!(
        s,
        "{}",
        json!({"id":17,"command":command,"params":params,"token":token})
    )
    .unwrap();
    s
}
fn send(host: &Host, command: &str, params: Value, token: Option<&str>) -> Value {
    let response = read_line(&request(host, command, params, token));
    assert_eq!(response["id"], 17);
    response
}
fn event(rx: &Receiver<Event>) -> Event {
    let end = Instant::now() + Duration::from_secs(4);
    loop {
        if let Ok(event) = rx.try_recv() {
            return event;
        }
        assert!(Instant::now() < end, "No UI event");
        thread::sleep(Duration::from_millis(5));
    }
}
fn pair(host: &Host, events: &Receiver<Event>, wanted: &[&str], granted: &[&str]) -> String {
    let resp = send(
        host,
        "session.pair",
        json!({"clientName":"private agent name","scopes":wanted}),
        None,
    );
    let id = resp["result"]["pairingId"].as_str().unwrap();
    let Event::Pair {
        id: shown,
        client_name,
        scopes,
    } = event(events)
    else {
        panic!("No pairing prompt");
    };
    assert_eq!(shown, id);
    assert_eq!(client_name, "private agent name");
    assert!(granted.iter().all(|s| scopes.contains(&s.to_string())));
    assert!(host.pair_pending(id));
    let poll = || send(host, "session.pairStatus", json!({"pairingId":id}), None);
    assert_eq!(poll()["result"]["status"], "pending");
    assert!(!host.decide_pair(id, Some(vec!["invalid".into()])));
    assert!(host.decide_pair(id, Some(granted.iter().map(|s| s.to_string()).collect())));
    let resp = poll();
    assert_eq!(resp["result"]["status"], "approved");
    assert_eq!(resp["result"]["scopes"], json!(granted));
    let token = resp["result"]["token"].as_str().unwrap().to_owned();
    assert_eq!(poll()["error"]["code"], "not_found");
    assert_eq!(token.len(), 64);
    token
}

#[test]
fn socket_pairing_scopes_advertisement_restart_revocation_and_private_audit() {
    let d = Directory::new();
    let commands = ["app.status", "settings.set", "library.reveal"];
    let (host, rx) = d.start(&commands);
    let endpoint = host.endpoint().to_owned();
    assert_eq!(fs::metadata(&endpoint).unwrap().mode() & 0o777, 0o600);
    assert_eq!(
        fs::metadata(endpoint.parent().unwrap()).unwrap().mode() & 0o777,
        0o700
    );
    let hello = send(&host, "session.hello", json!({}), None);
    assert_eq!(hello["result"]["platform"], "linux");
    assert_eq!(
        hello["result"]["commands"],
        json!([
            "session.hello",
            "session.pair",
            "session.pairStatus",
            "app.status",
            "library.reveal",
            "settings.set"
        ])
    );
    assert_eq!(
        send(&host, "app.status", json!({}), None)["error"]["code"],
        "unauthorized"
    );
    let token = pair(&host, &rx, &["read", "settings", "capture"], &["read"]);
    let stored = fs::read_to_string(d.0.join("state/automation-clients.json")).unwrap();
    use sha2::{Digest, Sha256};
    assert!(stored.contains(&hex::encode(Sha256::digest(token.as_bytes()))));
    assert!(!stored.contains(&token));
    assert_eq!(
        send(&host, "settings.set", json!({}), Some(&token))["error"]["code"],
        "missing_scope"
    );
    assert_eq!(
        send(&host, "capture.screenshot", json!({}), Some(&token))["error"]["code"],
        "not_supported"
    );
    assert_eq!(
        send(&host, "secret.command", json!({}), Some(&token))["error"]["code"],
        "unknown_command"
    );
    let s = request(&host, "app.status", json!({}), Some(&token));
    let Event::Execute(call) = event(&rx) else {
        panic!("not dispatched");
    };
    assert!(call.active());
    assert_eq!(call.command, "app.status");
    s.shutdown(Shutdown::Write).unwrap();
    assert!(call.active());
    call.reply(Ok(json!({"platform":"linux"})));
    assert_eq!(read_line(&s)["result"]["platform"], "linux");
    let pending = send(
        &host,
        "session.pair",
        json!({"clientName":"second","scopes":["read"]}),
        None,
    );
    let pending = pending["result"]["pairingId"].as_str().unwrap();
    assert!(host.pair_pending(pending));
    host.disable();
    assert!(!endpoint.exists());
    assert!(!host.pair_pending(pending));
    assert!(!host.decide_pair(pending, Some(vec!["read".into()])));
    let mut eof = String::new();
    assert_eq!(BufReader::new(&s).read_line(&mut eof).unwrap(), 0);
    let (again, _) = d.start(&commands);
    let id = again.clients().pop().unwrap().id;
    again.revoke(&id).unwrap();
    assert!(again.clients().is_empty());
    assert_eq!(
        send(&again, "app.status", json!({}), Some(&token))["error"]["code"],
        "unauthorized"
    );
    for file in ["automation-clients.json", "automation-audit.jsonl"] {
        let path = d.0.join("state").join(file);
        let text = fs::read_to_string(&path).unwrap();
        assert!(!text.contains(&token) && !text.contains(pending));
        assert_eq!(fs::metadata(path).unwrap().mode() & 0o777, 0o600);
        if file.ends_with("jsonl") {
            assert!(!text.contains("private agent name") && !text.contains("secret.command"));
            assert!(text.lines().any(
                |line| serde_json::from_str::<Value>(line).unwrap()["outcome"] == "missing_scope"
            ));
        }
    }
}

#[test]
fn sensitive_requires_fresh_approval_and_disconnect_or_revocation_cancels_it() {
    let d = Directory::new();
    let (host, rx) = d.start(&["recording.discard"]);
    let token = pair(&host, &rx, &["discard"], &["discard"]);
    let start = || {
        let s = request(&host, "recording.discard", json!({}), Some(&token));
        let Event::Approve(a) = event(&rx) else {
            panic!("no fresh approval");
        };
        assert!(a.active());
        (s, a)
    };
    let (s, a) = start();
    a.decide(false);
    assert_eq!(read_line(&s)["error"]["code"], "approval_denied");
    let (s, a) = start();
    a.decide(true);
    let Event::Execute(call) = event(&rx) else {
        panic!("no dispatch");
    };
    call.reply(Ok(json!({})));
    assert_eq!(read_line(&s)["ok"], true);
    let (s, a) = start();
    drop(s);
    let deadline = Instant::now() + Duration::from_secs(2);
    while a.active() && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(10));
    }
    assert!(!a.active());
    a.decide(true);
    let (s, a) = start();
    host.revoke(&host.clients()[0].id).unwrap();
    assert!(!a.active());
    a.decide(true);
    assert_eq!(read_line(&s)["error"]["code"], "unauthorized");
    assert!(rx.try_recv().is_err());
}

#[test]
fn queued_commands_and_pending_pairs_expire_or_cancel_without_execution() {
    let d = Directory::new();
    let (host, rx) = d.start(&["app.status"]);
    let token = pair(&host, &rx, &["read"], &["read"]);
    let s = request(&host, "app.status", json!({}), Some(&token));
    let Event::Execute(call) = event(&rx) else {
        panic!()
    };
    assert!(call.active());
    drop(s);
    assert!(!call.active());
    let p = send(
        &host,
        "session.pair",
        json!({"clientName":"late", "scopes":["read"]}),
        None,
    );
    let id = p["result"]["pairingId"].as_str().unwrap();
    host.inner
        .state
        .lock()
        .unwrap()
        .pairings
        .get_mut(id)
        .unwrap()
        .deadline = Instant::now();
    assert!(!host.decide_pair(id, Some(vec!["read".into()])));
    assert!(!host.pair_pending(id));
    assert_eq!(
        send(&host, "session.pairStatus", json!({"pairingId":id}), None)["result"]["status"],
        "denied"
    );
    let p = send(
        &host,
        "session.pair",
        json!({"clientName":"declined", "scopes":["read"]}),
        None,
    );
    let id = p["result"]["pairingId"].as_str().unwrap();
    assert!(host.decide_pair(id, None));
    assert_eq!(
        send(&host, "session.pairStatus", json!({"pairingId":id}), None)["result"]["status"],
        "denied"
    );
}

#[test]
fn approved_pairing_is_revocable_before_cli_can_claim_a_token() {
    let d = Directory::new();
    let (host, _rx) = d.start(&["app.status"]);
    let packet = send(
        &host,
        "session.pair",
        json!({"clientName":"agent", "scopes":["read"]}),
        None,
    );
    let id = packet["result"]["pairingId"].as_str().unwrap();
    assert!(host.decide_pair(id, Some(vec!["read".into()])));
    assert_eq!(host.clients().len(), 1);
    host.revoke(&host.clients()[0].id).unwrap();
    assert!(host.clients().is_empty());
    let poll = send(&host, "session.pairStatus", json!({"pairingId":id}), None);
    assert_eq!(poll["result"]["status"], "denied");
    assert!(poll["result"]["token"].is_null());
}

#[test]
fn simultaneous_connection_count_is_bounded() {
    let d = Directory::new();
    let (host, _rx) = d.start(&["app.status"]);
    let mut sockets = Vec::new();
    for _ in 0..16 {
        let s = request(&host, "session.hello", json!({}), None);
        assert_eq!(read_line(&s)["ok"], true);
        sockets.push(s);
    }
    let s = connect(host.endpoint());
    let mut line = String::new();
    assert_eq!(BufReader::new(&s).read_line(&mut line).unwrap(), 0);
    host.disable();
    for s in sockets {
        assert_eq!(BufReader::new(&s).read_line(&mut line).unwrap(), 0);
    }
}

#[test]
fn path_traversal_symlinks_and_cross_library_files_are_rejected() {
    let d = Directory::new();
    let (host, rx) = d.start(&["library.reveal"]);
    let token = pair(&host, &rx, &["read"], &["read"]);
    let root = d.0.join("library");
    let good = root.join("ok.png");
    let outside = d.0.join("outside.png");
    fs::write(&good, "inside").unwrap();
    fs::write(&outside, "outside").unwrap();
    symlink(&outside, root.join("bad.png")).unwrap();
    fs::hard_link(&outside, root.join("link.png")).unwrap();
    for path in [
        outside,
        root.join("../outside.png"),
        root.join("bad.png"),
        root.join("link.png"),
        PathBuf::from("ok.png"),
    ] {
        let result = send(&host, "library.reveal", json!({"path":path}), Some(&token));
        assert_eq!(result["error"]["code"], "invalid_params");
    }
    assert!(rx.try_recv().is_err());
    let s = request(&host, "library.reveal", json!({"path":good}), Some(&token));
    let Event::Execute(call) = event(&rx) else {
        panic!()
    };
    call.reply(Err(ApiError::new("not_found", "Capture disappeared.")));
    assert_eq!(read_line(&s)["error"]["code"], "not_found");
}

#[test]
fn never_replaces_live_socket_or_symlink_and_can_recover_stale_socket() {
    let d = Directory::new();
    let dir = d.0.join("app").join(APP);
    storage::private_dir(&dir, true).unwrap();
    let endpoint = dir.join("automation.sock");
    let sentinel = d.0.join("sentinel");
    fs::write(&sentinel, b"safe").unwrap();
    symlink(&sentinel, &endpoint).unwrap();
    assert!(Host::enable_at(&d.0, APP, &d.0.join("state"), &[], &[]).is_err());
    assert_eq!(fs::read(&sentinel).unwrap(), b"safe");
    fs::remove_file(&endpoint).unwrap();
    let cwd = std::env::current_dir().unwrap();
    let live = UnixListener::bind(endpoint.strip_prefix(cwd).unwrap()).unwrap();
    assert!(Host::enable_at(&d.0, APP, &d.0.join("state"), &[], &[]).is_err());
    assert!(endpoint.exists());
    drop(live);
    let (host, _) = d.start(&["app.status"]);
    assert!(Host::enable_at(&d.0, APP, &d.0.join("state"), &[], &[]).is_err());
    assert_eq!(send(&host, "session.hello", json!({}), None)["ok"], true);
    fs::remove_file(&endpoint).unwrap();
    fs::write(&endpoint, b"replacement").unwrap();
    drop(host);
    assert_eq!(fs::read(&endpoint).unwrap(), b"replacement");
    fs::set_permissions(&d.0, fs::Permissions::from_mode(0o755)).unwrap();
    assert!(Host::enable_at(&d.0, APP, &d.0.join("state"), &[], &[]).is_err());
}

#[test]
fn bounds_frame_and_pending_requests_and_handles_multiple_lines_in_order() {
    let d = Directory::new();
    let (host, _rx) = d.start(&["app.status"]);
    let mut s = connect(host.endpoint());
    s.write_all(
        b"{\"id\":1,\"command\":\"session.hello\"}\n{\"id\":2,\"command\":\"app.status\"}\n",
    )
    .unwrap();
    let mut r = BufReader::new(&s);
    let mut line = String::new();
    for id in [1, 2] {
        r.read_line(&mut line).unwrap();
        assert_eq!(serde_json::from_str::<Value>(&line).unwrap()["id"], id);
        line.clear();
    }
    s.write_all(&vec![b'x'; 256 * 1024 + 1]).unwrap();
    assert_eq!(read_line(&s)["error"]["code"], "invalid_params");
    for _ in 0..MAX_PENDING {
        assert_eq!(
            send(
                &host,
                "session.pair",
                json!({"clientName":"ask","scopes":["read"]}),
                None
            )["ok"],
            true
        );
    }
    assert_eq!(
        send(
            &host,
            "session.pair",
            json!({"clientName":"extra","scopes":["read"]}),
            None
        )["error"]["code"],
        "busy"
    );
}
