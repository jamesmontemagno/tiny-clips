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

The original recorder (still the default, now called the **CPU pipeline**) moves every frame through
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

Measured on the reference machine (below), the CPU pipeline at 3440×1440@30 sustained only
**~11 fps** end-to-end (114 frames encoded in 10 s) with zero "dropped" frames — the drop counter
only counts channel back-pressure, while the real loss was the **pump skipping ticks**: the pump
used `Monitor.TryEnter` and gave up whenever the WGC thread held the lock during its 10–17 ms
readback, and `System.Threading.Timer`'s ~15.6 ms granularity cannot hold a 33.3 ms cadence
precisely.

## 2. Options considered

| Option | Verdict | Why |
|--------|---------|-----|
| **A. Keep `MediaTranscoder` + `MediaStreamSource`, feed D3D11 surfaces** via `MediaStreamSample.CreateFromDirect3D11Surface` | **Chosen** | It is the documented Microsoft pattern ([Screen capture to video](https://learn.microsoft.com/windows/uwp/audio-video-camera/screen-capture-video), SimpleRecorder). Keeps the existing encoder prep / Baseline fallback / AAC mux / audio back-pressure code untouched. MF handles BGRA→NV12 on the GPU through its Video Processor MFT, so the hardware encoder receives GPU memory end-to-end. |
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
MediaTranscoder (HardwareAccelerationEnabled) → Video Processor MFT (BGRA→NV12 on GPU) → H.264 HW encoder → MP4
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
  look-ahead window. Measured `EncoderHold` (hand-off → `Processed`) on AMD VCN: **~25–50 ms avg,
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
    `BrandingOverlayCompositor.TryGetBadge` (same GDI+ rasterization as the CPU path);
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

## 4. Instrumentation

`RecordingPerformanceMonitor` is created per recording for **both** pipelines and produces a
`RecordingPerformanceReport` (`IVideoRecordingService.LastPerformanceReport`) that is also written to
`%LOCALAPPDATA%\TinyClips\Temp\webcam-diagnostics.log` as `Perf report:` lines at stop time.

| Stage | CPU pipeline | GPU pipeline |
|-------|--------------|--------------|
| `CaptureReadback` | staging copy + `Map` + memcpy to `byte[]` | `CopyResource` + `Flush` (GPU→GPU) |
| `FrameProduce` | `byte[]` clone | pool rent + `CopySubresourceRegion` |
| `Composite` | all CPU overlay blends | all Direct2D draws + `Flush` |
| `OverlayClicks` / `OverlayBranding` / `OverlayWebcam` | sub-stages of `Composite` | same (webcam includes the GPU upload) |
| `SamplePrepare` | bottom-up flip copy | `CreateFromDirect3D11Surface` |
| `EncoderWait` | time `SampleRequested` waited for a frame | same |
| `EncoderHold` | — | hand-off → `MediaStreamSample.Processed` (how long the encoder held the texture) |

Per stage: count, average, **p99** (reservoir-sampled, 4096 samples, no per-frame allocation), max,
total. Per recording: wall clock, frames emitted/encoded/dropped, effective fps, **process CPU %**
and core-equivalents (`Process.TotalProcessorTime` delta), **managed allocation rate**, Gen0/1/2
collection counts, **total GC pause time** (`GC.GetTotalPauseDuration`), peak working set, and for
the GPU path the texture-pool high-water mark and pacer overruns.

## 5. Benchmark harness

`windows/tools/RecordingBenchmark` drives the production `VideoRecordingService` headlessly
(in-memory settings, temp output, no-op analytics) against the primary monitor and prints a
comparison table plus the full per-stage report for each scenario. It must run from an interactive
desktop session (WGC needs the DWM).

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

## 6. Results

Reference machine: AMD Ryzen AI 7 PRO 350 (16 logical cores) with integrated **Radeon 860M**
(H.264 encoding via AMD VCN through Media Foundation), 3440×1440 primary display, Windows 11
26200, .NET 10. Static desktop with a few windows; 10 s per scenario, 30 fps target, no audio.

| Scenario | Pipeline | Encoded fps | CPU (cores) | Alloc rate | Gen2 GCs | GC pause | Readback avg | Produce avg | Composite avg / p99 |
|----------|----------|------------:|------------:|-----------:|---------:|---------:|-------------:|------------:|--------------------:|
| cpu | cpu | **11.3** | 5.4 % (0.86) | **1180 MB/s** | 122 | 4.5 % | 16.6 ms | 2.9 ms | 0.01 / 0.09 ms |
| gpu | gpu | **28.0** | 2.0 % (0.33) | 0.5 MB/s | 1 | 0.1 % | 1.2 ms | 0.03 ms | 0.40 / 2.2 ms |
| cpu+overlays (badge) | cpu | 11.2 | 4.2 % (0.68) | 1122 MB/s | 109 | 3.4 % | 17.8 ms | 3.8 ms | 4.2 / 17.6 ms (max **412 ms**) |
| gpu+overlays (badge) | gpu | 27.2 | 1.8 % (0.28) | 0.4 MB/s | 1 | 0.1 % | 2.1 ms | 0.14 ms | 2.6 / 17.4 ms |
| cpu+overlays + webcam | cpu | 22.6 | 7.5 % (1.20) | 1849 MB/s | 100 | 2.6 % | 6.7 ms | 2.1 ms | 4.0 / 123 ms |
| gpu+overlays + webcam | gpu | 26.7 | 6.7 % (1.08) | 55 MB/s | 42 | 1.1 % | 1.8 ms | 0.35 ms | 2.3 / 19.1 ms |

At a **60 fps** target (10 s, no overlays):

| Scenario | Encoded fps | CPU (cores) | Alloc rate | Gen2 GCs | Dropped (pool exhausted) | EncoderWait avg |
|----------|------------:|------------:|-----------:|---------:|-------------------------:|----------------:|
| cpu | 38.9 | 5.3 % (0.85) | 2412 MB/s | 182 | 0 (pump skipped instead) | 24.6 ms |
| gpu | **42.6** | 2.5 % (0.40) | 0.6 MB/s | 0 | 73 of 576 | 3.1 ms |

Takeaways:

1. **The GPU pipeline delivers the requested frame rate.** 28–30 fps vs 11 fps at 30 fps target on
   a 5 MP display; 2.5× the frames for 0.4× the CPU.
2. **Allocations drop from ~1.2 GB/s to ~0.5 MB/s** and Gen2 collections from >100 to ~0 per
   recording. This is what removes the latency spikes — the CPU path's 400 ms composite max and
   850 ms+ `EncoderWait` p99 were GC pauses, not compute.
3. **Overlays are effectively free on the GPU** (0.08 ms badge, 0.4 ms webcam including upload,
   0.1 ms clicks) and pixel-identical to the CPU output (verified by frame extraction).
4. **The remaining bottleneck is the hardware encoder's hold time**, not our pipeline: at 60 fps the
   encoder held frames long enough to exhaust a 30-texture pool (73 drops), and `EncoderHold`
   p99 was 170–220 ms. That is an encoder/driver property (look-ahead, B-frames — we request the
   High profile) and the lever for it is Option B (own sink writer with tuned encoder attributes) or
   requesting a low-latency encoder mode.
5. **With the webcam on, the CPU cost is now in webcam *capture*, not compositing.** The
   gpu+webcam run allocated 55 MB/s and paid 42 Gen2 GCs, all from
   `WebcamCaptureService` converting each `MediaFrameReference` to a managed `byte[]`. That is the
   next obvious GPU move (keep the camera frame as a `Direct3DSurface` and draw it straight into the
   Direct2D pass).

## 7. Risks and follow-ups

- **Driver coverage.** Validated on AMD VCN only. NVIDIA (NVENC) and Intel (QSV) MFTs should accept
  the same `IDirect3DSurface` samples (it is the documented pattern), but hold times, pool high-water
  marks and `VIDEO_SUPPORT` behaviour need checking; WARP falls back to software encoding and the
  `pipeline=`/`encoder=` report columns make that visible. (Phase 1 shipped the setting off; it was
  turned on by default in phase 2 alongside the What's new window — see §8.5.)
- **Window capture.** Window targets resize mid-recording; the GPU session recreates the "latest"
  texture on size change but the pooled encoder textures are fixed to the initial size (as in the CPU
  path, the encoder profile is fixed too). Behaviour is unchanged from the CPU path (crop/clamp).
- **Device removal.** `GetSharedDevice` already recreates the shared D3D device on
  `DeviceRemovedReason`; a GPU reset mid-recording will surface as D2D `RECREATE_TARGET` (overlays
  disabled, recording continues) or as encoder failure (recording stops), same as today.
- **Pause/resume**, **time limit**, **discard**, and **pre-warm** (`PrepareAsync`) paths are shared
  with the CPU pipeline and exercised by the same `VideoRecordingService` state machine.
- **GIF and scrolling capture** still use `ContinuousCaptureSession` (they need CPU pixels for
  quantization/stitching); it gained optional instrumentation only.
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

| Scenario | Encoded fps | CPU cores | Alloc | Gen2 | Composite avg/p99 | Size |
|---|---:|---:|---:|---:|---:|---:|
| cpu (transcoder) | 17.0 | 0.75 | 1546 MB/s | 167 | 0.01 / 0.05 ms | 10.2 MB |
| gpu (transcoder) | 27.5 | 0.22 | 0.4 MB/s | 0 | 0.8 / 13.5 ms | 17.7 MB |
| **gpu + sink writer** | **29.8** | **0.18** | 0.2 MB/s | 0 | **0.4 / 1.6 ms** | 17.7 MB |
| **gpu + sink writer + HEVC** | 29.6 | 0.17 | 0.2 MB/s | 0 | 0.4 / 1.4 ms | **10.6 MB** |
| cpu + overlays | 19.1 | 0.48 | 1655 MB/s | 129 | 68.6 / **1140 ms** (GC) | 10.7 MB |
| gpu + overlays (transcoder) | 27.9 | 0.25 | 0.4 MB/s | 0 | 1.5 / 20.7 ms | 17.7 MB |
| **gpu + sink writer + overlays** | 29.6 | **0.15** | 0.2 MB/s | 0 | 0.7 / 2.6 ms | 17.6 MB |

**60 fps** target:

| Scenario | Encoded fps | Dropped | CPU cores | Encoder held frames (high-water) |
|---|---:|---:|---:|---:|
| cpu (transcoder) | 19.6 | 0 (pump skipped) | 0.99 | — |
| gpu (transcoder) | 44.3 | 57 | 0.25 | 14–16 of 30 |
| **gpu + sink writer** | **59.6** | **0** | 0.22 | **1** of 30 |

**Webcam** (gpu + sink writer + overlays, 8 s): webcam overlay cost **1.9 ms → 0.095 ms** per frame
with GPU delivery (all 268 camera frames stayed on the GPU, source = `Direct3DSurface`); total CPU
0.43 cores vs 1.03 for the CPU pipeline with the same overlays, 29.4 fps vs 20.9.

Takeaways:

1. **Low-latency encoder configuration removed the last bottleneck.** With `AVLowLatencyMode` and
   no B-frames the encoder holds **one** input frame instead of 14+, so 60 fps is sustained with zero
   drops and `WriteSample` costs ~0.1 ms. The transcoder's opaque look-ahead was the whole problem.
2. **HEVC** cuts file size ~40 % (17.7 → 10.6 MB) at identical CPU cost; keep H.264 the default for
   playback compatibility.
3. **The GPU webcam path makes the webcam overlay free** (<0.1 ms). The remaining Gen2 collections
   seen with the webcam on (~60 per 8 s even at 0.2 MB/s managed allocation) are *induced* by the
   WinRT camera pipeline's RCW/finalizer pressure, not by our code — bisected by swapping the copy
   path with `TINYCLIPS_WEBCAM_DIRECT_COPY=0` and by recording with no webcam (0 GCs).
4. **Transcoder vs sink writer on the CPU pipeline** is a wash (both bounded by the readback); the
   sink writer's value is on the GPU pipeline.

### 8.5 Remaining follow-ups

- GPU + Low latency are now the defaults (validated on AMD VCN; NVIDIA/Intel use the same documented
  MF pattern). Both fall back automatically, the `encoder=`/`pipeline=` report columns show what ran,
  and the What's new window tells users where the switch is. Watch the first release's bug reports for
  `falling back to` lines in `webcam-diagnostics.log`.
- The CodecAPI keys are applied best-effort; log which ones the encoder accepted (`ICodecAPI::IsSupported`).
- Lossless keyframe-aligned trim through the sink writer (source reader → sink writer pass-through)
  to replace the `MediaComposition` re-encode in the trimmer.
- Surface the perf report in the Quick Bug Report.
