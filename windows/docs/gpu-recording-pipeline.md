# GPU Recording Pipeline on Windows

This document is the research write-up and design record for the Windows video recorder's
**GPU-resident ("zero-copy") pipeline**, the per-stage **performance instrumentation** added to both
pipelines, and the **benchmark harness** used to compare them. It complements
[`audio-video-sync.md`](audio-video-sync.md), which covers timeline/sync; nothing here changes the
timeline model — both pipelines stamp frames with the same `RecordingTimeline`.

- Settings (all under **Settings → Video → Recording & output**):
  - **GPU recording pipeline** (`UseGpuRecordingPipeline`, **default on**) — frames stay in video memory.
  - **Video encoder** (`VideoEncoderBackend`: *Standard* = `MediaTranscoder`, *Low latency* = `IMFSinkWriter`; **default Low latency**).
  - **Video codec** (`VideoCodec`: H.264 default, or HEVC).
  Both new defaults fall back to the previous behaviour automatically if they cannot start, and the
  one-time **What's new** window shown after updating points users at these settings if recordings
  misbehave on their hardware.
- Code: `GpuCaptureSession`, `GpuFrame` / `GpuFrameTexturePool`, `GpuOverlayCompositor`, `FramePacer`,
  `MfSinkWriterEncoder`, `RecordingPerformanceMonitor` in `windows/src/TinyClips.Core/Capture/`;
  pipeline/backend selection in `VideoRecordingService.PrepareCoreAsync`.
- Benchmark: `windows/tools/RecordingBenchmark`.

> **Phase 2 (sink writer, resize, GPU webcam) results are in §8.** Sections 1–7 record the first
> pass (GPU capture + Direct2D overlays feeding the existing `MediaTranscoder`).

## 1. Problem: where the time went in the CPU pipeline

The original recorder (now the **CPU fallback pipeline**) moved every frame through
system memory four times before the encoder sees it:

| # | Step | Where | Cost per 3440×1440 frame (19.8 MB) |
|---|------|-------|-----------------------------------|
| 1 | WGC frame → `CopyResource` into a **staging** texture → `Map` → copy into `byte[]` | `ContinuousCaptureSession.OnFrameArrived` | GPU→CPU readback + 20 MB alloc + memcpy (**~10–17 ms**, blocks the WGC thread) |
| 2 | Pump tick: `_latestPixels.Clone()` | `ContinuousCaptureSession.OnPump` | 20 MB alloc + memcpy (~3 ms) |
| 3 | Overlays: click rings, branding badge, webcam PiP, all CPU alpha-blended in `double` math | `VideoRecordingService.OnFrameReady` | 0 ms → tens of ms with webcam |
| 4 | **Bottom-up flip** into a third `byte[]` for Media Foundation, then `CreateFromBuffer` (MF copies again into an `IMFMediaBuffer`) | `CreateBottomUpVideoBuffer` | 20 MB alloc + memcpy (~5–9 ms) |

Steps 1, 2 and 4 each allocate a ~20 MB array on the **Large Object Heap** per frame. At 30 fps that
is **~1.2 GB/s of allocations**, every collection is a **Gen2** collection (LOH lives in Gen2), and
the benchmark measured **100–180 Gen2 GCs per 10 s recording** with **3–5 % of wall-clock time in
GC pauses**. The pauses are what make the CPU path's latency spiky: a branding blend that costs
microseconds showed a **400 ms max** simply because a Gen2 GC landed inside it.

The historical reference run (below) reported **~11 submitted samples/s** at 3440×1440@30
(114 sample handoffs in the legacy interval) with zero "dropped" frames — the drop counter
only counts channel back-pressure, while the real loss was the **pump skipping ticks**: the pump
used `Monitor.TryEnter` and gave up whenever the WGC thread held the lock during its 10–17 ms
readback, and `System.Threading.Timer`'s ~15.6 ms granularity cannot hold a 33.3 ms cadence
precisely.

### 1.1 Current CPU buffer ownership

The CPU fallback no longer allocates those three full-frame managed arrays on every frame.
`ContinuousCaptureSession` keeps an exact-size private readback buffer and, for video, an
exact-size private processing buffer (`CpuCaptureBuffers`). Readback and the screen-to-processing
copy use the capture lock. The optional synchronous `processBorrowedFrame` callback owns the
processing buffer only until it returns; `CpuFrameProcessingGate` serializes timer callbacks
through overlays and encoder submission. Stop waits for that callback before disposing capture
resources or the encoder. A static desktop still copies the unchanged screen into the processing
buffer every tick, so click/webcam overlays animate without accumulating on the cached screen.
The existing shared timeline, pause/resume, crop coordinates, and output dimensions are unchanged.

The two CPU encoder backends intentionally have different ownership boundaries:

| Backend | Pixel handoff | Full-frame managed allocation after warm-up |
|---------|---------------|--------------------------------------------|
| Sink writer | Copy top-down processing rows directly into a bottom-up `IMFMediaBuffer`. `WriteVideo` owns/disposes its native buffer and sample wrappers; MF retains its own COM references if encoding is asynchronous. | None for screen frame buffers; the two private capture buffers are reused. |
| MediaTranscoder | `CpuVideoBuffer.Create` makes one independent bottom-up array and wraps it in an `IBuffer`. The bounded channel and then `MediaStreamSample` retain that array, never the borrowed processing buffer. | One exact-size array per prepared sample, instead of readback + pump clone + bottom-up array. |

