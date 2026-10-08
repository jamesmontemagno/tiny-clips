# Studio rendering and export on Windows

`TinyClips.Core.Studio.Rendering` draws Studio frames and exports projects. It follows sections 6.5
to 6.9 of `docs/studio-project-format.md` and what `windows/spikes/StudioEngineSpike` measured.

## The pieces

- `StudioGraphicsDevice`: one Direct3D 11 device (graphics hardware, or WARP where there is none),
  its immediate context, and `Gate`, the lock for both. `CreateRenderTexture`, `ReadTexture`.
- `StudioSceneRenderer`: draws one frame with Direct2D into a `B8G8R8A8_UNorm` render target:
  background, screen shadow, screen, click rings, camera shadow, camera, border, badge. Layer edges
  sit on whole pixels. `StudioRenderQuality.Preview` samples linearly; `Export` uses high-quality
  cubic sampling, which keeps a picture drawn at a quarter of its size from aliasing. It draws the
  layers the resolved frame has and does not ask the layout: while a scene is being entered a
  frame can have a layer its layout does not. A layer whose opacity is below 1 goes through a
  Direct2D layer, so its shadow, picture, border and click rings fade as one. The background and
  the shadow of a screen that is whole are kept as one bitmap between frames; a screen that moves
  has it made again for every frame, and one that fades has its shadow drawn with it.
- `StudioExporter`: `ExportAsync` writes an MP4 (H.264 or HEVC), `WritePosterAsync` a JPEG.
  `StudioExportService` adapts it to `IStudioExportService`.
- Internal: `StudioVideoSource`, `StudioAudioSource`, `StudioSinkWriterEncoder`, `StudioAudioPump`,
  `StudioPcmGain`, `StudioBackgroundImage`. `StudioRenderingMath` holds the unit-tested rules, and
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
   Each 16-bit sample is multiplied by the project's volume (`audio.volume`, 0 to 1) on its way
   to the encoder and rounded to the nearest step; at 1 the samples are handed on as they are.
   At 0 the video keeps a sound track, of silence; a muted project has none.
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
`recorder` group feeds `StudioCameraRecorder` frames as the webcam service delivers them. Its `zoom`
group draws, exports and posters zooms (section 6.8 of the format) and measures which part of the
screen each frame shows from where four edges of the pattern are. Its `scenes` group does the same
for scenes entered with a morph (section 6.9): where each layer is on its way is worked out by
hand, the pattern is measured inside the screen and the camera, their outlines are looked for,
and a layer that fades is compared with what is under it. With `--keep` or `--out` both groups
save every frame they looked at as a PNG. 182 checks in about four minutes; a `FAIL` line and
exit code 1 for a failure. `--only <groups>`, `--match <text>` and `--out <folder>` narrow a run and keep its
files; `--help` lists the groups.

## Measured (AMD Radeon 860M on a shared PC, three runs, so ranges)

- Export of 20 s (2560×1440 screen, 720p camera), hardware H.264: at 1920×1080, 95–133 fps with
  linear and 101–126 fps with high-quality sampling; at 2560×1440, 83–118 and 83–113 fps (the
  spike: 131–238). Software encoder at 1920×1080: 51–55 fps. WARP: 37–55 fps linear, its default,
  and 18–19 fps high-quality, which is slower than the video plays.
- One frame drawn: 0.5–1.1 ms linear, 1.6–3.4 ms high-quality; on WARP 2.7–8.2 ms and 45–83 ms.
- Every frame of every export shows the screen and camera frames worked out by hand. Sound starts
  with the picture and ends within 11 ms of it; tone bursts are within 0.02 ms, lag 0 samples.
- The volume (8 October 2026): the same recording exported at 100%, 50% and 0%. At 50% the sound
  is 6.03 dB below the one at 100% by loudness and 5.99 dB by the loudest sample (half is 6.02);
  at 0% every sample is 0.
- Colour, export against source: 1.0–1.6 of 255 with the hardware encoder, 4.7–5.2 in software.
- Picture edges within 0.32 px of the layout. Cancellation stops the export in 170–230 ms.
- Zooms: the pattern's edges are within 0.08 px of where the zoom window puts them in a drawn frame
  (0.21 px on WARP), within 0.13 px in an exported one and 0.04 px in a poster. A frame at 2× draws
  no slower than one with no zoom: 0.18–0.20 against 0.27 ms linear, and 0.50–0.58 against
  1.1–1.2 ms high-quality.
- Scenes entered with a morph (one run): the pattern's edges are within 0.22 px of where the
  layers are worked out to be in a drawn frame (0.25 px on WARP), within 0.08 px in an exported
  one and 0.02 px in a poster. A camera fading in an export has its colours within 2.2 of 255 of
  the mix worked out by hand. A frame with a gradient, both shadows and a border takes 0.34 ms
  at rest, 1.5–1.8 ms while both cards move, and 1.4–1.5 ms while the screen fades; on WARP
  1.6–1.8, 13–16 and 10–11 ms.

## Known limits

- A source taller than 576 lines that is tagged BT.601 is decoded by Media Foundation as BT.709:
  colours up to 24 of 255 off. Tiny Clips' own recordings are not tagged that way.
- Background images: PNG, JPEG, BMP, GIF, TIFF, WebP. Others (HEIC) fall back to the primary colour.
- The regular recorder's CPU path writes its file upside down on this PC, in H.264 and in HEVC.
  The tool measures it on every run and reports it as known; nothing here changes that recorder.
- Not exercised: rotated or anamorphic sources, HE-AAC sound, other GPU vendors, ARM64.
