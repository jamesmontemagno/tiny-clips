# Studio rendering and export on Windows

`TinyClips.Core.Studio.Rendering` draws Studio frames and exports projects. It follows sections 6.5
and 6.7 of `docs/studio-project-format.md` and what `windows/spikes/StudioEngineSpike` measured.

## The pieces

- `StudioGraphicsDevice`: one Direct3D 11 device (graphics hardware, or WARP where there is none),
  its immediate context, and `Gate`, the lock for both. `CreateRenderTexture`, `ReadTexture`.
- `StudioSceneRenderer`: draws one frame with Direct2D into a `B8G8R8A8_UNorm` render target:
  background, screen shadow, screen, click rings, camera shadow, camera, border, badge. Layer edges
  sit on whole pixels. `StudioRenderQuality.Preview` samples linearly; `Export` uses high-quality
  cubic sampling, which keeps a picture drawn at a quarter of its size from aliasing.
- `StudioExporter`: `ExportAsync` writes an MP4 (H.264 or HEVC), `WritePosterAsync` a JPEG.
  `StudioExportService` adapts it to `IStudioExportService`.
- Internal: `StudioVideoSource`, `StudioAudioSource`, `StudioSinkWriterEncoder`, `StudioAudioPump`,
  `StudioBackgroundImage`. `StudioRenderingMath` holds the unit-tested rules, and
  `DeleteStaleTemporaryFiles(folder, age)` for what an export that was killed leaves behind.

## How an export runs

1. A worker thread creates its own device and renderer; nothing is shared with the preview. Not
   asked for a quality, it samples with high quality on graphics hardware and linearly on WARP.
2. The source readers get the device manager and deliver RGB32 textures, which the renderer wraps
   without a copy (on WARP they decode to memory and frames are uploaded). Their frame rate
   conversion is off (`MF_XVP_DISABLE_FRC`): it moves a recording's frames onto an even grid.
3. Output frame `i` shows the source at the middle of its duration, `(i + 0.5) / fps` through the
   `StudioTimeMap`, and the camera at that time minus `startOffset`. A source frame shows from the
   instant it starts until the next starts. A reader seeks only backwards or over a second ahead.
4. Each frame is drawn straight into a texture from the sink writer's allocator. The hardware
   encoder is tried first, then the software one, then frames through system memory; a failure
   within the first two seconds of video starts the export again with the next.
5. Sound is the screen file's first track decoded to PCM, cut by sample count, encoded as AAC.
   44.1 and 48 kHz in mono or stereo keep their shape; other rates become 48 kHz, more channels stereo.
6. Media Foundation encodes with BT.601 up to 576 lines and BT.709 above; the file is tagged so.
7. The video is written as `.<name>.<guid>.tcexport` beside the output and moved into place last.
   A failed or cancelled export leaves nothing new and leaves an earlier file at that path alone.

## Threading and lifetime rules

- A renderer is not thread-safe and draws through the device's immediate context: create, call and
  dispose it holding `StudioGraphicsDevice.Gate`. `ReadTexture` and other uses of `Context` take it too.
- The renderer keeps a Direct2D wrapper for every texture it has seen, which keeps the texture
  alive. Call `ForgetSources()` before source textures are released or replaced, `ForgetTarget(t)`
  before a target is released or its swap chain resized, and dispose the renderer before the device.
- A source frame must stay valid until `Render` returns. Sources are BGRA or BGRX textures on the
  renderer's device; a target in any other format than BGRA is refused with an `ArgumentException`.
- After `StudioDeviceLostException`, dispose the renderer and the device and create both again.
- `StudioExporter` can be called from any thread; both methods check their arguments and return.
  Progress reaches 1 only once the file is in place. A failure is a `StudioExportException`,
  `FileNotFoundException`, `DirectoryNotFoundException` or `InvalidOperationException` whose message
  a user can read; cancellation is an `OperationCanceledException`.

## Checking it

```powershell
dotnet test windows\tests\TinyClips.Core.Tests\TinyClips.Core.Tests.csproj -c Debug
dotnet run --project windows\tools\StudioRenderCheck\StudioRenderCheck.csproj -c Release -p:Platform=x64
```

`StudioRenderCheck` is headless and needs no ffmpeg. It writes its own clips with Media Foundation
(a number in every frame, colour patches, a tone burst every second), checks them, then renders and
exports through the public API and reads the results back from pixels and decoded samples. Its
`recorder` group feeds `StudioCameraRecorder` frames as the webcam service delivers them. 142 checks
in about five minutes; a `FAIL` line and exit code 1 for a failure. `--only <groups>`,
`--match <text>` and `--out <folder>` narrow a run and keep its files; `--help` lists the groups.

## Measured (AMD Radeon 860M on a shared PC, three runs, so ranges)

- Export of 20 s (2560×1440 screen, 720p camera), hardware H.264: at 1920×1080, 95–133 fps with
  linear and 101–126 fps with high-quality sampling; at 2560×1440, 83–118 and 83–113 fps (the
  spike: 131–238). Software encoder at 1920×1080: 51–55 fps. WARP: 37–55 fps linear, its default,
  and 18–19 fps high-quality, which is slower than the video plays.
- One frame drawn: 0.5–1.1 ms linear, 1.6–3.4 ms high-quality; on WARP 2.7–8.2 ms and 45–83 ms.
- Every frame of every export shows the screen and camera frames worked out by hand. Sound starts
  with the picture and ends within 11 ms of it; tone bursts are within 0.02 ms, lag 0 samples.
- Colour, export against source: 1.0–1.6 of 255 with the hardware encoder, 4.7–5.2 in software.
- Picture edges within 0.32 px of the layout. Cancellation stops the export in 170–230 ms.

## Known limits

- A source taller than 576 lines that is tagged BT.601 is decoded by Media Foundation as BT.709:
  colours up to 24 of 255 off. Tiny Clips' own recordings are not tagged that way.
- Background images: PNG, JPEG, BMP, GIF, TIFF, WebP. Others (HEIC) fall back to the primary colour.
- The regular recorder's CPU path writes its file upside down on this PC, in H.264 and in HEVC.
  The tool measures it on every run and reports it as known; nothing here changes that recorder.
- Not exercised: rotated or anamorphic sources, HE-AAC sound, other GPU vendors, ARM64.
