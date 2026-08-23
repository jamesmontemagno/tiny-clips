# Audio/Video Sync on Windows

Tiny Clips for Windows records the **screen**, an optional **webcam overlay**, **microphone**,
and **system (loopback) audio** as one MP4. These come from four independent capture sources
that each start at slightly different times and run on different clocks, so keeping them in sync —
and keeping the audio clean — takes deliberate timeline handling. This is the Windows analogue of
the macOS shared-`CMTime`/`AVAssetWriter` approach.

## The pieces

| Source | API | Clock |
| --- | --- | --- |
| Screen | `Windows.Graphics.Capture` (WGC) | system-relative QPC |
| Webcam overlay | `MediaCapture` frame reader | `MediaFrameReference` QPC |
| Microphone | WASAPI capture (`TimestampedWasapiCapture`) | WASAPI packet QPC |
| System audio | WASAPI loopback (`TimestampedWasapiCapture`) | WASAPI packet QPC |

All four are muxed into one MP4 by a `MediaStreamSource` + `MediaTranscoder`: the composited
BGRA video frames feed the video stream descriptor, and the mixed 48 kHz / 16‑bit / stereo PCM
feeds the audio stream descriptor.

## One shared origin

Every source is anchored to a single **`RecordingTimeline`** — a QPC-relative origin captured with
`RecordingTimeline.StartNow()` at the moment recording truly begins (after the encoder is prepared
and the first webcam frame is warm). See `VideoRecordingService.StartAsync`.

- **Screen** frames get `pts = timeline.Elapsed` (real wall-clock elapsed since origin) at emit
  time, produced at a steady cadence by the pump in `ContinuousCaptureSession`.
- **Webcam** frames are normalized against the same origin before compositing, and a frame is never
  composited from ahead of the screen clock (`IsWebcamFrameReady`).
- **Audio** packets keep their real WASAPI capture timestamps and are aligned to the same origin by
  `TimelineAlignedWaveProvider` (below).

Anchoring to the real start moment (rather than PTS 0 = whatever arrives first) keeps encoder
warm-up and camera spin-up out of the recorded timeline — otherwise several seconds of frozen
pre-roll get baked into the front of every clip.

## Aligning audio to the timeline — `TimelineAlignedWaveProvider`

Each audio source has its own `TimelineAlignedWaveProvider` that places captured packets on the
shared timeline, then the two are combined by an NAudio `MixingSampleProvider`. The alignment rules
were hard-won; each exists to fix a specific defect:

1. **Align only the first packet, then append contiguously.** Re-deriving every ~10 ms packet's
   position from its (jittery, frame-rounded) QPC timestamp inserted or dropped a sample or two on
   *every* packet — ~100 micro-edits per second of constant audible crackle. Only the first packet
   after the origin is positioned; the rest are appended back-to-back.

2. **Drop *all* pre-origin pre-roll, not just the first straddling packet.** The capture thread
   starts before the origin, so several whole packets can predate it. Each fully pre-origin packet is
   dropped; alignment locks onto the first packet that reaches the origin (trimming its pre-origin
   frames). Appending stale pre-origin audio at the origin would delay all real audio.

3. **Preserve inter-source start offsets.** A source that genuinely starts *after* the origin (e.g.
   microphone vs. system audio) is padded with leading silence so the offset between sources is
   preserved.

4. **Advance by capture latency.** WASAPI timestamps the buffer read, which trails the true acoustic
   capture instant. Audio is advanced by the source's `IAudioClient.StreamLatency` so recorded sound
   lines up with video captured at the same wall-clock instant. (Some drivers report `0` here; the
   dominant sync mechanism is the back-pressure below.)

5. **Apply the user's audio offset.** Settings → Video → Audio → *Audio offset* (±500 ms) is added
   to every packet's target position. Positive delays audio; negative plays it earlier. This is the
   escape hatch for Bluetooth headsets and some USB microphones whose real latency WASAPI does not
   report (they stamp packets on arrival, so their audio lands late).

## Drift and discontinuity correction

Rule 1 (append contiguously) on its own lets a source silently slide against the video clock:

- a WASAPI buffer overrun drops a packet, and everything after it moves earlier;
- a microphone whose crystal runs 50–100 ppm fast or slow drifts hundreds of milliseconds per hour;
- two sources at slightly different real rates make the faster one pile up in its buffer until
  `DiscardOnBufferOverflow` throws away a chunk;
- a pause/resume cycle removes packets from the middle of the stream.

