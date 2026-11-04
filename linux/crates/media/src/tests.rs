use super::*;
use gst::prelude::*;
use std::{
    collections::HashSet,
    fs,
    os::unix::fs::{MetadataExt, PermissionsExt},
    sync::Mutex,
    time::{Duration, Instant},
};

struct Library(PathBuf);
impl Library {
    fn new() -> Self {
        let p = std::env::current_dir()
            .unwrap()
            .join(format!(".media-test-{}", storage::uuid()));
        storage::private_dir(&p, true).unwrap();
        Self(p)
    }
}
impl Drop for Library {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

#[cfg(feature = "portal")]
#[test]
fn operations_without_a_grant_fail_without_contacting_the_desktop() {
    let media = MediaService::new();
    let lib = Library::new();
    futures_lite::future::block_on(async {
        assert!(media.target().is_none());
        assert!(media.snapshot("stale-id", &lib.0).await.is_err());
        assert!(
            media
                .start("stale-id", &lib.0, AudioOptions::default())
                .await
                .is_err()
        );
        assert_eq!(media.status().phase, Phase::Idle);
        media.revoke().await.unwrap();
    });
    assert_eq!(fs::read_dir(lib.0.as_path()).unwrap().count(), 0);
}

#[test]
fn region_is_half_open_and_safe_from_overflow() {
    let r = Region {
        x: 100,
        y: 10,
        width: 100,
        height: 90,
    };
    assert!(r.validate(Some((200, 100))).is_ok());
    assert!(r.validate(Some((199, 100))).is_err());
    assert!(Region { x: u32::MAX, ..r }.validate(None).is_err());
    assert!(Region { width: 0, ..r }.validate(None).is_err());
}

#[test]
fn lock_prevents_recovering_active_take_and_names_never_overwrite() {
    let lib = Library::new();
    let take = storage::Take::new(&lib.0).unwrap();
    assert!(storage::Take::existing(&take.dir).is_err());
    let staged = lib.0.join("staged");
    fs::write(&staged, b"new").unwrap();
    let target = lib.0.join("existing");
    fs::write(&target, b"old").unwrap();
    assert!(storage::Scratch::new_file(target.clone()).is_err());
    assert!(storage::publish(&staged, &target).is_err());
    assert_eq!(fs::read(target).unwrap(), b"old");
    fs::set_permissions(&lib.0, fs::Permissions::from_mode(0o755)).unwrap();
    assert!(storage::private_dir(&lib.0, false).is_err());
}

#[test]
fn real_png_frame_honors_raster_crop() {
    let lib = Library::new();
    let r = Region {
        x: 10,
        y: 10,
        width: 111,
        height: 123,
    };
    let (p, w, h) = engine::snapshot(
        engine::VideoInput::Synthetic,
        Arc::new(AtomicBool::new(true)),
        &lib.0,
        Some(r),
    )
    .unwrap();
    assert_eq!((w, h), (111, 123));
    assert_eq!(fs::metadata(&p).unwrap().mode() & 0o777, 0o600);
    assert!(
        engine::snapshot(
            engine::VideoInput::Synthetic,
            Arc::new(AtomicBool::new(false)),
            &lib.0,
            None
        )
        .is_err()
    );
    assert_eq!(fs::read_dir(&lib.0).unwrap().count(), 1);
}

// Demux every sample, verifying elementary stream types without requiring a decoder.
fn check_mp4(path: &Path, audio: bool) -> gst::ClockTime {
    let p = engine::Running(gst::Pipeline::new());
    let file = engine::element("filesrc").unwrap();
    file.set_property("location", path.to_str().unwrap());
    let demux = engine::element("qtdemux").unwrap();
    p.0.add_many([&file, &demux]).unwrap();
    file.link(&demux).unwrap();
    let tracks = Arc::new(Mutex::new(HashSet::new()));
    let seen = tracks.clone();
    let weak = p.0.downgrade();
    demux.connect_pad_added(move |_, pad| {
        let sink = engine::element("fakesink").unwrap();
        sink.set_property("sync", false);
        sink.set_property("async", false);
        let received = seen.clone();
        sink.static_pad("sink")
            .unwrap()
            .add_probe(gst::PadProbeType::BUFFER, move |pad, _| {
                if let Some(caps) = pad.current_caps() {
                    if let Some(s) = caps.structure(0) {
                        let name = if s.name() == "audio/mpeg"
                            && s.get::<i32>("mpegversion").ok() == Some(4)
                        {
                            "aac"
                        } else {
                            s.name().as_str()
                        };
                        received.lock().unwrap().insert(name.to_owned());
                    }
                }
                gst::PadProbeReturn::Ok
            });
        weak.upgrade().unwrap().add(&sink).unwrap();
        pad.link(&sink.static_pad("sink").unwrap()).unwrap();
        sink.sync_state_with_parent().unwrap();
    });
    p.play().unwrap();
    let bus = p.0.bus().unwrap();
    let inspect_to_eos = || {
        let deadline = Instant::now() + Duration::from_secs(15);
        loop {
            assert!(Instant::now() < deadline, "MP4 inspection timed out");
            if let Some(msg) = bus.timed_pop(gst::ClockTime::from_mseconds(100)) {
                match msg.view() {
                    gst::MessageView::Eos(_) => break,
                    gst::MessageView::Error(e) => panic!("{}", engine::message_error(e)),
                    _ => (),
                }
            }
        }
        let mut seen = tracks.lock().unwrap();
        assert!(seen.contains("video/x-h264"));
        assert_eq!(seen.contains("aac"), audio);
        seen.clear();
    };
    inspect_to_eos();
    let duration = p.0.query_duration::<gst::ClockTime>().unwrap();
    assert!(duration >= gst::ClockTime::from_mseconds(600));
    p.0.seek_simple(
        gst::SeekFlags::FLUSH | gst::SeekFlags::KEY_UNIT,
        duration / 2,
    )
    .unwrap();
    inspect_to_eos();
    assert_eq!(fs::metadata(path).unwrap().mode() & 0o777, 0o600);
    duration
}

#[test]
fn synthetic_one_second_no_audio() {
    let lib = Library::new();
    let path =
        futures_lite::future::block_on(synthetic_smoke(&lib.0, 1, AudioOptions::default(), false))
            .unwrap();
    check_mp4(&path, false);
}
#[test]
fn short_media_is_never_published_as_a_long_take_but_remains_recoverable() {
    let lib = Library::new();
    let (dir, expected) = {
        let real = futures_lite::future::block_on(synthetic_smoke(
            &lib.0,
            1,
            AudioOptions::default(),
            false,
        ))
        .unwrap();
        let take = storage::Take::new(&lib.0).unwrap();
        let original = take.dir.join("open-00000000.mp4");
        fs::rename(real, &original).unwrap();
        take.closed(&original, 123_000_000).unwrap();
        let e = engine::finalize(&take, Some(Duration::from_secs(1800))).unwrap_err();
        assert!(e.contains("closed segments retained"), "{e}");
        assert!(!take.output().exists());
        assert_eq!(take.segments().unwrap().len(), 1);
        (take.dir.clone(), take.output())
    };
    assert_eq!(
        futures_lite::future::block_on(recover(&dir)).unwrap(),
        expected
    );
    check_mp4(&expected, false);
}

#[test]
fn backpressured_video_keeps_the_real_timeline_through_multiple_fragments() {
    let lib = Library::new();
    let path = futures_lite::future::block_on(synthetic_check(
        &lib.0,
        6,
        AudioOptions {
            microphone: true,
            system: true,
        },
        true,
        250,
    ))
    .unwrap();
    assert!(check_mp4(&path, true) >= gst::ClockTime::from_mseconds(5500));
}
#[test]
fn synthetic_three_seconds_two_mixed_tones_pause_and_remux() {
    let lib = Library::new();
    let path = futures_lite::future::block_on(synthetic_smoke(
        &lib.0,
        3,
        AudioOptions {
            microphone: true,
            system: true,
        },
        true,
    ))
    .unwrap();
    check_mp4(&path, true);
}
#[test]
fn revoked_recording_can_recover_only_closed_fragments_without_overwrite() {
    let lib = Library::new();
    let recorder = recorder::Recorder::default();
    let valid = Arc::new(AtomicBool::new(true));
    futures_lite::future::block_on(async {
        recorder
            .start(
                engine::VideoInput::Synthetic,
                valid.clone(),
                &lib.0,
                AudioOptions::default(),
                1,
                None,
            )
            .await
            .unwrap();
        delay(Duration::from_millis(2400)).await.unwrap();
        valid.store(false, std::sync::atomic::Ordering::Release);
        let deadline = Instant::now() + Duration::from_secs(20);
        while recorder.status().active() {
            assert!(Instant::now() < deadline);
            delay(Duration::from_millis(50)).await.unwrap();
        }
        let status = recorder.status();
        assert_eq!(status.phase, Phase::Failed);
        let take = status.recovery_dir.unwrap();
        fs::write(take.join("open-99999999.mp4"), b"torn tail").unwrap();
        assert_eq!(recoverable_takes(&lib.0).await.unwrap(), vec![take.clone()]);
        let path = recover(&take).await.unwrap();
        assert!(recoverable_takes(&lib.0).await.unwrap().is_empty());
        check_mp4(&path, false);
        assert_eq!(fs::metadata(&path).unwrap().nlink(), 1);
        assert!(!take.exists());
        let tail_dir = take.with_file_name(take.file_name().unwrap().to_str().unwrap().replacen(
            ".take-",
            ".recovered-",
            1,
        ));
        assert_eq!(
            fs::read(tail_dir.join("open-99999999.mp4")).unwrap(),
            b"torn tail"
        );
        assert!(fs::read_dir(tail_dir).unwrap().all(|e| {
            !e.unwrap()
                .file_name()
                .to_string_lossy()
                .starts_with("ready-")
        }));
        assert!(recover(&take).await.is_err());
    });
}
