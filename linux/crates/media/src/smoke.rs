use std::{
    env,
    path::PathBuf,
    process::ExitCode,
    sync::mpsc,
    time::{Duration, Instant},
};
use tinyclips_media::{AudioOptions, synthetic_check};
const USAGE: &str = "tinyclips-media-check --synthetic --seconds 1..86400 --output-dir /private/path [--audio] [--pause] [--stress-video-ms 0..2000]";
fn run() -> Result<(), String> {
    let mut args = env::args_os().skip(1);
    let (mut synthetic, mut seconds, mut output, mut audio, mut pause) =
        (false, None, None, false, false);
    let mut delay_ms = 0;
    while let Some(arg) = args.next() {
        match arg.to_str() {
            Some("--synthetic") => synthetic = true,
            Some("--audio") => audio = true,
            Some("--pause") => pause = true,
            Some("--stress-video-ms") => {
                delay_ms = args
                    .next()
                    .and_then(|n| n.to_str()?.parse::<u32>().ok())
                    .ok_or(USAGE)?
            }
            Some("--seconds") if seconds.is_none() => {
                seconds = Some(
                    args.next()
                        .and_then(|n| n.to_str()?.parse::<u32>().ok())
                        .ok_or(USAGE)?,
                )
            }
            Some("--output-dir") if output.is_none() => {
                output = Some(PathBuf::from(args.next().ok_or(USAGE)?))
            }
            _ => return Err(USAGE.into()),
        }
    }
    if !synthetic {
        return Err(USAGE.into());
    }
    let output = output.ok_or(USAGE)?;
    let seconds = seconds.ok_or(USAGE)?;
    eprintln!(
        "Synthetic encoder/segment/remux check; not a desktop authorization or PipeWire test."
    );
    let (done, progress) = mpsc::channel::<()>();
    let reporter = std::thread::spawn(move || {
        let now = Instant::now();
        while progress.recv_timeout(Duration::from_secs(10)) == Err(mpsc::RecvTimeoutError::Timeout)
        {
            let status = std::fs::read_to_string("/proc/self/status").unwrap_or_default();
            eprintln!(
                "synthetic elapsed={}s {}; {}",
                now.elapsed().as_secs(),
                status
                    .lines()
                    .find(|l| l.starts_with("VmRSS:"))
                    .unwrap_or(""),
                status
                    .lines()
                    .find(|l| l.starts_with("VmHWM:"))
                    .unwrap_or("")
            );
        }
    });
    let path = futures_lite::future::block_on(synthetic_check(
        &output,
        seconds,
        AudioOptions {
            microphone: audio,
            system: audio,
        },
        pause,
        delay_ms,
    ));
    drop(done);
    let _ = reporter.join();
    println!("{}", path?.display());
    Ok(())
}
fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("{e}");
            ExitCode::FAILURE
        }
    }
}
