# GPU Recording Pipeline on Windows

This document is the research write-up and design record for the Windows video recorder's
**GPU-resident ("zero-copy") pipeline**, the per-stage **performance instrumentation** added to both
pipelines, and the **benchmark harness** used to compare them. It complements
[`audio-video-sync.md`](audio-video-sync.md), which covers timeline/sync; nothing here changes the
timeline model — both pipelines stamp frames with the same `RecordingTimeline`.

- Setting: **Settings → Video → Recording & output → GPU recording pipeline** (`UseGpuRecordingPipeline`, default **off** while experimental).
- Code: `GpuCaptureSession`, `GpuFrameTexturePool`, `GpuOverlayCompositor`, `FramePacer`,
  `RecordingPerformanceMonitor` in `windows/src/TinyClips.Core/Capture/`; pipeline selection in
  `VideoRecordingService.PrepareCoreAsync` / `TryStartGpuCapture`.
- Benchmark: `windows/tools/RecordingBenchmark`.

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
| B. Native MF `IMFSinkWriter` + `IMFDXGIDeviceManager` + own Video Processor MFT | Rejected for now | Full control (encoder MFT selection, NV12 conversion, B-frame/GOP tuning) but hundreds of lines of COM lifetime code, and the earlier `MediaFoundationEncoderSpike` found Vortice's MF wrappers unusable. Revisit only if A's encoder hold time becomes the bottleneck. |
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
dotnet run --project windows/tools/RecordingBenchmark -c Release -p:Platform=x64 -- --seconds 10 --fps 60 --scenarios cpu,gpu
dotnet run --project windows/tools/RecordingBenchmark -c Release -p:Platform=x64 -- --scenarios cpu+overlays,gpu+overlays --webcam --keep --json out.json
```

Scenarios: `cpu`, `gpu`, `cpu+overlays`, `gpu+overlays` (`+overlays` = branding badge + click visuals,
plus the webcam PiP with `--webcam`). `--region WxH` records a centred region, `--audio` adds system
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
  `pipeline=`/`encoder=` report columns make that visible. Until then the setting ships **off**.
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