The transcoder array is deliberately **not pooled**: an encoder may retain it after sample
submission, including error/stop paths. Keeping it independently owned and GC-accounted avoids
both use-after-return and per-frame native WinRT allocations whose release would otherwise depend
on collection of small managed wrappers. The existing four-second bounded channel and its
DropWrite accounting remain unchanged; completed/abandoned CPU queues are drained on cleanup.
Sink-writer write failures leave only encoder-owned pixels, and CPU finalization failures still
dispose the stopped pipeline before propagating.

The general `FrameReady` and `FrameArrived` events retain their snapshot contract. GIF stores
frames for later quantization, and scrolling capture retains frames for stitching, so their arrays
must never be reused. `FrameReady` gets an independent clone. A readback published through
`FrameArrived` is permanently excluded from reuse, even when that subscriber later unsubscribes.
Video readback always keeps the initial encoder dimensions, including during window resize. It
uses WGC `ContentSize` rather than unused pool-surface pixels, then recreates the pool/staging
texture as the source size changes. Resized whole windows are aspect-fitted with opaque black
letterboxing and allocation-free nearest-neighbour sampling (the GPU compositor uses linear
sampling); the ordinary one-pixel even-dimension trim remains an unscaled crop. Monitor/region
captures retain physical crop coordinates, padding unavailable pixels black instead of submitting
a short sample. The private video arrays stay fixed-size across source resizes. Retained non-video
snapshots still use their captured dimensions; no oversized pooled capacity becomes pixel data.

`CpuRecordingBufferTests` and `CpuVideoFrameReadbackTests` use synthetic BGRA fixtures to cover retained snapshots/samples,
DropWrite, cancelled reads and queue draining, encoder exceptions, overlapping callbacks, stop,
repeated start/stop, static-frame overlays, region coordinates, bottom-up orientation, and fixed-size
encoder samples through source shrink/grow and pool-pitch changes. Its
allocation regression submits the same number of known 1280x720 physical-pixel frames to both
copy paths: after warming the private buffers, managed bytes per submitted frame must stay below
16 KiB for sink-writer copying, or one exact pixel array plus 16 KiB for transcoder preparation.
These are deterministic ownership/allocation checks, **not** real-time WGC/encoder throughput,
GC-pause, working-set, or native-device benchmark results. The historical measurements below do
not describe this updated CPU implementation. Native x64/ARM64 cadence, long-recording memory,
overlays, and forced GPU-initialization fallback still require device validation at matching,
verified physical output dimensions; a lower allocation rate alone is not evidence of a win.

## 2. Options considered