So `TimelineAlignedWaveProvider` still computes every packet's **expected position**
(`timeline.Normalize(ts) − latency + userOffset`, in source frames) and compares it with the frames
written so far. If the deviation is within **`DriftTolerance` (30 ms)** the packet is appended
contiguously — that covers all ordinary jitter, so the crackle fix holds. Only when the deviation
exceeds the tolerance, or the driver flagged the packet with `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY`
(surfaced by `TimestampedWasapiCapture`), is the stream corrected **once**: silence is inserted for a
gap, or frames are trimmed from the front of the packet (and following packets, if needed) for an
overlap. Each correction is logged as `sync correction: padded/trimmed … ms`.

The first packet after the origin is simply a forced, exact correction — the same code path anchors
the source to the origin.

**Underruns count as written.** If the muxer ever reads past captured audio, the zero padding it
receives (`ReadFully`) occupies real positions on the output timeline, so `Read` advances the written
cursor to cover it. The next packet then shows as an overlap and is trimmed, instead of landing late
behind silence the muxer already consumed.

### Pause and resume

- `RecordingTimeline.Pause/Resume` exclude the paused interval from `Normalize`, so video PTS and
  audio expected positions both continue seamlessly from the pause point.
- `TimelineAlignedWaveProvider.Pause()` stops accepting packets but **keeps** already-captured audio:
  it belongs to pre-pause video time and the muxer still has to drain it. (It used to be cleared,
  which shifted every later sample earlier by the discarded amount.)
- `Resume()` needs no special re-alignment: the first post-resume packet is compared against the
  paused-adjusted timeline like any other and corrected only if it is out of tolerance.
- `ContinuousCaptureSession.PauseEmitting` keeps capturing into its cached frame so the pump can
  emit the current screen the instant recording resumes, even on a static desktop.
- The muxer's audio request waits **without a cap** while paused (see back-pressure below).

## Clean capture — `TimestampedWasapiCapture`

