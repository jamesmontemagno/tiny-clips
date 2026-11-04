//! These subprocesses are synthetic-only, owned by the test, and reaped even on panic.
use gst::prelude::*;
use std::{
    fs::{self, DirBuilder, OpenOptions},
    io::Write,
    os::unix::fs::{DirBuilderExt, MetadataExt, OpenOptionsExt},
    path::{Path, PathBuf},
    process::{Child, Command, ExitStatus, Stdio},
    time::{Duration, Instant},
};

struct Library(PathBuf);
impl Library {
    fn new() -> Self {
        let path = std::env::current_dir().unwrap().join(format!(
            ".interruption-test-{}",
            gst::glib::uuid_string_random()
        ));
        DirBuilder::new().mode(0o700).create(&path).unwrap();
        Self(path)
    }
}
impl Drop for Library {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

struct SyntheticChild(Child);
impl SyntheticChild {
    fn start(dir: &Path, seconds: u32) -> Self {
        let cargo_bin = Path::new(env!("CARGO_BIN_EXE_tinyclips-media-check"));
        // A cross-built test ELF can be copied next to the checker on a Linux guest.
        let checker = if cargo_bin.is_file() {
            cargo_bin.to_owned()
        } else {
            std::env::current_exe()
                .unwrap()
                .parent()
                .unwrap()
                .join("tinyclips-media-check")
        };
        Self(
            Command::new(checker)
                .args([
                    "--synthetic",
                    "--seconds",
                    &seconds.to_string(),
                    "--output-dir",
                ])
                .arg(dir)
                .env_remove("DISPLAY")
                .env_remove("WAYLAND_DISPLAY")
                .env("PULSE_SERVER", "unix:/nonexistent-tinyclips-test-pulse")
                .env(
                    "DBUS_SESSION_BUS_ADDRESS",
                    "unix:path=/nonexistent-tinyclips-test-bus",
                )
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .spawn()
                .unwrap(),
        )
    }
    fn kill_and_reap(&mut self) {
        if self.0.try_wait().unwrap().is_none() {
            self.0.kill().unwrap();
        }
        self.0.wait().unwrap();
    }
    fn wait(&mut self) -> ExitStatus {
        let deadline = Instant::now() + Duration::from_secs(60);
        loop {
            if let Some(s) = self.0.try_wait().unwrap() {
                return s;
            }
            assert!(Instant::now() < deadline, "synthetic recorder did not stop");
            std::thread::sleep(Duration::from_millis(100));
        }
    }
    fn take(&mut self, dir: &Path, min_ready: usize) -> PathBuf {
        let deadline = Instant::now() + Duration::from_secs(60);
        loop {
            assert!(
                self.0.try_wait().unwrap().is_none(),
                "child exited before test milestone"
            );
            for entry in fs::read_dir(dir).unwrap().flatten() {
                if entry.file_name().to_string_lossy().starts_with(".take-")
                    && entry.file_type().unwrap().is_dir()
                    && ready(&entry.path()).len() >= min_ready
                {
                    return entry.path();
                }
            }
            assert!(
                Instant::now() < deadline,
                "timed out waiting for committed keyframe fragments"
            );
            std::thread::sleep(Duration::from_millis(100));
        }
    }
}
impl Drop for SyntheticChild {
    fn drop(&mut self) {
        if self.0.try_wait().ok().flatten().is_none() {
            let _ = self.0.kill();
        }
        let _ = self.0.wait();
    }
}
fn ready(dir: &Path) -> Vec<PathBuf> {
    let mut ready: Vec<_> = fs::read_dir(dir)
        .unwrap()
        .flatten()
        .filter(|e| e.file_name().to_string_lossy().starts_with("ready-"))
        .map(|e| e.path())
        .collect();
    ready.sort();
    ready
}
fn saved_fragments(dir: &Path) -> Vec<(PathBuf, Vec<u8>)> {
    fs::read_dir(dir)
        .unwrap()
        .flatten()
        .filter(|e| e.file_type().unwrap().is_file())
        .map(|e| {
            let p = e.path();
            let bytes = fs::read(&p).unwrap();
            (p, bytes)
        })
        .collect()
}
fn expected_output(take: &Path) -> PathBuf {
    let id = take
        .file_name()
        .unwrap()
        .to_str()
        .unwrap()
        .strip_prefix(".take-")
        .unwrap();
    take.parent().unwrap().join(format!("TinyClip-{id}.mp4"))
}
fn parse_recovered(path: &Path) {
    gst::init().unwrap();
    // Static test graph only. The selected path is a property, never a launch string.
    let p = gst::parse::launch("filesrc name=input ! qtdemux name=d d.video_0 ! queue ! h264parse ! fakesink name=consumer sync=false").unwrap().downcast::<gst::Pipeline>().unwrap();
    p.by_name("input")
        .unwrap()
        .set_property("location", path.to_str().unwrap());
    let sink = p.by_name("consumer").unwrap();
    sink.set_property("enable-last-sample", true);
    p.set_state(gst::State::Playing).unwrap();
    let wait_eos = || {
        let msg = p
            .bus()
            .unwrap()
            .timed_pop_filtered(
                gst::ClockTime::from_seconds(20),
                &[gst::MessageType::Eos, gst::MessageType::Error],
            )
            .expect("recovered output did not drain");
        assert!(matches!(msg.view(), gst::MessageView::Eos(_)), "{msg:?}");
        let sample = sink
            .property::<Option<gst::Sample>>("last-sample")
            .expect("no recovered H.264 frames");
        assert!(sample.buffer().unwrap().size() > 0);
    };
    wait_eos();
    let duration = p.query_duration::<gst::ClockTime>().unwrap();
    assert!(duration > gst::ClockTime::from_mseconds(800));
    p.seek_simple(
        gst::SeekFlags::FLUSH | gst::SeekFlags::KEY_UNIT,
        duration / 2,
    )
    .unwrap();
    wait_eos();
    p.set_state(gst::State::Null).unwrap();
}
fn assert_recovery(take: &Path) {
    let previous = saved_fragments(take);
    let output = futures_lite::future::block_on(tinyclips_media::recover(take)).unwrap();
    assert_eq!(output, expected_output(take));
    assert_eq!(fs::metadata(&output).unwrap().nlink(), 1);
    parse_recovered(&output);
    let result = fs::read(&output).unwrap();
    assert!(futures_lite::future::block_on(tinyclips_media::recover(take)).is_err());
    assert_eq!(fs::read(&output).unwrap(), result);
    assert!(!take.exists());
    assert!(
        futures_lite::future::block_on(tinyclips_media::recoverable_takes(take.parent().unwrap()))
            .unwrap()
            .is_empty()
    );
    let retired = take.with_file_name(take.file_name().unwrap().to_str().unwrap().replacen(
        ".take-",
        ".recovered-",
        1,
    ));
    for (p, bytes) in previous {
        let name = p.file_name().unwrap();
        let kept = retired.join(name);
        if name.to_string_lossy().starts_with("ready-")
            || name.to_string_lossy().starts_with("clock-")
        {
            assert!(!kept.exists());
        } else {
            assert_eq!(fs::read(kept).unwrap(), bytes);
        }
    }
}

#[test]
fn killed_recorder_remuxes_only_closed_fragments() {
    let lib = Library::new();
    let mut child = SyntheticChild::start(&lib.0, 120);
    let take = child.take(&lib.0, 2);
    assert!(futures_lite::future::block_on(tinyclips_media::recover(&take)).is_err());
    child.kill_and_reap();
    assert!(ready(&take).len() >= 2);
    assert!(!expected_output(&take).exists());
    // A torn tail must not be parsed, merged, deleted or renamed by recovery.
    let tail = take.join("open-99999999.mp4");
    fs::write(&tail, b"unfinished mp4").unwrap();
    assert_recovery(&take);
}

#[test]
fn failed_final_publication_preserves_destination_and_recoverable_input() {
    let lib = Library::new();
    let mut child = SyntheticChild::start(&lib.0, 3);
    let take = child.take(&lib.0, 0);
    let output = expected_output(&take);
    OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o600)
        .open(&output)
        .unwrap()
        .write_all(b"existing clip")
        .unwrap();
    assert!(!child.wait().success());
    assert_eq!(fs::read(&output).unwrap(), b"existing clip");
    assert!(!ready(&take).is_empty());
    let retained = saved_fragments(&take);
    assert!(futures_lite::future::block_on(tinyclips_media::recover(&take)).is_err());
    for (p, bytes) in retained {
        assert_eq!(fs::read(p).unwrap(), bytes);
    }
    // The user explicitly moves the conflicting file; the recovery API never does.
    fs::rename(&output, lib.0.join("preserved-existing")).unwrap();
    assert_recovery(&take);
    assert_eq!(
        fs::read(lib.0.join("preserved-existing")).unwrap(),
        b"existing clip"
    );
}