| Option | Verdict | Why |
|--------|---------|-----|
| **A. Keep `MediaTranscoder` + `MediaStreamSource`, feed D3D11 surfaces** via `MediaStreamSample.CreateFromDirect3D11Surface` | **Chosen** | It is the documented Microsoft pattern ([Screen capture to video](https://learn.microsoft.com/windows/uwp/audio-video-camera/screen-capture-video), SimpleRecorder). Keeps the existing encoder prep / Baseline fallback / AAC mux / audio back-pressure code untouched. Surface handoff allows MF to use a GPU colour converter/encoder, but does not verify the selected transforms or an end-to-end hardware path. |
| B. Native MF `IMFSinkWriter` + `IMFDXGIDeviceManager` + own Video Processor MFT | **Adopted in phase 2** (§8) | Full control (encoder MFT attributes, HEVC, push model). The earlier `MediaFoundationEncoderSpike` found Vortice's MF wrappers unusable, but `Vortice.MediaFoundation` 3.8.3 exposes everything needed (`MFCreateSinkWriterFromURL`, `MFCreateDXGIDeviceManager`, `IMFVideoSampleAllocatorEx`), as [crutkas/tiny-clips](https://github.com/crutkas/tiny-clips) demonstrated. Phase 1 measured the transcoder's encoder hold time as the remaining bottleneck, which is exactly what this fixes. |
| C. Direct Desktop Duplication (`IDXGIOutputDuplication`) instead of WGC | Rejected | No window capture, no cursor composition without extra work, and WGC already hands us a D3D11 texture. |
| D. Win2D (`CanvasDevice`) for overlays | Rejected | Win2D is WinUI-app-only (the `TinyClips.App` package); the compositor must live in UI-free `TinyClips.Core`. Direct2D via `Vortice.Direct2D1` gives the same primitives on the same D3D11 device. |
| E. Custom HLSL compute/pixel shader for overlays | Deferred | More work for no measurable gain: Direct2D fills/ellipses/bitmaps on a 5 MP target cost 0.1–0.7 ms. |

## 3. Design of the GPU pipeline

```
WGC frame pool (B8G8R8A8, 2 buffers)
   │  FrameArrived (WGC thread)
   ▼
CopyResource → "latest" texture  ── Flush ──▶  (WGC buffer recycled safely)
   │
   │  FramePacer tick (dedicated high-res thread, absolute 1/fps grid)
   ▼
GpuFrameTexturePool.TryRent  →  CopySubresourceRegion(region crop)  →  Direct2D overlays  →  Flush
   │                                (RenderTarget | ShaderResource, no CPU access)
   ▼
Channel<GpuFrame> (bounded to pool size, DropWrite ⇒ texture released)
   │  SampleRequested (MF thread)
   ▼
MediaStreamSample.CreateFromDirect3D11Surface(surface, pts)
   │  sample.Processed ⇒ GpuFrame.Release() ⇒ back to pool
   ▼
MediaTranscoder (hardware requested) → colour conversion / selected encoder (unverified) → MP4
```

Key decisions and the bugs they avoid:

- **One shared D3D11 device** (`WgcInterop.GetSharedDevice`) now created with
  `D3D11_CREATE_DEVICE_VIDEO_SUPPORT | BGRA_SUPPORT` (falls back without `VIDEO_SUPPORT`). MF's
  encoder MFTs need `VIDEO_SUPPORT` to bind our textures; without it MF would copy through system
  memory or fail to prepare.
- **`Flush()` after the WGC `CopyResource`.** The CPU path's `Map()` implicitly waited for its copy.
  Without the flush, the copy sat in the command buffer while WGC recycled the 2-buffer frame pool,
  and the first GPU run produced frames that were a **torn mix of two captures** (large white areas).
  Flushing submits the copy before the frame is disposed. `Flush()` again after overlays guarantees
  the encoder — which may run on its own context — reads completed pixels.
- **No bottom-up flip.** `CreateFromDirect3D11Surface` samples are top-down; the orientation bug
  class is gone.
- **Texture pool, not per-frame textures.** Hardware encoders hold input surfaces for their
  look-ahead window. Historical `EncoderHold` (hand-off → `Processed`) on the reference adapter,
  with encoder transform unverified: **~25–50 ms avg,
  100–220 ms p99/max**, i.e. up to ~7 frames in flight at 30 fps and ~14 at 60. The pool starts at 4
  and grows on demand to `clamp(fps/2, 8, 30)`; when it is exhausted the pump **drops at the source**
  (no texture churn) and the frame's wall-clock PTS slot is simply absent, so audio never slides.
  VRAM cost is bounded at ~1 GB for 4K@60 worst case (30 × 33 MB).
- **`FramePacer`** replaces `System.Threading.Timer` for the GPU pump: a dedicated
  `AboveNormal` thread, `CreateWaitableTimerExW(HIGH_RESOLUTION)`, absolute grid scheduling (an
  overrun skips to the next future slot instead of drifting), and the pump now **blocks** on the
  session lock (single producer) rather than skipping. This alone moved the GPU path from ~25 to
  **28–30 fps** at 30 fps target.
- **Overlays in Direct2D on the same device.** `GpuOverlayCompositor` creates an
  `ID2D1Device` from the DXGI device, wraps each pooled texture once as an `ID2D1Bitmap1` render
  target (cached by texture pointer), and draws:
  - click pulses with `DrawEllipse` (geometry from the shared `MouseClickOverlayCompositor.TryComputeRing`);
  - the branding badge as a premultiplied `ID2D1Bitmap1` uploaded **once** from
    `BrandingOverlayCompositor.TryGetPreparedBadge` (same GDI+ rasterization as the CPU path,
    prepared before capture callbacks and frame emission; see below);
  - the webcam PiP via an `ID2D1BitmapBrush` with a crop→overlay transform, filled as ellipse /
    rounded-rect / rect using the shared `WebcamOverlayLayout` placement math. The camera frame is
    uploaded with `CopyFromMemory` only when a **new** `WebcamFrame` instance arrives, using
    `AlphaMode.Ignore` because camera drivers leave BGRA alpha undefined.
  Placement math was extracted into `WebcamOverlayLayout` so both compositors share one
  implementation; a unit test renders through the CPU compositor and asserts its blended bounding
  box equals the layout rectangle the GPU path uses.
- **Failure policy.** Anything failing while *starting* the GPU path
  (`TryStartGpuCapture`) logs to the diagnostics log and falls back to the CPU pipeline. A Direct2D
  failure *mid-recording* (typically `D2DERR_RECREATE_TARGET`) disables overlays for the rest of that
  recording rather than losing the screen content. The report's `pipeline=` column always tells you
  which path actually ran.

### Branding preparation and ownership

`VideoRecordingService.PrepareCoreAsync` establishes the fixed encoder dimensions first.
`GpuCaptureSession.Initialize` creates the device/session without starting WGC callbacks.
The recorder then awaits `BrandingOverlayCompositor.PrepareAsync` (GDI+ font initialization
and rasterization on a worker), followed by a worker-thread
`GpuOverlayCompositor.PrepareBranding` (premultiplication and upload on that compositor's device).
Only after both complete does it start GPU capture; the shared timeline and frame pump still
begin later in `BeginPreparedAsync`, after encoder and webcam readiness. CPU recording prepares
the same badge before its pump starts. Disabled branding performs neither preparation nor upload.

The recorder holds its lifecycle gate and awaits each worker to completion, including when
cancellation arrives during a non-interruptible native call. No preparation runs under the active
capture lock, and no WGC callback or pump can touch this session's immediate/D2D context during
upload. Other users of the process-wide D3D device retain the existing required
`ID3D11Multithread` protection. No preparation worker uses the D2D context concurrently with
capture or another preparation worker.

Recording draws are cache-only (`DrawPrepared` / `DrawBranding`): they do not initialize fonts,
rasterize, or upload, including after a preparation failure. Failure is best-effort and logged;
an upload failure disables only the GPU badge, leaving other overlays and the CPU badge usable.
GPU capture-start failure still falls back to CPU and re-prepares if its output height differs.
The badge scales with output height, not width; window-resize letterboxing retains the fixed
encoder dimensions. Each new recording has fresh CPU/GPU compositor instances, so a different
size or recreated D3D device never reuses a device-bound bitmap from an earlier recording.
Cancelled uploads dispose their unpublished bitmap; discard/stop disposes the GPU compositor.
Screenshot and GIF callers keep their lazy CPU rendering behavior.

Separate local `Branding preparation:` diagnostics record CPU preparation `elapsedMs` (including
worker scheduling), GPU `uploadMs` (premultiplication plus bitmap creation), and ready/unavailable/
cancelled/disabled outcomes. The capture-flow trace marks preparation completion.
These are preparation costs, **not** `OverlayBranding` samples or active-recording cadence:
the latter measures only per-frame drawing. Existing performance-report counter schemas are
unchanged.

Deterministic tests cover prepared/lazy pixel parity and scaling, cache-only frame access,
height changes, repeat compositor lifetimes, best-effort failure caching, and cancellation before
and during CPU preparation. They do not validate native D2D upload/device removal, real frame
cadence, or A/V synchronization. Native x64 and native ARM64 checks must separately exercise
fresh-process/repeated synthetic-window recordings with branding on/off, both pipelines/backends,
discard during preparation, size changes, pause/resume, and device recreation. Measure preparation
and first-frame latency separately; control other overlays (`+overlays` in the benchmark also
enables clicks). Use synthetic disposable content, avoid concurrent real-time benchmarks, keep
local measurements private, and include actual audio sources/listening before claiming A/V sync.

## 4. Instrumentation

`RecordingPerformanceMonitor` is created per recording for **both** pipelines and produces a
`RecordingPerformanceReport` (`IVideoRecordingService.LastPerformanceReport`) that is also written to
`%LOCALAPPDATA%\TinyClips\Temp\webcam-diagnostics.log` as `Perf report:` lines at stop time.

| Stage | CPU pipeline | GPU pipeline |
|-------|--------------|--------------|
| `CaptureReadback` | staging copy + `Map` + memcpy to `byte[]` | `CopyResource` + `Flush` (GPU→GPU) |
| `FrameProduce` | copy into reusable video processing buffer | pool rent + `CopySubresourceRegion` |
| `Composite` | all CPU overlay blends | all Direct2D draws + `Flush` |
| `OverlayClicks` / `OverlayBranding` / `OverlayWebcam` | sub-stages of `Composite` | same (webcam includes the GPU upload) |
| `SamplePrepare` | bottom-up copy into encoder-owned memory | `CreateFromDirect3D11Surface` |
| `EncoderWait` | time `SampleRequested` waited for a frame | same |
| `EncoderHold` | — | hand-off → `MediaStreamSample.Processed` (how long the encoder held the texture) |

Per stage: count, average, **p99** (reservoir-sampled, 4096 samples, no per-frame allocation), max,
total. Per recording: wall clock, frames emitted/encoded/dropped, effective fps, **process CPU %**
and core-equivalents (`Process.TotalProcessorTime` delta), **managed allocation rate**, Gen0/1/2
collection counts, **total GC pause time** (`GC.GetTotalPauseDuration`), peak working set, and for
the GPU path the texture-pool high-water mark and pacer overruns.

### 4.1 Diagnostic contract (schema 2)

Reports and benchmark JSON now include `SchemaVersion: 2`. Existing report fields and the benchmark
array/scenario shape remain available. These are additive fields; the legacy `EffectiveFps`,
`DropPercent`, and `FramesEncoded` calculations are retained for existing consumers, **not** renamed
or silently reinterpreted. Use the following meanings when comparing new recordings:

| Fields | Meaning |
| --- | --- |
| `RequestedPipeline` / `Pipeline` | Requested CPU/GPU path versus the capture/composite path that actually initialized. CPU fallback does not inherit a GPU label. |
| `RequestedEncoderBackend` / `EncoderBackend` | Requested sink writer/transcoder versus the initialized backend, independently of CPU/GPU capture. |
| `D3DDriver` | `hardware`, `warp`, or `unknown`, recorded from the successful D3D device creation call. A GPU-resident path can use WARP. This is **not** encoder evidence. |
| `HardwareEncodingRequested` / `HardwareEncoding` | Acceleration request on the selected backend versus verified transform selection. Selection is currently `unknown`: neither backend inspects the encoder transform. Baseline fallback disables the hardware request but is still not a transform-inspection result. Sink-writer low-latency/no-B-frame settings are requests too. |
| `Geometry` | Initial requested physical rectangle, its intersection with WGC content, and the final even encoder rectangle. `WasClipped` and `WasEvenSized` identify intentional adjustments. Window targets use their WGC item size, not a monitor/DIP estimate. Encoder dimensions remain fixed during resize. |
| `Phases.Preparation` | Building the pipeline, including a failed GPU/backend attempt before fallback, until encoder-ready. |
| `Phases.PreparedWait` | Encoder-ready until emission starts; includes countdown reuse and the first-webcam wait. |
| `Phases.FirstFrameLatency` | Active time from emission start to the first successfully produced frame, excluding intentional pauses; `null` if none. Not latency to the first encoded/decoded output frame. |
| `Phases.ActiveRecording` / `Phases.Paused` | Time from pump start until both pumps stop, separated at pause/resume. Existing stop-time webcam/audio shutdown can still occur before the pumps stop and is included here. |
| `Phases.Finalization` / `FinalizationSucceeded` | Pump-stop through encoder/audio drain and finalization; no exception versus failure (or `null` if not established). This does not verify file contents and excludes subsequent resource disposal. |
| `WallClock` / `EffectiveFps` | Legacy start-to-report interval, including pause and finalization; submitted samples divided by that interval. CPU/GC/allocation denominators use this same interval, not active-only time. Preparation is separately timed, not included in these process deltas. |
| `FramesEncoded` / `FramesSubmitted` | The same count: accepted sink-writer `WriteVideo` results or `MediaStreamSample` handoffs. A stopped/disposed sink-writer rejection is not counted as submitted. Neither means decoded output. `VerifiedOutputFrames` remains `null` without independent decoding. |
| `SubmissionAttempts` / `SubmissionFailures` | Per-video-sample handoff attempts and exceptions, including drain-time handoffs. Failed channel reads without a sample are logged but are not submission attempts/failures. |
| `FramesNotSubmitted` | Emitted frames rejected at a pause boundary, after a channel completes, by a stopped/disposed sink writer, or by a CPU consumer failure before sample submission; not queue-full drops. |
| `FramesPendingSubmission` | Emitted minus submitted, queue-dropped, submission-failed and explicitly rejected frames (clamped at zero). An undrained queue or in-flight producer remains explicit at the report snapshot, not mislabeled as encoded/dropped. Pool/production loss precedes emission and is not subtracted here. |
| `ActiveFps` | All successful video submissions (including draining pre-stop frames) divided by active recording time. No intentional pause or encoder-finalization denominator. |
| `QueueDrops` / `PoolExhaustionEvents` / `ProductionFailures` | Separate failed queue admissions, failed allocator acquisitions, and failed CPU/GPU frame-production attempts before emission. Each site increments exactly one category and legacy `FramesDropped` once. CPU borrowed-consumer failures after emission belong to the submission budget instead. |
| `CpuSkippedTickEvents` | Observed active CPU timer callbacks rejected by the borrowed-frame processing gate or capture-cache `TryEnter`; at most one event per callback, **not** an estimate of every missed timer slot. Timer callbacks never dispatched cannot be recovered from this counter. |
| `GpuPacingOverrunEvents` / `GpuPacingMissedSlots` | One active overrun event per grid jump and the number of due slots it skips. Only a stable active cadence epoch across the previous callback and jump is counted, excluding pause/resume boundaries. |
| `CaptureReadbackFailures` / `NoSourceFrameTicks` | Failed WGC readbacks across the session (including preparation/pause), versus active pump attempts with no cached source yet. Neither is an encoder queue drop. Stage timing still starts at recording start. |
| `RepeatedSourceFrames` / `MaxEmittedFrameGap` | Frames reusing the same cached WGC source version, versus largest within-active-segment emitted PTS gap. Static-content repeats are normal output, not drops. The gap resets at pause/resume; no derived “lost frames” are added to any counter. |

**Do not sum** pacing events, pacing slots, frame gaps, readback failures and the drop aggregate into
a single loss count. An event and its skipped slots describe the same overrun; frame gaps can
describe those same slots. Legacy `DropPercent` uses emitted frames as its denominator even though
pool/production drops happen *before* emission, so it is not an overall cadence-loss percentage and
can exceed 100%. Use the individual categories and `ActiveFps`. The console comparison table,
full report, local log and JSON share these meanings. The extra raw GPU pacer slot log explicitly
includes pauses and is **not** the active-only structured counter.

Instrumentation uses atomic counters, a fixed 4096-entry reservoir per stage and constant-size
phase/emission state. There is no per-frame diagnostic object allocation, trace list or per-frame
success log write; CPU readback/processing failure messages are capped at three per category while
the counters continue counting. Working-set queries are throttled to approximately once per second. Output inspection,
driver/transform validation, live mixed-DPI capture and native ARM64 runs are separate manual
checks, not implied by deterministic tests.

## 5. Benchmark harness

`windows/tools/RecordingBenchmark` drives the production `VideoRecordingService` headlessly
(in-memory settings, temp output, no-op analytics) against the primary monitor and prints a
comparison table plus the full per-stage report for each scenario. It must run from an interactive
desktop session (WGC needs the DWM).

The harness establishes and verifies Per-Monitor-V2 awareness **before monitor enumeration**;
if it cannot establish physical coordinates, it fails rather than accepting DPI-virtualized bounds.
An oversized `--region` remains in the requested metadata; capture intersects it with the actual
WGC item before even-size cropping. Benchmark JSON also records `DpiAwareness` and `RequestedRegion`.
Only use synthetic/disposable or explicitly consented content. Do not run simultaneous real-time
benchmarks on a shared host. Reports remain local; nothing is uploaded.

```powershell
dotnet run --project windows/tools/RecordingBenchmark -c Release -p:Platform=x64 -- --seconds 10
dotnet run --project windows/tools/RecordingBenchmark -c Release -p:Platform=x64 -- --seconds 10 --fps 60 --scenarios cpu,gpu,gpu+sink
dotnet run --project windows/tools/RecordingBenchmark -c Release -p:Platform=x64 -- --scenarios gpu+sink+overlays,gpu+sink+hevc --webcam --audio --keep --json out.json
dotnet run --project windows/tools/RecordingBenchmark -c Release -p:Platform=x64 -- --scenarios gpu+sink --window "Notepad"
```

Scenarios follow `(cpu|gpu)[+overlays][+sink][+hevc]`: `+overlays` = branding badge + click visuals
(plus the webcam PiP with `--webcam`), `+sink` = the `IMFSinkWriter` backend, `+hevc` = H.265.
`--region WxH` records a centred region, `--window <title>` records a window (resize it to exercise
letterboxing), `--audio` adds system
audio, `--iterations N` repeats, `--keep` leaves the MP4s in `%TEMP%\TinyClipsBenchmark` for
inspection (e.g. `ffmpeg -ss 3 -i file.mp4 -frames:v 1 frame.png`).

Caveat: the click overlay is only exercised if you actually click during the run (the harness does
not synthesize input), so `OverlayClicks` rows mostly measure the no-clicks early-out.

### Local diagnostic collection and architecture evidence

Run `windows/tools/Collect-Diagnostics.ps1` from a repository checkout. The collector compiles the
same dependency-free `src/TinyClips.Core/Services/ProcessArchitectureClassifier.cs` used by the
deterministic tests; keep that file at its repository-relative location when distributing the
collector. Missing helper source fails before creating a results folder.

Target process, OS, and loaded runtime architectures are separate evidence:
`IsWow64Process2` supplies native OS / WOW machine codes, `GetProcessInformation` with
`ProcessMachineTypeInfo` supplies the target process machine, and a readable **loaded**
`coreclr.dll` PE supplies corroborating runtime-module evidence (not a bundled file or the
collector's own architecture). Both API results/failures are printed explicitly. A zero WOW
machine is “unspecified”, not proof of a native ARM64 runtime. Missing evidence yields `unknown`,
conflicting codes `ambiguous`, explicit ARM64EC/ARM64X codes `hybrid`, x86 on ARM64 `emulated`,
and x86 on x64 `wow64`. x64 on ARM64 is `emulated-or-hybrid`: final ARM64EC images can expose the
x64 ABI/machine code, so neither those APIs nor the simple PE machine read proves that all code
is emulated. The runtime PE field is corroborating module/ABI evidence, not an instruction-level
execution measurement. Module access failures also remain explicit; package PE entries are
distribution metadata, not runtime proof. See Microsoft's
[PROCESS_MACHINE_INFORMATION contract](https://learn.microsoft.com/windows/win32/api/processthreadsapi/ns-processthreadsapi-process_machine_information)
and [ARM64EC binary identification](https://learn.microsoft.com/windows/arm/arm64ec#identifying-arm64ec-binaries-and-apps).

The collector is opt-in and never uploads. Its existing logs/system/window-title collection can
contain private information: review the archive locally, and do not paste raw logs or machine
details into public issues. Classification tests use invented machine codes, not host profiling.

## 6. Results

The measurements below are historical, pre-schema-2 results. Their “encoded” columns count sample
submissions, their fps includes finalization, hardware transform selection was not verified, and
CPU skipped ticks were not structured. They do not establish hardware execution or universal
cadence guarantees. Re-measure with synthetic content and the schema-2 contract before drawing
new device-specific conclusions; this diagnostic change claims no measured performance gain.

Reference machine: AMD Ryzen AI 7 PRO 350 (16 logical cores) with integrated **Radeon 860M**
(Media Foundation H.264 requested; encoder transform unverified), 3440×1440 primary display, Windows 11
26200, .NET 10. Static desktop with a few windows; 10 s per scenario, 30 fps target, no audio.

| Scenario | Pipeline | Legacy submission fps | CPU (cores) | Alloc rate | Gen2 GCs | GC pause | Readback avg | Produce avg | Composite avg / p99 |
|----------|----------|------------:|------------:|-----------:|---------:|---------:|-------------:|------------:|--------------------:|
| cpu | cpu | **11.3** | 5.4 % (0.86) | **1180 MB/s** | 122 | 4.5 % | 16.6 ms | 2.9 ms | 0.01 / 0.09 ms |
| gpu | gpu | **28.0** | 2.0 % (0.33) | 0.5 MB/s | 1 | 0.1 % | 1.2 ms | 0.03 ms | 0.40 / 2.2 ms |
| cpu+overlays (badge) | cpu | 11.2 | 4.2 % (0.68) | 1122 MB/s | 109 | 3.4 % | 17.8 ms | 3.8 ms | 4.2 / 17.6 ms (max **412 ms**) |
| gpu+overlays (badge) | gpu | 27.2 | 1.8 % (0.28) | 0.4 MB/s | 1 | 0.1 % | 2.1 ms | 0.14 ms | 2.6 / 17.4 ms |
| cpu+overlays + webcam | cpu | 22.6 | 7.5 % (1.20) | 1849 MB/s | 100 | 2.6 % | 6.7 ms | 2.1 ms | 4.0 / 123 ms |
| gpu+overlays + webcam | gpu | 26.7 | 6.7 % (1.08) | 55 MB/s | 42 | 1.1 % | 1.8 ms | 0.35 ms | 2.3 / 19.1 ms |

At a **60 fps** target (10 s, no overlays):

| Scenario | Legacy submission fps | CPU (cores) | Alloc rate | Gen2 GCs | Dropped (pool exhausted) | EncoderWait avg |
|----------|------------:|------------:|-----------:|---------:|-------------------------:|----------------:|
| cpu | 38.9 | 5.3 % (0.85) | 2412 MB/s | 182 | 0 (pump skipped instead) | 24.6 ms |
| gpu | **42.6** | 2.5 % (0.40) | 0.6 MB/s | 0 | 73 of 576 | 3.1 ms |

Historical observations, not current cadence or transform-validation results:

1. **Higher sample-submission rates were observed on the GPU path.** The legacy interval reported
   roughly 28 vs 11 submitted samples/s at a 30 fps target with lower process CPU usage.
   These averages do not establish delivered output cadence or a guaranteed frame rate.
2. **Lower allocation and collection counts were reported in these runs.** GC could contribute
   to the CPU path's long composite/encoder-wait timings, but aggregate stage and GC counters
   alone do not attribute individual latency spikes.
3. **GPU overlay stage timings were small in the reference runs.** Historical frame extraction
   compared rendered appearance; it does not make overlays free or establish full-output parity.
4. **Long sample lifetimes coincided with pool exhaustion at 60 fps.** `EncoderHold` p99 was
   170-220 ms and 73 allocator drops were reported. This motivates testing buffering/backpressure
   and backend configuration, but does not identify the uninspected transform as a hardware
   encoder, prove look-ahead/B-frames caused the holds, or exclude other pipeline bottlenecks.
5. **Webcam delivery was another allocation candidate.** The GPU+webcam run reported 55 MB/s
   and 42 Gen2 collections; the camera-to-managed-buffer path warranted investigation, not
   exclusive attribution of every allocation or collection from these aggregate counts.

## 7. Risks and follow-ups

- **Driver coverage.** Historical runs used the reference AMD adapter; no encoder transform was
  inspected. NVIDIA/Intel and other adapter/transform combinations remain unvalidated follow-ups,
  including surface compatibility, hold times, pool high-water marks and `VIDEO_SUPPORT`
  behaviour. `D3DDriver=warp` explicitly identifies WARP,
  independently of the unverified encoder transform. (Phase 1 shipped the setting off; it was
  turned on by default in phase 2 alongside the What's new window — see §8.5.)
- **Window capture.** Window targets resize mid-recording; both video paths preserve the initial
  encoder dimensions and aspect-fit resized content with opaque black bars. CPU video recreates
  its WGC frame pool and bounds readback by both `ContentSize` and physical pitch/surface size;
  its allocation-free nearest-neighbour resize differs from GPU linear filtering (§1.1).
  Physical regions keep their clipped origin and black-pad missing pixels without scaling.
- **Device removal.** `GetSharedDevice` already recreates the shared D3D device on
  `DeviceRemovedReason`; a GPU reset mid-recording will surface as D2D `RECREATE_TARGET` (overlays
  disabled, recording continues) or as encoder failure (recording stops), same as today.
- **Pause/resume**, **time limit**, **discard**, and **pre-warm** (`PrepareAsync`) paths are shared
  with the CPU pipeline and exercised by the same `VideoRecordingService` state machine.
- **GIF and scrolling capture** still use `ContinuousCaptureSession` (they need retained CPU pixels
  for quantization/stitching); their published snapshots are not recycled (§1.1).
- **Next steps, in order of payoff:** (1) flip the default to GPU once NVIDIA/Intel are validated;
  (2) GPU webcam frames; (3) low-latency encoder configuration or Option B to cut `EncoderHold`;
  (4) expose the perf report in the Quick Bug Report so users can attach it.

## 8. Phase 2: sink-writer backend, window resize, GPU webcam

Prompted by a review of Clint Rutkas's [crutkas/tiny-clips](https://github.com/crutkas/tiny-clips)
spec (WGC → `IMFSinkWriter`), phase 2 added the items that phase 1 had deferred.

### 8.1 `IMFSinkWriter` encoder backend (`MfSinkWriterEncoder`)

*Setting: Video encoder → Low latency.* A push-model MP4 writer built on `Vortice.MediaFoundation`:

- `MFCreateSinkWriterFromURL` with `MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS`,
  `MF_SINK_WRITER_D3D_MANAGER` (an `IMFDXGIDeviceManager` reset to our shared `VIDEO_SUPPORT` device),
  `MF_LOW_LATENCY`, and `MF_SINK_WRITER_DISABLE_THROTTLING`.
- Video out: H.264 High or **HEVC Main**, CBR at the same bitrate formula as the transcoder (HEVC ×0.6).
  Video in: `MFVideoFormat_ARGB32` at the capture size — MF's Video Processor does BGRA→NV12 on the GPU.
- **Encoder configuration** via the `SetInputMediaType` encoding-parameters store (passed through to
  the encoder's `ICodecAPI`): `CODECAPI_AVLowLatencyMode=1`, `AVEncMPVDefaultBPictureCount=0`,
  `AVEncMPVGOPSize=2·fps`, `AVEncCommonRateControlMode=CBR`, `AVEncCommonMeanBitRate`,
  `AVEncCommonQualityVsSpeed=50`. These are the knobs `MediaTranscoder` has no way to set.
- Audio: PCM 48 kHz/16-bit/stereo in → AAC-LC 192 kbps out. A dedicated `AudioMux` thread pushes
  20 ms chunks gated on captured-frame availability (the same back-pressure rule as the pull path,
  shared through `AccountAudioChunk`), and drains the captured tail after Stop.
- **GPU frames come from `IMFVideoSampleAllocatorEx`** (`MF_SA_D3D11_BINDFLAGS = RENDER_TARGET|SHADER_RESOURCE`,
  `MF_SA_D3D11_USAGE = DEFAULT`). The allocator's samples are `IMFTrackedSample`s: when the sink
  writer releases one, its texture returns to the allocator automatically. That replaces the
  phase-1 texture pool + `MediaStreamSample.Processed` bookkeeping on this backend, and `AllocateSample`
  failing with `MF_E_SAMPLEALLOCATOR_EMPTY` is the natural "encoder is behind, drop at source" signal.
  `GpuFrame` became allocator-agnostic (`IGpuFrameAllocator`) so the transcoder keeps its pool.
- CPU frames are written as memory buffers (bottom-up BGRA, as MF expects for RGB32).
- Both stream writers take their own lock; `Finalize` takes both. A single lock across streams
  deadlocked intermittently against the sink writer's cross-stream throttling (fixed by per-stream
  locks *and* disabling throttling, since both streams are real-time paced already).
- Any failure creating the writer falls back to the transcoder; the report's `encoder=` column says
  which ran. GUIDs not surfaced by Vortice were verified against the Windows SDK 10.0.26100 headers.

### 8.2 Window resize (`GpuCaptureSession` + `GpuOverlayCompositor.BlitLetterboxed`)

Encoders cannot change frame size mid-stream, so the encoder frame stays at the initial size. When
WGC reports a different `ContentSize` the session recreates the frame pool at the new size
(`Direct3D11CaptureFramePool.Recreate`, legal from inside `FrameArrived`) and the pump asks the
compositor to **scale-to-fit with black letterboxing** via a single Direct2D `DrawBitmap` instead of
cropping. Verified by recording Notepad while resizing it 1200×800 → 700×900 → 1600×600
(`contentResizes=2` in the report; frames pillar/letter-boxed and centred). The CPU pipeline is unchanged.

### 8.3 GPU webcam frames (`WebcamCaptureService.SetPreferredDirect3DDevice`)

On the GPU pipeline the recorder hands the webcam service the shared `IDirect3DDevice`. The service
then initialises `MediaCapture` with `MemoryPreference.Auto` and, per frame, lets Media Foundation
copy (and colour-convert) the camera frame into a ring of three
`VideoFrame.CreateAsDirect3D11SurfaceBacked` BGRA surfaces on our device via `VideoFrame.CopyToAsync`.
`WebcamFrame.Surface` carries the surface; `GpuOverlayCompositor` wraps it as a Direct2D bitmap
(`AlphaMode.Ignore`, cached per ring slot) and fills the shaped PiP from a bitmap brush. Any failure
falls back to CPU frames for that recording. The CPU pipeline still receives pixel buffers, now
copied through `IMemoryBufferByteAccess` into a reusable ring instead of two fresh LOH arrays per frame.

### 8.4 Results (same machine as §6; 10 s, 3440×1440)

These historical results use the same legacy sample-submission and unverified-transform semantics
as section 6; neither zero queue/pool drops nor near-target average submissions proves output cadence.

| Scenario | Legacy submission fps | CPU cores | Alloc | Gen2 | Composite avg/p99 | Size |
|---|---:|---:|---:|---:|---:|---:|
| cpu (transcoder) | 17.0 | 0.75 | 1546 MB/s | 167 | 0.01 / 0.05 ms | 10.2 MB |
| gpu (transcoder) | 27.5 | 0.22 | 0.4 MB/s | 0 | 0.8 / 13.5 ms | 17.7 MB |
| **gpu + sink writer** | **29.8** | **0.18** | 0.2 MB/s | 0 | **0.4 / 1.6 ms** | 17.7 MB |
| **gpu + sink writer + HEVC** | 29.6 | 0.17 | 0.2 MB/s | 0 | 0.4 / 1.4 ms | **10.6 MB** |
| cpu + overlays | 19.1 | 0.48 | 1655 MB/s | 129 | 68.6 / **1140 ms** (GC) | 10.7 MB |
| gpu + overlays (transcoder) | 27.9 | 0.25 | 0.4 MB/s | 0 | 1.5 / 20.7 ms | 17.7 MB |
| **gpu + sink writer + overlays** | 29.6 | **0.15** | 0.2 MB/s | 0 | 0.7 / 2.6 ms | 17.6 MB |

**60 fps** target:

| Scenario | Legacy submission fps | Dropped | CPU cores | Allocator high-water |
|---|---:|---:|---:|---:|
| cpu (transcoder) | 19.6 | 0 (pump skipped) | 0.99 | — |
| gpu (transcoder) | 44.3 | 57 | 0.25 | 14–16 of 30 |
| **gpu + sink writer** | **59.6** | **0** | 0.22 | **1** of 30 |

**Webcam** (gpu + sink writer + overlays, 8 s): webcam overlay cost **1.9 ms → 0.095 ms** per frame
with GPU delivery (all 268 camera frames stayed on the GPU, source = `Direct3DSurface`); total CPU
0.43 cores vs 1.03 for the CPU pipeline with the same overlays, 29.4 fps vs 20.9.

Historical observations:

1. **The sink-writer run reported near-target submission averages and a lower allocator
   high-water mark.** Low-latency/no-B-frame configuration was requested, not verified.
   These results do not prove those settings were honored, that 60 fps output was sustained,
   or that transcoder look-ahead was the only bottleneck.
2. **The HEVC sample file was smaller with similar reported CPU usage.** This is a result for
   these files, not a general 40% saving; H.264 remains the default for playback compatibility.
3. **GPU webcam delivery had lower measured overlay-stage cost in these runs.** The historical
   direct-copy/no-webcam comparisons motivated investigating camera/interop collection pressure,
   but do not make the overlay free or establish exclusive GC causality.
4. **The two CPU backend runs had similar submission rates.** Readback was a candidate cost,
   but aggregate averages do not establish it as the sole bottleneck.

### 8.5 Remaining follow-ups

- GPU + Low latency are now the defaults. Historical runs used the reference AMD adapter, with
  selected encoder transforms unverified; NVIDIA/Intel compatibility still needs device testing.
  Both fall back automatically, the `encoder=`/`pipeline=` report columns show the initialized paths,
  and the What's new window tells users where the switch is. Watch the first release's bug reports for
  `falling back to` lines in `webcam-diagnostics.log`.
- The CodecAPI keys are applied best-effort; log which ones the encoder accepted (`ICodecAPI::IsSupported`).
- Lossless keyframe-aligned trim through the sink writer (source reader → sink writer pass-through)
  to replace the `MediaComposition` re-encode in the trimmer.
- Surface the perf report in the Quick Bug Report.