A custom WASAPI capture (rather than NAudio's `WasapiCapture`) so packets carry their QPC capture
timestamps for timeline alignment. Two things keep it dropout-free under load:

- **200 ms capture buffer** requested in `IAudioClient.Initialize`, polled on a **fixed 8 ms
  interval** at **`ThreadPriority.Highest`**. Every recording currently falls back to *software*
  H.264 encoding (the default hardware profile hits `MF_E_TRANSFORM_TYPE_NOT_SET` / `0xC00D6D60`),
  which is CPU-heavy; a small buffer on a normal-priority polling thread would get starved and
  overrun, dropping samples.
- **Mix-format standardization.** WASAPI's shared-mode mix format is a `WaveFormatExtensible`, which
  NAudio's sample-provider converters reject (`ArgumentException: Unsupported source encoding`). The
  capture initializes WASAPI with the native format but exposes `WaveFormatExtensible.ToStandardWaveFormat()`
  (identical byte layout, IEEE-float/PCM tag) to the NAudio pipeline. Without this, both audio
  sources fail to start silently and recordings have **no audio track**.

## Back-pressure — the key to A/V sync

This is the mechanism that actually keeps audio and video aligned end-to-end.

The audio source (`AudioCaptureService.ReadChunk`) pads silence on demand (`ReadFully`), so it will
*always* return a full chunk even when little real audio has been captured. Left unchecked, the
`MediaTranscoder` drains the audio stream **far faster than real time**, races the entire audio track
~1 s ahead, and captured sound then lands ~1 s **late** on playback.

The fix (`VideoRecordingService.HandleAudioRequestAsync`) is proper producer/consumer back-pressure:
**only hand the muxer a 20 ms chunk once that many real frames have actually been captured** across
all sources (`AudioCaptureService.AvailableFrames`, the minimum buffered duration over sources).

- Audio can never get ahead of real capture progress → **no racing / no delay**.
- We never read an empty buffer → **no silence-splicing crackle**.
- Sources that go through the resampler (e.g. 44.1 kHz microphones) keep a 20 ms margin so the
  resampler's lookahead never reads past captured data.
- **While paused there is no wait cap.** No audio arrives by design; ending the wait would hand the
  muxer a sample it must not have.
- When *not* paused, a 2 s cap prevents a dead device from hanging the transcode. A starved request
  is filled with **silence** (logged as `Audio muxer starved`) — it is never answered with `null`,
  because `MediaStreamSource` treats a `null` sample as **end of the audio stream**. (That was the
  original "pause for more than 2 s and the rest of the clip is silent" bug.)

### Ending the audio track

`StopAsync` stops the WASAPI devices, then switches the muxer into a **drain** mode that serves the
remaining captured frames (bounded to 500 ms, `MaxDrainFrames`) and only then answers `null`. This
is what makes the audio track end where the video ends instead of a few hundred milliseconds early.

> **Do not** gate audio reads to `timeline.Elapsed` (the recording wall clock). An earlier version
> did, and it still read whenever the buffer was momentarily thin (WASAPI capture latency), splicing
> in silence and crackling. Gate on **captured-frame availability**, not the clock.

## Muxer PTS

Audio sample PTS is a monotonic counter derived from frames actually handed to the muxer
(`_audioFramesRead / SampleRate`), not from per-packet timestamps — this is what lets later packets be
appended contiguously without re-quantizing. Because the alignment above pins buffer position 0 to
the origin, and back-pressure pins the read rate to real capture, buffer position *X* both plays at
PTS *X* and was captured at real time `origin + X`, so audio and video stay locked together.

## Diagnostics

Per-recording tracing is written to `webcam-diagnostics.log` (packaged app:
`%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\Local\TinyClips\`). Useful lines when
debugging sync/audio:

- `Audio capture loop started … latencyMs=…` — WASAPI stream latency and buffer/poll settings.
- `TimelineAlignedWaveProvider[source] aligned: padded/trimmed … ms (sourceOffsetMs=… latencyMs=… userOffsetMs=…)`
  — how the first packet landed on the origin.
- `TimelineAlignedWaveProvider[source] sync correction: …` — a drift/discontinuity correction fired.
  Zero of these in a normal recording; one or two over a long recording with a drifting device is
  expected and each is ≤ the accumulated drift.
- `Audio packet discontinuity (…)` / `Audio packet timestamp jump (…)` — the driver lost data (or
  its timestamps jumped) before this packet. The provider corrects it; this is the evidence.
- `Audio muxer starved` — no captured audio for 2 s while not paused; silence was substituted.
- `First screen frame emitted: ptsMs=…` — where the video stream starts.
- `Audio muxer progress: requests=… nonSilentChunks=… framesRead=…` — verify `framesRead` tracks
  real elapsed time (≈ `SampleRate × seconds`); if it runs ahead, back-pressure isn't holding.

### Sync report

Every recording ends with a `Sync report:` block:

```
Sync report: encoder='software H.264 Baseline (fallback)' elapsed=12.345s pauses=1 pausedTotal=3.012s.
Sync report: video lastPts=12.331s framesEmitted=370 framesDroppedByEncoderBackpressure=0.
Sync report: audio pts=12.340s chunks=617 nonSilent=540 starvedChunks=0 drainedFrames=432 userOffsetMs=0 driverDiscontinuities=0.
Sync report: audio-video end delta=9.0ms (audio longer; |delta| < 30 ms is healthy).
Sync report: system/loopback: written=12.340s corrections=0 padded=0.0ms trimmed=0.0ms underrun=0.0ms preOriginDropped=3 lastDeviation=0.4ms maxDeviation=1.1ms
Sync report: microphone: written=12.340s corrections=0 padded=0.0ms trimmed=0.0ms underrun=0.0ms preOriginDropped=2 lastDeviation=-0.8ms maxDeviation=2.3ms
```

Healthy values: `|delta|` well under 30 ms, `corrections=0` (or a handful on very long recordings),
`starvedChunks=0`, `underrun=0`, `maxDeviation` a few ms. A large `framesDropped…` count only means
the encoder could not keep up (lower effective frame rate) — video PTS is wall-clock, so it does not
affect sync.

## Hardware listen-test checklist

Unit tests cover the timeline math; these need a real machine (record, then open the clip and the
`Sync report` block):

1. **Clap test** — record the screen with microphone, clap on camera (webcam overlay on) or against
   a visible on-screen metronome; the transient must land on the visual within a frame.
2. **Pause** — record 10 s, pause ≥ 5 s, resume, record 10 s more. Audio must be present after the
   pause and still aligned; `pauses=1`, `corrections` 0 or 1.
3. **Long recording** — 15+ minutes with mic + system audio; `delta` stays < 30 ms, and any
   `sync correction` lines are small (tens of ms).
4. **Mic + system together** — play a video with speech while talking; both tracks stay aligned to
   the picture, neither creeps ahead of the other.
5. **Bluetooth / USB mic** — if audio lands late, set a negative *Audio offset* (start at −150 ms)
   and confirm the report shows `userOffsetMs` and the clap test passes.
6. **Stop edge** — speak right up to pressing Stop; the last word must be in the file (`drainedFrames`
   > 0).

## See also

- [DPI & Coordinates on Windows](./dpi-and-coordinates.md)
- macOS equivalent: shared-clock capture in `mac/TinyClips/Capture/VideoRecorder.swift`
