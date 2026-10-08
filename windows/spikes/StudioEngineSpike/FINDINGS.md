# Studio engine spike: findings

Todo `m0-win-engine-spike`. Throwaway prototype in `windows\spikes\StudioEngineSpike`; how to build and run it is in [README.md](README.md). All five questions are answered with measurements. What could not be measured is listed with each question and again at the end.

## In brief

| Question | Answer | Recommendation |
|---|---|---|
| 1. Preview decode and sync | Two frame-server `MediaPlayer`s on one `MediaTimelineController` work. At 1x every frame of both clips is delivered and the clips are a frame apart for 0.2–1.9 % of the time, never more than one frame. A composite takes 0.5 ms. A paused frame can be redrawn on demand in 1 ms. A paused seek shows the exact frame when it delivers one, but 0–2.7 % of them deliver nothing. | Use it, with a seek policy (one seek at a time, middle of the frame, watchdog and repair): 800 of 800 random seeks correct at about 100 ms each. Copy frames at preview size. |
| 2. Presenting in WinUI 3 | Both paths work and behave alike: pixel-exact, same pacing, same sync on screen, same resize and scale behaviour, about the same code size. | (b), a plain `SwapChainPanel` with a DXGI swap chain through `ISwapChainPanelNative`: no extra copy, one Direct2D device, full control of the swap chain. |
| 3. Export | 131–238 fps (4.4–7.9 times real time) for 30 s at 2560×1440 with audio. Decoder output wraps as a Direct2D bitmap without a copy when the reader outputs RGB32. All 900 output frames carry the expected frame numbers; a trim-in off a keyframe is exact; the audio round trip shifts by 0.00 ms. | Source readers → shared renderer → sink writer with low latency **off**. |
| 4. Two encoders while recording | A second hardware encoder at 1920×1080 changes nothing measurable on the screen stream: no drops, 0–2 late ticks in 900, 5 times real-time headroom with both running. | Second hardware H.264 sink writer, up to 1920×1080 at 30 fps; software 1280×720 as fallback. |
| 5. NativeAOT | Nothing breaks, including the swap chain panel interop and Win2D. Two trim warnings, both inside Vortice's COM layer and not reached. | No AOT-specific accommodation needed. |

The design that follows from this is at the end, under "Recommended engine design", with the pitfalls the real implementation must avoid.

## Status

| Question | State |
|---|---|
| 1. Preview decode and sync | **answered, measured** |
| 2. Presenting in WinUI 3 | **answered, measured**; the window was covered by another application's full-screen window in the final run, so DXGI frame statistics come from earlier runs |
| 3. Export | **answered, measured** |
| 4. Two encoders while recording | **answered, measured** with synthetic frames |
| 5. NativeAOT | **answered, measured** |

## Environment

- AMD Ryzen AI 7 PRO 350 (8 cores, 16 threads), integrated AMD Radeon 860M, driver 32.0.31041.1004
- Windows 11 Enterprise 10.0.26300, one 3440×1440 display at 100 Hz and 150 % scaling
- .NET SDK 10.0.401 (runtime 10.0.12, ILCompiler 10.0.12), Visual Studio Build Tools 2022 for the AOT link, Windows App SDK 1.8 (`Microsoft.WindowsAppSDK.WinUI` 1.8.260528001), Win2D 1.4.0, Vortice 3.8.3, ffmpeg 9.0.2
- Media Foundation offers: `AMDh264Encoder` (hardware), `Microsoft AVC DX12 Encoder` (hardware, async), `H264 Encoder MFT` (software); `Microsoft H264 Video Decoder MFT`; Microsoft AAC encoder and decoder
- The machine was shared while everything here was measured: other agents were running `dotnet build` and `dotnet test`, and for the second half a full-screen PowerPoint slide show that was not part of this work covered the display. Where a number moved between runs, the range is given.
- The system timer period was 1.0 ms whenever it was read (another process had requested it). It was not read during the first export and preview runs.

## Test media

`StudioEngineSpike media` generates the two clips with ffmpeg into `media\` and checks them with ffprobe and ffmpeg.

| | screen.mp4 | camera.mp4 |
|---|---|---|
| Video | H.264 High, 2560×1440, 30 fps, 900 frames, keyframe every 2 s, no B-frames, BT.709 limited | H.264 High, 1280×720, 30 fps, 900 frames, same GOP |
| Audio | AAC-LC 48 kHz stereo: a 50 ms tone burst at the start of every second, pitch 400 + 100 × second Hz | none |
| Size | 40 MB (11.3 Mbit/s; the content is easy, so x264 undershoots the 24 Mbit/s target) | 10 MB |

Every frame carries its frame number as text and as a strip of 12 black/white cells with their complements underneath. A read counts only if every column has one bright and one dark cell, so a covered, blended, or stale strip never decodes to a plausible wrong number. The camera's strip sits in the centre so it survives the square crop, the circle mask, and mirroring. Four flat colour patches and the strip's always-black and always-white cells give six reference colours. The camera clip is treated as having started 0.2 s after the screen clip, so screen frame N belongs with camera frame N − 6.

## 1. Preview decode and sync

**Answered, measured.** `StudioEngineSpike preview`, headless (no window; every result is read back from the composited texture or from the players' textures). JIT build, 1920×1080 preview canvas, each clip copied at its full size unless stated. Numbers are from the final run (`out\preview\report.txt`, 18 minutes); where an earlier run of the same code gave a different number, both are given. Sample composites are saved as PNG (`out\preview\play-1x-mid.png` shows the gradient, the screen clip as a rounded card with its shadow showing "SCREEN 360", and the mirrored camera circle bottom-right showing "CAM 354", six frames behind as intended).

### What was built

Two `MediaPlayer`s (`IsVideoFrameServerEnabled`, `CommandManager.IsEnabled = false`, muted), both attached to one `MediaTimelineController`. Each `VideoFrameAvailable` calls `CopyFrameToVideoSurface` into that clip's BGRA texture on the shared D3D11 device; a render thread then draws the scene with Direct2D (Vortice) into a canvas texture on the same device. Nothing is copied between devices or to the CPU.

It works without a window, on an MTA thread, unpackaged, from plain file paths (`MediaSource.CreateFromUri`). Both players raised `MediaOpened` 0.39 s after construction (0.55 s in an earlier run) and each delivered its first frame on its own while paused, without a seek.

### Time per composite

Paused, both source textures resident, 200 composites per row, time until the GPU has finished (not just until the commands were submitted):

| Canvas | Cached shadows, linear | Cached shadows, high-quality cubic | Shadow effect every frame | No shadows |
|---|---|---|---|---|
| 1280×720 | 0.54 ms (p95 0.77) | 2.32 ms | 1.14 ms | 0.34 ms |
| 1920×1080 | 0.50 ms (p95 0.69) | 2.81 ms | 1.25 ms | 0.30 ms |
| 2560×1440 | 0.52 ms (p95 0.70) | 2.96 ms | 2.57 ms | 0.68 ms |
| 3840×2160 | 1.48 ms (p95 2.11) | 4.62 ms | 5.59 ms | 0.85 ms |

"Cached shadows" blurs each shadow once per layout into a bitmap and blits it; rebuilding that bitmap when the layout changes costs 1.9 ms at 1080p. CPU time to submit a composite is 0.03–0.25 ms. During playback, when the GPU is also decoding and copying two clips, the same 1080p composite takes 0.75–2.5 ms on average and 1.3–6.4 ms at the 95th percentile (the larger values include waiting for the GPU to finish the frame copy that came just before); the longest was 7.9 ms at 1x and 22 ms, once, at 2x.

Colours: the six reference colours in the players' textures and in the composite are within 0.5/255 of what ffmpeg decodes with the BT.709 limited-range matrix.

### Playback at 1x

20 s, every frame identified from its pixels:

| | Screen 2560×1440 | Camera 1280×720 |
|---|---|---|
| Frames delivered | 600 of 600 | 600 of 600 |
| Dropped / repeated / out of order | 0 / 0 / 0 | 0 / 0 / 0 |
| Interval between frames | mean 33.33 ms, p50 30.0, p95 40.1, max 40.5 | mean 33.33 ms, p50 30.0, p95 40.1, max 41.1 |
| `CopyFrameToVideoSurface`, CPU side | mean 5.9 ms, p95 8.9, max 12.9 | mean 3.4 ms, p95 5.2, max 8.3 |
| Until the GPU finished that copy | + 4.5 ms | + 1.3 ms |
| Controller position − frame start, at the callback | mean 5.1 ms, max 10.4 | mean 5.1 ms, max 10.8 |

- Frames arrive on a 10 ms grid (30, 30, 40 ms), which is this display's 100 Hz refresh. A display with another refresh rate was not available.
- `floor(PlaybackSession.Position × fps)` read inside the callback equals the frame number in the pixels in 600 of 600 callbacks on both clips. That is how the real engine can know which frame a texture holds without reading pixels.
- Process CPU: 25–31 % of one core for both players plus compositing.
- Three more 12 s runs with less instrumentation: 360 of 360 on both clips each time.

**How far apart the two clips are at 1x.** The callbacks of matching frames start 0.07 ms apart on average (p95 0.28 ms, p99 1.1 ms, max 9.7 ms). Compositing on every arrival, the composite shows the two clips one frame apart for 0.26 % and 0.45 % of the time in the two final runs (6 of 1200 and 6 of 719 composites; longest stretch 13 ms) and for 0.9–1.4 % in earlier runs (longest stretch 27 ms). Never more than one frame. Sampled the way a 60 Hz or 100 Hz display would latch it, 0.2–0.8 % of refreshes (1.2–1.6 % in the earlier runs) show mismatched clips.

Waiting up to 8 ms for the other clip before compositing did not help reliably: 0.00 % in one earlier run, 0.37 % in the final run with one 44 ms stretch, and it made 2x clearly worse (below).

### Other rates

| Rate | Frames delivered (screen / camera) | Clips on different frames | Notes |
|---|---|---|---|
| 0.5x | 149 of 149 / 148 of 148 | 0.05 % of the time (1 of 297 composites), earlier runs 0.3–0.9 % | intervals 60/70 ms; CPU 15 % of a core |
| 2x, composite on every arrival | 99.3 % / 96.8 % | 13.0 % of the time, longest stretch 68 ms; 6.6 % in an earlier run and 42 % in the NativeAOT run, in which each screen copy took 7.2 ms instead of 5.4 | 2.8 % of screen frames never composited; CPU 51–61 % of a core |
| 2x, 8 ms pairing window | 98.9 % / 96.7 % | 22.1 % of the time, longest 195 ms | 17 % of screen frames never composited: worse than not waiting |
| 2x, players alone (no compositing) | 99.2 % / 98.5 % | n/a | CPU 29 % of a core |

At 2x the players deliver 60 frames per second and there is no slack. The first 2x run read a few pixels back inside the callback to identify every frame, which forces a GPU sync of about 5 ms per frame; that alone made the screen player drop 14 % of its frames. The real engine must do nothing in `VideoFrameAvailable` except the copy.

### Smaller copy targets

`CopyFrameToVideoSurface` scales to whatever texture it is given. A separate run (`preview --texture-scale 0.5`, `out\preview-half\report.txt`) copied the screen into 1280×720 and the camera into 640×360 instead of their full sizes:

| | Full-size targets | Half-size targets |
|---|---|---|
| 1x: frames delivered | 600 of 600 on both | 600 of 600 on both |
| 1x: `CopyFrameToVideoSurface`, CPU side, screen / camera | 5.9 / 3.4 ms | 3.8 / 3.0 ms |
| 1x: clips on different frames | 0.26–0.45 % of the time | 0.46–0.77 % (0.00 % with the 8 ms pairing window) |
| 1x: process CPU | 25–31 % of a core | 18–25 % |
| 2x: frames delivered, screen / camera | 99.3 % / 96.8 % | 99.8 % / 99.8 % |
| 2x: clips on different frames | 13.0 % of the time (6.6 % and 42 % in other runs) | 1.4 % (longest stretch 29 ms) |
| 2x: screen frames never composited | 2.8 % | 0.2 % |
| 2x: frames dropped with a pixel readback in the callback | 14 % | 0.2 % (1 frame) |
| 2x: process CPU | 51–61 % of a core | 35–49 % |

With targets no larger than the preview needs, 2x stops being marginal. The full-resolution frame is still available on demand when paused (below).

### Pause

40 cycles of play, `Pause()`, wait 500 ms:

- No callback arrived after `Pause()` returned (0 of 40).
- The two clips were on matching frames in 40 of 40 (39 of 40 in an earlier run and in the NativeAOT run; after 2x playback they were one frame apart in 2 of 3 pauses).
- The picture was one frame behind the frame that contains `Controller.Position` in 4 of 40 (4–7 of 40 in earlier runs). An editor that shows "frame 660" from the clock while the picture shows 659 is wrong by a frame.
- `floor(PlaybackSession.Position × fps)` taken at each player's last callback equals the frame on screen in 40 of 40.

Remedy, measured: after `Pause()`, take the frame the screen clip shows (from its session position at its last callback) and seek both players to the middle of that frame through the seek policy below. 60 of 60 pauses then had matching clips and the clock inside the frame on screen. The snap had something to fix in 7 of 60, took 1.8 ms at the median, 70 ms at p95, and 327 ms once (a repaired seek).

### Seek while paused

`Controller.Position = middle of frame N`, both clips, checked from pixels.

- **When a frame is delivered it is the exact frame**: 248 of 250 random seeks, and all 100 single-frame steps (50 forward, 50 back), showed frame N on the screen and N − 6 on the camera. There is one callback per player per seek (one seek of the 250 produced two on the screen player).
- Latency until both frames arrived: mean 70 ms, p95 143 ms, max 175 ms. It is decode time from the previous keyframe: 11 ms on a keyframe, 128 ms when 50 or more frames into the 60-frame GOP, about 2.0 ms per 2560×1440 frame and 1.5 ms per 1280×720 frame. A step back onto the last frame of the previous GOP took 180 ms.
- **Not every seek delivers a frame.** 2 of those 250 seeks produced no `VideoFrameAvailable` on the screen player at all. `PlaybackSession.Position` already reported the target; waiting 1.5 s, assigning the same position again, and assigning position + 1 tick all changed nothing, so the texture kept showing the old frame (frames 17 and 646 instead of 810 and 491). Over all runs the rate was between 0 % and 2.7 % per batch of 250–300 seeks issued one at a time (final run: 0 of 1000 in one test, 2 of 250, 7 of 800, 2 of 300 and 2 of 528 in the others; NativeAOT run: 1 of 250 and 6 of 300), on either player. With one seek at a time it was never a wrong frame, always a missing one.
- What tells a lost seek from a slow one: `PlaybackSession.SeekCompleted` is raised for every seek. On a delivered seek the frame callback starts before it (5.8 ms earlier on average, always earlier in the final run, at most 2.3 ms later in an earlier run; n ≈ 2000 each). On a lost seek it is raised 4–11 ms after the position change and no callback follows (timing from the earlier runs; all 11 losses the seek policy met in the final run were recognised this way).
- What brings the frame back: seeking to the neighbouring frame and then back (18 of 18 in an earlier run, and all 14 repairs in the final run). Pulling with `CopyFrameToVideoSurface`, the same position again, and a position a quarter of a frame later did not.
- Overlapping seeks: position changes 0–50 ms apart without waiting ended on the right frame in 150 of 150 bursts in the final run but in 148 of 150 in an earlier run (one stale camera frame, one unreadable composite). Free scrubbing at 30 position changes per second delivers about 11 frames per second per clip (9–13 across runs), and the composites made on the way show the clips up to 12 frames apart.
- Position rounding: a position anywhere from 2 % to 98 % of the way through frame N shows frame N. A position one 100 ns tick before the frame's nominal start shows N − 1, except on frames whose start is a whole number of ticks (333, 777), where it still shows N. Seeking to a frame's start time is therefore off by one for some frames and not others; the middle of the frame is always right.

**Seek policy** (`Engine\SeekCoordinator.cs`): one seek in flight at a time; the newest request replaces any waiting one; always the middle of a frame; a seek counts as lost when `SeekCompleted` has been raised and no frame callback has started 40 ms later (500 ms timeout behind that); a lost seek is repaired by seeking to the neighbouring frame and back.

| Test | Result | Latency, request to both frames |
|---|---|---|
| 800 random seeks | 800 correct; 7 needed the repair (0.88 %), all 7 spotted through `SeekCompleted` | mean 101 ms, p95 165, max 343; repaired ones mean 263 ms |
| 150 steps forward, 150 back | 300 correct; 2 repairs | mean 89 ms, p95 139, max 283 |
| 60 s drag, a request every 16.7 ms | 58 of 58 rest points show the last requested frame; 17 % of requests became seeks, 8.7 frames per second | last request to settled: mean 122 ms, p95 304 |

With only the 500 ms timeout as watchdog (an earlier run), a repaired request took about 740 ms instead of 205–263 ms.

**Seek during playback** needs none of this: 60 of 60 position changes while running at 1x resynchronized; the first composite with both clips on matching frames at the new position came after 102 ms on average (p95 207 ms, max 242 ms).

### Single-frame steps

| Method | Result | Time until both frames |
|---|---|---|
| `Controller.Position` = middle of next/previous frame, through the seek policy | 300 of 300 | mean 89 ms, p95 139 ms |
| `MediaPlayer.StepForwardOneFrame()` on both players | 300 of 300 moved exactly +1 on both clips, across 5 keyframes | mean 24 ms, p95 38, max 47 (mean 7 ms, max 11 in an earlier 40-step run) |
| `MediaPlayer.StepBackwardOneFrame()` on both players | **unreliable**: −1 in 45 of 60, −2 in 15; the camera once −3 and once 0; clips left two frames apart once | mean 110 ms |

`StepForwardOneFrame` does not move `Controller.Position` (it stayed at 6.683 s while the players showed 16.667 s). After 10 such steps, `Resume()` produced no frames at all in the 300 ms the test waited, presumably because the players wait for the controller's clock to reach them. Assigning `Controller.Position` to the middle of the frame on screen first fixes it (verified: both clips kept their frame, and playback then delivered 501, 502, …).

### Re-render on demand while paused

Yes. The players' textures keep their last frame, so the scene can be redrawn at any time without touching the players: 120 of 120 composites at new camera positions showed the same two frames at the moved position, with 0 `VideoFrameAvailable` callbacks, in 1.0 ms each (max 3.1 ms) with cached shadows and 3.4 ms with the shadow effect run every frame. `out\preview\drag-mid.png` shows it.

`CopyFrameToVideoSurface` also works outside the callback while paused, into a texture of any size (the player scales): 9.8 ms for the 2560×1440 clip at full size, 4.5 ms into 1280×720; 4.1 ms and 3.3 ms (into 640×360) for the camera. This allows a preview-resolution texture to be replaced by a full-resolution one on pause.

### Offsets and edges

- `TimelineControllerPositionOffset` is **added** to the controller position: with +0.2 s the camera showed frame 156 when the screen showed 150. A camera that started 0.2 s after the screen needs **−0.2 s**.
- A `Controller.Position` assigned immediately after changing the offset was lost on that player in every run (the camera showed 144 instead of 145; in the NativeAOT run the very first position after the first offset was lost as well). Assigning the position again fixed it.
- Before the camera's first frame (timeline time < 0.2 s) the camera player sits on its frame 0 and raises no callback when its clamped position does not change. The renderer has to hide the camera by timeline time; the player will not say so.
- The controller keeps running past the end of the clips (31.5 s, state `Running`, `Duration` null). The players raise `MediaEnded`; a seek back works. With `Controller.Duration = 15 s` it raised `Ended` once and paused itself at 15.002 s showing frame 449 (frame 450 in an earlier run, so the last picture is within one frame of the end); paused seeks beyond the duration still work.

### What failed, and why

- Lost paused seeks (above): a `MediaPlayer` behaviour, worked around by the seek policy.
- `StepBackwardOneFrame`: steps two frames a quarter of the time. Not used.
- The 8 ms pairing window: no reliable gain at 1x, harmful at 2x. Not used.
- Reading pixels back in the frame callback at 2x: drops frames. Measurement artifact, and a warning about the callback budget.
- The final run reports "2 checks did not hold": the lost position after an offset change and the 2 lost seeks. Both are findings, not harness faults.

### Recommendation

Use two frame-server `MediaPlayer`s on one `MediaTimelineController` for the preview. At 1x and 0.5x it delivered every frame of both clips and kept them on matching frames for 98.6–99.95 % of the time, never more than one frame apart. The real implementation must:

1. Do only `CopyFrameToVideoSurface` in `VideoFrameAvailable`, then wake the render thread. Composite on every arrival; do not wait for the other clip.
2. Take the frame number of a texture from `floor(PlaybackSession.Position × fps)` inside the callback.
3. Route every paused position change through a seek policy like `SeekCoordinator`: one at a time, middle of the frame, `SeekCompleted`-based watchdog, neighbour-and-back repair.
4. After `Pause()`, snap both players to the frame the screen shows.
5. Step forward with `StepForwardOneFrame` on both players, then set `Controller.Position` to the middle of the new frame. Step back with a seek.
6. Use a negative `TimelineControllerPositionOffset` for a clip that starts late, and re-seek after changing it.
7. Hide a clip outside its own time range in the renderer, and set `Controller.Duration` to the project length.
8. Give each player a copy target no larger than the clip appears in the preview (in physical pixels). It lowers the cost of every frame and is what makes 2x work: with full-size targets about 1–4 % of frames are skipped at 2x and the clips are a frame apart for 7–42 % of the time; with half-size targets 0.2 % and 1.4 %. When paused, pull the frame again at full resolution if the preview is zoomed.

## 2. Presenting in WinUI 3

**Answered, measured, with one limit on the evidence.** `StudioEngineSpike present` opens a minimal WinUI 3 window (unpackaged, self-contained Windows App SDK 1.8) with the two paths side by side, both fed by the same two `MediaPlayer`s and each drawing the same scene with the shared renderer. Numbers are from the final run (`out\present\report.txt`, 103 s) unless stated.

The limit: this is a shared machine, and from about halfway through the work a full-screen PowerPoint slide show, not part of this work, was in front of everything. The spike window was created, laid out and composed by DWM, and every check below that reads **screenshots** uses Windows.Graphics.Capture of the spike's own window, which does not depend on the window being in front. But the window was not on the physical display during the final run, so **DXGI frame statistics were all zero** in it. The statistics quoted below are from earlier runs of the same presenters, made while the window was visible; they are marked "earlier run, window visible".

### The two paths

- **(a) Win2D.** A `CanvasSwapChainPanel` with a `CanvasSwapChain` on a `CanvasDevice` made by `CanvasDevice.CreateFromDirect3D11Device` from the shared device. The shared renderer draws the scene into a texture of the panel's pixel size; that texture is wrapped as a `CanvasBitmap` (`CreateFromDirect3D11Surface`, no copy) and drawn into the swap chain with a Win2D drawing session.
- **(b) DXGI.** A plain `SwapChainPanel`, a swap chain from `IDXGIFactory2.CreateSwapChainForComposition` on the shared device (BGRA, 2 buffers, flip-sequential), attached with `ISwapChainPanelNative.SetSwapChain` (IID `63AAD0B8-7C24-40FF-85A8-640D944CC325`). The shared renderer draws straight into the back buffer.

A render thread presents; the UI thread only attaches the swap chains and passes on size and scale changes.

### Which works

Both. `out\present\window.png` (2982×1091, described here because the folder is not committed) shows the window with the two panels next to each other, each 1462×928 physical pixels for a 974.67×618.67 DIP panel at 150 %. Both show the same picture: the gradient, the screen card with "SCREEN 345" and the mirrored camera circle with "CAM 339". A XAML label ("XAML overlay on (a)" / "(b)") and a small orange XAML square in the bottom-right corner are drawn **over** each swap chain, so XAML content such as drag handles can sit on top of either. `panel-a-win2d.png` and `panel-b-swapchainpanel.png` are the two panels cut out.

What the screenshot proves, by pixels rather than by eye:

- Each presenter writes a small test pattern into the four corners of what it draws: a bar of a key colour, then 1 px vertical stripes, then 1 px horizontal stripes. In the screenshot all four are found for both paths at exactly 8×24 pixels with a contrast of 255 between neighbouring stripe pixels. One buffer pixel is one screen pixel; nothing is scaled on the way.
- The distance between the corner patterns gives the on-screen size of each swap chain: 1462×928, equal to the buffer size and to the panel's size in physical pixels.
- The XAML corner square sits exactly where the swap chain ends (offset +44, +28 from the last pattern, as computed), so the swap chain fills the panel.
- The frame-number strips decoded from the screenshot read 345 and 339 in both panels.

The same checks passed after every resize and scale change below.

### Cost

| | (a) Win2D | (b) DXGI |
|---|---|---|
| Attach on the UI thread (create device/swap chain, hand to the panel) | 9.9 ms (12–20 ms in earlier runs) | 2.5 ms (3–4 ms) |
| One 1462×928 frame, drawn on demand, until the GPU has finished | 1.18 ms (1.28 in another run) | 0.99 ms (1.00) |
| CPU time to draw during playback | 0.34–0.45 ms | 0.21–0.29 ms |
| `Present` call during playback | 0.43–0.77 ms | 0.30–0.56 ms |
| Extra GPU memory | one more texture of the panel's size | none |

The 0.2–0.3 ms per frame is Win2D's copy of the composited texture into its swap chain. Win2D creates its swap chain itself: BGRA, 2 buffers, flip-sequential, premultiplied alpha, no flags. There is no way to ask for anything else (more buffers, a waitable swap chain, another format).

### Frame pacing

**A present loop running flat out** (sync interval 1, paused picture, 3 s) locks to the display on both paths: 101.3 presents per second each, `Present` blocking for 9.6 ms on average. Earlier run, window visible: every present reached the screen on consecutive refreshes (281 of 282 intervals of one refresh for (a), 282 of 282 for (b)), and the loop sat 4–5 presents ahead of the screen on (a) and 1–4 on (b). That is 10–50 ms of latency for nothing, so the preview must not present in a loop; it presents when there is something new.

**Playback at 1x, presenting when a clip's texture holds a newer frame than the panel last drew.** Six rounds of 10 s, alternating sync interval 1 and 0, both panels live, with every screenshot the system delivered (about 32 per second, one per composition in which the window changed) decoded for the two clips' frame numbers in both panels:

| | (a) Win2D | (b) DXGI |
|---|---|---|
| Presents per second | 30.5–31.5 | 30.2–31.6 |
| Screen frames of the stretch that were seen on screen | 301–302 of 301–302 in every round | the same |
| Time on screen with the two clips one frame apart, sync interval 1 | 0.6 %, 0.6 %, 1.7 % | 0.2 %, 0.9 %, 1.9 % |
| The same, sync interval 0 | 0.9 %, 1.1 %, 0.3 % | 0.8 %, 0.7 %, 0.7 % |
| Longest such stretch | 10–20 ms | 10–30 ms |

- The picture changes every 3 or 4 refreshes (30 or 40 ms on this 100 Hz display), as the frames arrive.
- The two panels show the same screen frame in 97–100 % of the screenshots, and when they differ it is by one frame, in either direction.
- Neither the path nor the sync interval makes a difference that stands out from the spread between rounds. An earlier complete run gave 0.4–1.7 % for (a) and 0.3–1.9 % for (b), with one 60–70 ms stretch during a hiccup that also lost 8 frames on both panels.
- These on-screen figures agree with question 1's headless estimate (0.3–1.4 % of the time).
- A panel waits 3–4 ms on average (p95 8 ms) for the device gate before it can draw, because a `MediaPlayer` callback is still copying the other clip's frame. That wait is what keeps a half-updated pair off the screen most of the time. The render thread alternates which panel goes first, so that this does not look like a difference between the paths; before it did, the first panel showed 2–5 % mismatched screenshots and the second almost none.
- `Present` never blocked at this rate, with either sync interval (longest call 7 ms; 9 ms in an earlier run).
- Earlier run, window visible, DXGI statistics: all presents reached the screen ((a) 366 of 366, (b) 355 of 355), consecutive presents were 3 or 4 refreshes apart on screen (200 and 109 times for (a)), and one present was queued when the next was issued.

**With the UI thread blocked for 1.5 s during playback** both paths kept presenting: (a) 50 presents while blocked against 49 in the 1.5 s before, (b) 45 against 46. Two screenshots taken 0.63 s apart inside the block show screen frame 143 and then 161 in both panels. Presentation does not depend on the UI thread. (Earlier run, window visible: 42 of 42 and 39 of 39 presents reached the screen during the block.)

**Dragging the camera while paused** (120 layout changes at 60 Hz, each redrawn and presented on demand, no `MediaPlayer` involved): from the request to both panels presented took 3.1 ms on average, 5.7 ms at p95 and 6.5 ms at most (1.6–2.5 ms on average in earlier runs). The screenshot at the end shows the camera at its new place with frames 200 and 194 in both panels.

### Resize

After a single `AppWindow.Resize` during playback (five sizes from 2000×800 to 3300×1250):

- Both swap chains had the new size 15–73 ms after the call returned (11–84 ms in other runs), with one `SizeChanged` per panel.
- The resize itself took 2.6–46.5 ms on (a) and 1.7–20.4 ms on (b). The short ones are the work; the long ones are `ResizeBuffers` waiting for presents that are still queued. Long ones occurred on both paths in every run, more often on (a).
- At every size the screenshot checks passed for both paths: pixel-exact, on-screen size equal to the buffer size, swap chain ending where XAML puts the panel's corner.

During a continuous resize (the window edge moved in and out again, 90 size changes in 1.5 s, during playback), every screenshot was checked for whether each swap chain ends where XAML layout has the panel's corner at that moment:

| Swap chains resized | (a) in step | (b) in step | Out of step by | Window resize took |
|---|---|---|---|---|
| by the render thread before its next present | 26 % (left column), 5 % (right) | 11 % (right), 14 % (left) | up to 54–81 px | 1.5 s |
| on the UI thread at the end of each layout pass, then redrawn and presented there | 49 % (left), 30 % (right) | 29 % (right), 44 % (left) | up to 18–36 px | 2.9–3.2 s |

- The two paths behave alike; what differs is the column. The right-hand panel also moves when the window is resized, and when its swap chain is still larger than the panel its right edge is cut off by the window.
- A swap chain that is resized follows XAML layout a frame or more late, on either path. With the render thread doing it, only 18–21 of 43–48 size changes were applied (each `ResizeBuffers` took 29–38 ms under this load) and presents fell to about 17 per second while the window was being dragged.
- Resizing on the UI thread halves the error but makes the UI thread the bottleneck: each size change cost it 31–34 ms instead of 14 ms, and the drag took twice as long.

### DPI and scale

- At this display's 150 % both paths are pixel-exact (above). (a) gets there by creating and resizing the `CanvasSwapChain` with the panel's DIP size and `96 × CompositionScaleX` as DPI; Win2D compensates by itself. (b) sizes its buffers as DIP × composition scale and sets `IDXGISwapChain2.MatrixTransform` to the inverse scale.
- A change of composition scale was simulated with a `ScaleTransform` on the panels' parent (×2/3, ×1/2, ×1, giving composition scales 1.0, 0.75 and 1.5). On both paths each change raised one `CompositionScaleChanged` and no `SizeChanged`, the buffers became 972×619, 729×464 and 1458×928, both were settled 11 ms later, and at each scale the screenshot was pixel-exact with the right frames.
- One thing the simulation taught: a swap chain is only pixel-exact if its panel sits on a whole pixel. In an earlier run the transform put the right-hand panel on a fractional pixel and its stripes came out at a contrast of 81 instead of 255. The test now makes the panel offset a multiple of 6 px first. A real DPI change does not have this problem, because XAML lays out again and rounds to the new pixel grid.
- **Not tested:** a real change of monitor DPI (moving the window to a monitor with another scale, or changing the display scale). The machine has one monitor and changing its scale would have disturbed whoever else was using it. The simulated change exercises the same event and the same resize code, but not the order in which `SizeChanged` and `CompositionScaleChanged` arrive in a real change. A display with a refresh rate other than 100 Hz was not available either.

### Code complexity

| | (a) Win2D | (b) DXGI |
|---|---|---|
| Presenter class (`Present\Presenters.cs`), code lines | 123 | 131 |
| COM interop needed to present | none | 25 lines: `ISwapChainPanelNative`, one method |
| COM interop used only to measure | 25 lines: `ICanvasResourceWrapperNative`, to reach the DXGI swap chain | none |
| Direct2D devices on the shared D3D11 device | two (the renderer's and Win2D's) | one |
| On resize | release the intermediate texture and its `CanvasBitmap`, `ResizeBuffers`, create both again | drop the renderer's Direct2D target for the back buffer, `ResizeBuffers`, set the matrix |
| Control over the swap chain | sync interval only | everything in the description, and `IDXGISwapChain2` |
| Dependency | Win2D (already referenced by TinyClips.App) | none beyond Vortice |

About the same amount of code. (a) hides the interop and the DPI matrix; (b) has fewer moving parts at run time.

### What failed, and why

- The first start of the window died with `XamlParseException` 0x802B000A and no text. The XAML application object in the spike is not called `App.xaml` and is not in the project root, so the SDK compiled it as a page. The generated `XamlTypeInfo.g.cs` then still contains a metadata provider class, but the application class does not implement `IXamlMetadataProvider`, so the framework has nobody to ask about the types that provider knows (the WinUI controls library's, Win2D's `CanvasSwapChainPanel`). Declaring the file as `ApplicationDefinition` in the project file fixed it. Checked both ways with clean builds: without the item the build succeeds with no warning and the window fails with this exception; with it the application class implements the interface and the window runs. This is a property of the spike's layout, not of either path, but the real app must not move its `App.xaml` without doing the same.
- DXGI frame statistics return success with every field zero while the window is covered by another application's full-screen window. They cannot be relied on as a health signal.
- Screenshots through Windows.Graphics.Capture came only every other refresh (20 ms apart on this 100 Hz display) until `GraphicsCaptureSession.MinUpdateInterval` was set to 1 ms. This matters to the recorder if it ever needs more than about 50 frames per second on a fast display.
- Nothing failed that is specific to (a) or to (b).

### Recommendation

**Use (b): a plain `SwapChainPanel` with a DXGI composition swap chain attached through `ISwapChainPanelNative`.** Both paths work and behave the same in pacing, sync, resize and scale, so the choice is made by the rest: (b) draws straight into the back buffer with the one shared renderer (no second Direct2D device, no intermediate texture, 0.2–0.3 ms less per frame, a quarter of the attach time), keeps the swap chain's description in the engine's hands, and costs 25 lines of source-generated COM interop. (a) is a sound fallback if that interop ever becomes a problem.

Whichever is used: present only when there is something new (a frame, a layout change, a resize), from a render thread, never in a loop; either sync interval will do at these rates; resize the buffers on the render thread and accept that the picture trails the window edge by a few frames during a drag, rather than block the UI thread; keep the preview panel on whole pixels.

## 3. Export

**Answered, measured.** `StudioEngineSpike export`, headless. Numbers are from the final run (`out\export\report.txt`, 4 minutes) and from a settings comparison run afterwards (`out\export-settings\report.txt`); earlier runs of the same code are quoted where they differ. Every exported file was decoded again with ffmpeg and the frame-number strips of the screen and the camera were read in **every** output frame, not a sample.

### What was built

One loop on one thread, everything on the shared D3D11 device:

1. Two `IMFSourceReader`s created with `MF_SOURCE_READER_D3D_MANAGER` and `MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING`, output type RGB32. Media Foundation builds "H.264 decoder (D3D11) → video processor (D3D11)" for each.
2. For each output frame the source frames that cover the **middle** of that frame are fetched (`FrameCursor.Get`), a BGRA texture is taken from the sink writer's `IMFVideoSampleAllocatorEx`, the same `SceneRenderer` as in the preview draws the scene straight into it, and the sample goes to `IMFSinkWriter.WriteSample`.
3. A third source reader decodes the screen clip's audio to 48 kHz stereo PCM; it is written to the sink writer's AAC stream (192 kbit/s), kept about half a second ahead of the video.

The encoder that Media Foundation picked was `AMDh264Encoder` (hardware MFT, asynchronous, D3D11-aware).

### Can decoder output be wrapped as a Direct2D bitmap?

| Reader output | Texture | Wrap with `CreateBitmapFromDxgiSurface` |
|---|---|---|
| RGB32 | `B8G8R8X8_UNorm`, array size 1, bind `ShaderResource \| RenderTarget`, 3 textures recycled | **yes**, no copy |
| ARGB32 | `B8G8R8A8_UNorm`, array size 1, same binding, 3–4 textures recycled | **yes**, no copy |
| NV12 (the decoder's own output) | one `NV12` texture **array of 9 slices**, bind `Decoder \| VideoEncoder` | **no**: `0x80070057` (invalid parameter) |

So a copy is not needed, provided the reader is asked for RGB and given the device manager; the GPU video processor then does the colour conversion into an ordinary 2D texture. Reading 90 samples of each clip directly from those textures gave 90 of 90 correct frame numbers, a frame drawn through the wrapped bitmap read back correctly, and the six reference colours are within 0.5/255.

Two consequences for the implementation: the reader recycles a pool of 3–4 textures, so a wrapped bitmap is only meaningful while its sample is held, and the wrappers should be cached by texture (about 10 are created per export). Copying each source frame to a texture of my own before drawing was also tried: 100 fps against 100–111 fps for its neighbours in the same run, so it buys nothing.

Sample times differ by output type: RGB32 gives 0, 333333, 666667 (rounded), NV12 gives 0, 333333, 666666 (truncated). Durations are 333333 in both. Code must not compare sample times for equality with `frame × 10⁷ / fps`.

### Export speed

30 s, 900 frames, 2560×1440 output, audio included. Each cell is one export.

| Encoder settings | Final run | Settings run (variants in turn) | Earlier runs |
|---|---|---|---|
| Low latency, writer throttling off (what Core's recorder uses) | 62, 63, 62 fps | 103, 120, 95 fps | 88, 106, 97 fps |
| Low latency, throttling on | 112 fps | 98, 121, 106 fps | |
| Not low latency, throttling off | 129 fps | 138, 146, 139 fps | |
| **Not low latency, throttling on ("offline")** | **238, 204, 131 fps** | **140, 140, 150 fps** | 233, 177, 140 fps |

- With offline settings the export runs at **131–238 fps, 4.4–7.9 times real time**. With the recorder's settings it runs at 62–120 fps.
- All 18 files of the settings run, and the recorder and offline files of the final run, are 42,391,371 bytes: the settings change when frames are encoded, not what is encoded.
- Where the time goes with recorder settings: the loop is blocked in `IMFVideoSampleAllocatorEx.AllocateSample` for 13–14 ms per frame in the final run (6–8 ms in the settings run), against 2.7–5.6 ms with offline settings. It never returned "allocator empty"; it simply did not return sooner. The other stages are small: decode 0.3 + 0.3 ms, composite 0.6–0.8 ms, `WriteSample` 0.04–0.5 ms, audio 0.3–0.5 ms per frame.
- The 62 fps of the final run is one frame per 16 ms. The system timer period was 1.0 ms throughout the settings run (another process on the machine had asked for it); it was not recorded during the final run. I suspect the low-latency path waits in timer ticks, but could not set the timer back to 15.6 ms on this shared machine to prove it. Calling `timeBeginPeriod(1)` myself made no difference when the timer was already at 1 ms (112–139 fps recorder, 133–163 fps offline). Every later export with recorder settings, all with the timer at 1.0 ms, ran at 95–139 fps (135 fps in the NativeAOT run, against 164 fps offline there).
- The spread between runs with the same settings (131 to 238 fps) is this shared machine; the slowest offline run also shows every stage about twice as slow.

Other sizes and variants (final run, recorder settings unless noted):

| Export | Speed |
|---|---|
| 1920×1080, 30 s | 214 fps, 7.1× (190–225 fps in earlier runs) |
| 1280×720, 30 s | 211 fps |
| 2560×1440 with a trim-in and a cut, 6 s of output | 132 fps |
| 2560×1440 without audio | 100 fps |
| 2560×1440 with the shadow effect run on every frame | 111 fps |
| 2560×1440, decode and composite only, no encoder | 208 fps (196–276 earlier) |
| 2560×1440, composite and encode a fixed frame, no decode | 62 fps (63–90 earlier) |

Process CPU was 0.2–1.4 cores. Decode on its own (no compositing, no encode):

| Decode only | Final run (two passes) | Earlier runs |
|---|---|---|
| 2560×1440 → RGB32 on the GPU | 223, 282 fps | 330–360 fps |
| 2560×1440 → NV12 (no conversion) | 481, 568 fps | 850–920 fps |
| 2560×1440 → RGB32 scaled to 1280×720 by the video processor | 494, 488 fps | 530–610 fps |
| 1280×720 → RGB32 | 724, 673 fps | about 720 fps |
| 2560×1440 → RGB32 without a D3D device (software decode) | 35, 39 fps | 37–39 fps |

The last row is what happens if the device manager is not set on the reader: 1.3 times real time for one clip.

### Frame accuracy

| Export | Screen frame number as expected | Camera frame number as expected |
|---|---|---|
| 2560×1440, recorder settings | 900 of 900 | 900 of 900 (hidden in the first 6, as intended) |
| 2560×1440, offline settings | 900 of 900 | 900 of 900 |
| 1920×1080 | 900 of 900 | 900 of 900 |
| 1280×720 | 900 of 900 | 900 of 900 |
| Trim and cut: keep 3.5–6.0 s and 8.5–12.0 s | 180 of 180 | 180 of 180 |

The trim-in at 3.5 s is frame 105, 45 frames after its keyframe: the first output frame shows screen 105 and camera 99, the last shows 359 and 353. Output timestamps are spaced exactly 33.33 ms, keyframes are 2.00 s apart, and the first frame is at 0.

Colours through the whole chain (H.264 → RGB → Direct2D → H.264, decoded as BT.709 limited range) are within 2.1–2.9/255 of the source. The output stream is tagged `color_range=tv` but its matrix, primaries and transfer are **untagged**; players assume BT.709 for these sizes, which is what the pixels are, but the real exporter should set them.

### Duration and audio/video alignment

- Video stream: 29.999967 s for 900 frames. Audio stream and container: 30.016 s, 1407 AAC frames. The extra 16 ms is the last AAC frame being padded to 1024 samples. Both streams start at 0. The 6 s export: 6.000 s video, 6.016 s audio.
- The tone bursts are found at every second with the right pitch (30 of 30; 5 of 5 in the cut export, so the cut kept audio and video together), and they are **21.35 ms late** relative to the video frame that marks the second. The offset is the same for every burst in every export (minimum = maximum).
- Where the 21.35 ms comes from: the source file, made by ffmpeg, measures +0.02 ms when ffmpeg decodes it and +21.35 ms when Media Foundation decodes it. 21.33 ms is 1024 samples at 48 kHz, one AAC frame of encoder priming, which ffmpeg removes through the MP4 edit list and Media Foundation's reader does not. It is a property of reading an ffmpeg-written file, not of the export.
- Proof: an export that takes its audio from the first export (a file written by Media Foundation) has the bursts at the same +21.35 ms. **One Media Foundation decode and AAC encode round trip moves the audio by 0.00 ms.** Files written by Media Foundation measure the same in ffmpeg and in Media Foundation.

Tiny Clips records with Media Foundation, so its own recordings should not show the 21 ms. That was not tested with a real recording.

### Seeking the readers

`SetCurrentPosition` on a video reader takes 0.4–2.3 ms and the next sample is the **keyframe at or before** the target. The target is reached by reading forward and discarding:

| Seek to | First sample after the seek | Samples read to reach the target | Time |
|---|---|---|---|
| middle of frame 60 (a keyframe) | frame 60 | 2 | 11 ms |
| middle of frame 61 | frame 60 | 3 | 23 ms |
| middle of frame 105 | frame 60 | 47 | 149 ms |
| middle of frame 119 | frame 60 | 61 | 176 ms |
| start of frame 120 (a keyframe) | frame 120 | 2 | 15 ms |
| one tick before frame 120 | frame 60 | 61 | 185 ms |
| middle of frame 15, going backwards | frame 0 | 17 | 56 ms |

- Decoding forward costs about 2.9 ms per 2560×1440 frame, so a trim-in that is not on a keyframe costs at most one GOP: under 0.2 s here, once.
- A position past the end fails with `MF_E_INVALID_POSITION` (0xC00D36E5), also for 30.0167 s on a 30.000 s clip. Positions must be clamped. After end of stream, a seek to 0 followed by `ReadSample` works.
- Crossing a cut: decode through it, or seek to the frame after it?

  | Cut | Decode through | Seek |
  |---|---|---|
  | 10 frames | 2 ms | 30 ms |
  | 30 frames, no keyframe inside | 25 ms | 92 ms |
  | 60 frames, one keyframe inside | 95 ms | 8 ms |
  | 120 frames | 251 ms | 9 ms |
  | 300 frames | 862 ms | 15 ms |

  A seek only wins when a keyframe lies inside the cut, because it restarts from the keyframe before the resume point. The spike seeks when the gap is longer than one second; the real exporter can do the same, or compare against the keyframe positions if it has them.
- The audio reader delivers 1024-sample blocks. After a seek the first block starts 1.3–20 ms **before** the target (64–960 samples), so the leading samples must be dropped by sample count. A seek takes 2–5 ms.

### What failed, and why

- Wrapping the decoder's NV12 output: it is a slice of a texture array bound for decoding only. Ask the reader for RGB32 instead.
- Seeking past the end: `MF_E_INVALID_POSITION`. Clamp.
- The recorder's low-latency encoder settings in an offline loop: correct output, but a half to a quarter of the speed.
- Without the device manager on the reader: software decode, 35–39 fps.

### Not measured

- Real Tiny Clips recordings. The test clips have a constant frame rate; recordings carry wall-clock timestamps and may have missing or irregular frames. `FrameCursor` picks the source frame covering each output instant, which is the right rule for that, but it was only exercised with constant-rate clips.
- HEVC, the `Microsoft AVC DX12 Encoder`, and GPUs other than this AMD one.
- Exports long enough to show thermal effects.

### Recommendation

Build the exporter as this loop: source readers with the device manager and advanced video processing, RGB32 out, wrapped directly; the shared renderer drawing into allocator textures; one sink writer with **low latency off and writer throttling on**. Sample the sources at the middle of each output frame so that export and paused preview pick the same frames. Seek the readers only across cuts that contain a keyframe, clamp positions to the duration, and trim audio by sample count after a seek. Expect 4–8 times real time at 2560×1440 on this class of GPU, and tag the output's colour matrix, primaries and transfer.

## 4. Two encoders while recording

**Answered, measured** with synthetic frames; real camera and screen capture were not part of it (see "Not measured"). `StudioEngineSpike encoders`, headless, 7.5 minutes (`out\encoders\report.txt`).

### What was built

Each stream is a sink writer set up exactly as Core's recorder sets its own up (copied from `MfSinkWriterEncoder`): H.264, low latency, writer throttling off, the shared D3D11 device through a device manager, input textures from the writer's sample allocator (pool of 4 growing to 15), bitrate 0.1 bit per pixel per frame (11.1 Mbit/s at 2560×1440, 6.2 at 1920×1080, 2.8 at 1280×720). Each stream has its own thread, paced at 30 fps by the recorder's frame pacer, which copies a prepared BGRA texture into an allocator texture and calls `WriteSample`. 30 s per scenario, two passes.

- Screen stream, 2560×1440: screen-like content (static panels and text-sized stripes, a moving window, a scrolling band).
- Camera stream: camera-like content with per-pixel noise that changes every frame, which is harder for an encoder than a real camera picture.
- Hardware streams use `AMDh264Encoder`. The software variant feeds system-memory frames to Microsoft's software H.264 encoder (Media Foundation puts a video processor in front of it).

A tick is "late" when it starts more than 5 ms after its slot. "Backlog" is frames given to the sink writer that its encoder has not finished, sampled every 100 ms.

### Real time, 30 s, 900 slots per stream

Pass 1 / pass 2:

| Scenario | Stream | Frames written | Dropped (no free texture) | Slots skipped | Late ticks | `WriteSample` mean, ms | `WriteSample` p99, ms | Largest backlog | Process CPU, cores |
|---|---|---|---|---|---|---|---|---|---|
| One encoder | screen 2560×1440 | 900 / 901 | 0 / 0 | 0 / 0 | 1 / 0 | 0.07 / 0.04 | 0.17 / 0.10 | 3 / 1 | 0.09 / 0.10 |
| + camera 1920×1080, hardware | screen | 901 / 901 | 0 / 0 | 1 / 0 | 0 / 1 | 0.03 / 0.03 | 0.20 / 0.08 | 1 / 2 | 0.19 / 0.17 |
| | camera | 902 / 902 | 0 / 0 | 1 / 0 | 2 / 1 | 0.04 / 0.03 | 0.16 / 0.11 | 1 / 2 | |
| + camera 1280×720, hardware | screen | 901 / 901 | 0 / 0 | 0 / 0 | 2 / 2 | 0.04 / 0.03 | 0.09 / 0.07 | 2 / 1 | 0.16 / 0.17 |
| | camera | 902 / 902 | 0 / 0 | 0 / 0 | 2 / 1 | 0.03 / 0.03 | 0.18 / 0.20 | 2 / 1 | |
| + camera 1920×1080, software | screen | 901 / 900 | 0 / 0 | 1 / 0 | 8 / 0 | 0.04 / 0.03 | 0.15 / 0.08 | 2 / 1 | 2.11 / 2.31 |
| | camera | 902 / 901 | 0 / 0 | 1 / 0 | 16 / 0 | 1.64 / 1.35 | 5.02 / 3.13 | 3 / 1 | |
| + camera 1280×720, software | screen | 902 / 901 | 0 / 0 | 0 / 0 | 4 / 0 | 0.04 / 0.04 | 0.10 / 0.38 | 3 / 1 | 1.00 / 1.42 |
| | camera | 903 / 901 | 0 / 0 | 0 / 0 | 3 / 0 | 0.76 / 0.90 | 2.29 / 2.05 | 2 / 1 | |

- **A second hardware encoder does not change the screen stream.** No frame was dropped in any scenario. `WriteSample` on a hardware stream returns in 0.03–0.07 ms with or without a second encoder (the encoder is asynchronous; the call only queues). The whole tick (take a texture, copy the frame, write) is 0.5–0.7 ms on average and 1.3–2.3 ms at the 99th percentile in the hardware scenarios. The encoders were never more than 3 frames behind.
- Late ticks are 0–2 of 900 with one or two hardware encoders. The single longest `WriteSample`, 29 ms, happened with one encoder.
- The four skipped slots are two stalls that each hit both threads at the same instant: 51 ms in the first 1920×1080 hardware pass and 39 ms in the first 1920×1080 software pass. Each left one 66.7 ms gap in the hardware-encoded files. Neither recurred in the second pass. With other agents building on the machine I cannot attribute them to the second encoder.
- Every hardware-encoded file has as many frames as were written, at 33.3 ms spacing apart from those gaps. The software path closed its one gap by itself: that file has 903 frames for 902 written and no gap, so the video processor in front of the software encoder repeats a frame for a missing slot. `Finalize` took 2–15 ms.
- The software encoder kept up too (it took 1.4–1.6 ms per 1920×1080 frame and 0.8–0.9 ms per 1280×720 frame), but it costs CPU: 2.1–2.3 cores at 1920×1080 and 1.0–1.4 at 1280×720, against 0.16–0.19 cores for two hardware encoders. In its first pass the 1920×1080 software scenario had 8 and 16 late ticks; in the second, none.

GPU load, from the `GPU Engine` performance counters of the process, averaged over each scenario (the two passes):

| Scenario | Video codec engine | 3D engine |
|---|---|---|
| One encoder, 2560×1440 | 8 % / 7 % | 1.5 % |
| + 1920×1080 hardware | 17 % / 11 % | 2–3 % |
| + 1280×720 hardware | 14 % / 9 % | 2–3 % |
| + 1920×1080 software | 12 % / 12 % | 2 % |
| + 1280×720 software | 11 % / 8 % | 2 % |

### Headroom

The same streams with nothing pacing them (a thread per stream feeds frames as fast as the allocator hands out textures, 10 s):

| Scenario | Screen 2560×1440 | Camera | Process CPU |
|---|---|---|---|
| One encoder | 177 fps (5.9× real time) | | 0.4 cores |
| + 1920×1080 hardware | 154 fps | 157 fps | 0.7 cores |
| + 1280×720 hardware | 156 fps | 199 fps | 0.9 cores |
| + 1920×1080 software | 140 fps | 369 fps | 7.9 cores |
| + 1280×720 software | 125 fps | 660 fps | 6.0 cores |

Two hardware encoders together still run more than five times faster than a 30 fps recording needs; the video codec engine was at about 45 % with one stream flat out and 65–85 % with two.

### What failed, and why

Nothing failed in the scenarios. One thing failed on the way: `IMFSinkWriter.GetStatistics` through Vortice returns `E_INVALIDARG`, because the wrapper leaves the structure's `cb` field at zero. The spike calls the method through the vtable with the size filled in (`SinkWriterEncoder.VideoStatistics`).

### Not measured

- A real camera. Frames from `MediaCapture` arrive as NV12 or YUY2, often in system memory, at the camera's own pace; converting and uploading them is extra work that this test does not include.
- Windows.Graphics.Capture running at the same time, and whatever the recorded application does with the GPU.
- Other GPUs. Some vendors limit the number of simultaneous hardware encode sessions; this AMD integrated GPU ran two without complaint. The fallback when the second hardware encoder cannot be created must stay in place.
- Recordings longer than 30 s (heat, power limits), and battery power.
- Picture quality of the camera stream at these bitrates.
- The system timer period was 1.0 ms throughout (another process had requested it). Behaviour at the default 15.6 ms was not measured.

### Recommendation

Record the camera with a **second hardware H.264 sink writer on the shared device, at the camera's resolution up to 1920×1080 and 30 fps**, with the same settings as the screen stream. Measured cost: none on the screen stream's timing, 0.1 more CPU cores, and 4–9 points more of the video codec engine. Keep each stream on its own paced thread and its own allocator pool, as the recorder does today. Use the software encoder only as the fallback when a second hardware encoder is not available, and then prefer 1280×720: it costs about one core instead of two.

## 5. NativeAOT

**Answered, measured.** The spike was published with `dotnet publish -c Release -r win-x64 -p:PublishAot=true` and every mode was run from the published executable, including the window (`out\*-aot\report.txt`; `info` prints `runtime: NativeAOT`).

### Result

**Nothing in the engine code breaks under NativeAOT or trimming.** No code change, `rd.xml`, trimmer root or `DynamicDependency` was needed. Ordinary builds with `IsAotCompatible` give 0 analyzer warnings in the spike's own code.

| Mode from the AOT executable | Outcome |
|---|---|
| `media`, `info` | pass; `info` output identical to the JIT build apart from the runtime line |
| `preview` (all groups, shorter counts) | works as under JIT: 360 of 360 frames on both clips in each 1x run, 300 of 300 seeks through the seek policy (6 repaired), 300 of 300 steps, 120 of 120 redraws while paused. The same three `MediaPlayer` findings show up as "checks that did not hold": the position lost after an offset change (twice) and one lost seek |
| `export` | every check passed: 900 of 900 frames exact at three sizes, 180 of 180 with trim and cut, audio round trip 0.00 ms |
| `encoders` | no dropped frames in any scenario; headroom 173 fps alone, 141 + 142 fps with a 1920×1080 hardware camera stream |
| `present` | all checks passed: both panels pixel-exact, both follow resize and scale changes, both keep presenting with the UI thread blocked. The window was again covered by the other application's full-screen window, so no DXGI frame statistics |

The two things the question singles out:

- **Swap chain panel interop.** `ISwapChainPanelNative` is declared with `[GeneratedComInterface]`; the panel's pointer comes from `((IWinRTObject)panel).NativeObject.ThisPtr`, is queried for the IID and wrapped with `ComInterfaceMarshaller<T>.ConvertToManaged`. It works under AOT without warnings. So do the same pattern for `ICanvasResourceWrapperNative` (Win2D) and `IGraphicsCaptureItemInterop` (window capture).
- **Win2D 1.4.0.** `CanvasSwapChainPanel` created from XAML markup, `CanvasDevice.CreateFromDirect3D11Device`, `CanvasSwapChain` (create, resize, drawing session, present) and `CanvasBitmap.CreateFromDirect3D11Surface` all work under AOT, with no trim warnings from Win2D.

Also exercised without trouble: WinRT events with generic delegates (`MediaPlayer.VideoFrameAvailable`, `MediaPlaybackSession.SeekCompleted`, `MediaTimelineController` state events, `SwapChainPanel.CompositionScaleChanged`, `LayoutUpdated`), `DispatcherQueue.TryEnqueue`, `Application.Start` from a hand-written `Main`, Vortice Direct3D11/Direct2D/DXGI/Media Foundation, and `LibraryImport` P/Invokes.

### Warnings

The AOT compiler reports trim-analysis warnings for one assembly only, `SharpGen.Runtime` (the COM layer under Vortice): by default a single `IL2104` for the assembly, and with `-p:TrimmerSingleWarn=false` the two behind it:

- `IL2067` in `SharpGen.Runtime.TypeDataStorage.GetTargetVtbl`
- `IL2072` in `SharpGen.Runtime.TypeDataStorage.RegisterFromReflection`

Both concern reflection over a type's fields and properties when SharpGen builds a vtable for a **managed object that implements a COM callback interface**. The spike implements no such callback, and nothing failed at run time. The real engine should keep it that way where it can (synchronous source readers, as here) or test any managed COM callback it adds under AOT; Core already ships Vortice under AOT, so this is not new.

### What did go wrong (build and publish, not AOT)

1. **The link step failed on this machine**: `'vswhere.exe' is not recognized`, then `MSB3073 ... link.exe ... exited with code 123`. The Visual Studio Build Tools environment script calls `vswhere.exe` by name, it is not on `PATH`, and the error text ends up where the linker's folder should be. Prepending `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` for that shell fixed it. The compile itself had succeeded.
2. **The published window did not start**: `XamlParseException` 0x802B000A, "XAML parsing failed", at the window's `LoadComponent`. `dotnet publish` had left the app's own `StudioEngineSpike.pri`, which holds the compiled XAML, out of the publish folder. A publish **without** AOT failed the same way, so this is about publishing an unpackaged WinUI 3 app, not about AOT. The project had `EnableMsixTooling` set to false; with it set to true the `.pri` is published and the window runs. Tiny Clips itself is packaged and should not meet this, but any unpackaged tool or test host will.

### AOT against JIT

| | JIT | NativeAOT |
|---|---|---|
| Start to exit of `help` (no device) | 85–104 ms | 35–47 ms |
| Start to exit of `info` (device, Media Foundation enumeration) | 560–880 ms | 430–680 ms |
| Both players opened | 386 ms (551 in another run) | 323 ms |
| Attach a swap chain to its panel, (a) / (b) | 9.9 / 2.5 ms | 3.2 / 0.7 ms |
| Composite 1920×1080 / 2560×1440, cached shadows | 0.50 / 0.52 ms | 0.36 / 0.56 ms |
| One window frame on demand, (a) / (b) | 1.18 / 0.99 ms | 1.86 / 1.02 ms |
| Export 2560×1440, recorder settings / offline settings | 62–120 / 131–238 fps | 135 / 164 fps (one run each) |
| Two hardware encoders flat out, screen + 1920×1080 | 154 + 157 fps | 141 + 142 fps |
| Output folder | 94 MB, exe 0.3 MB plus the runtime | 62 MB plus a 35 MB `.pdb`, exe 8.0 MB |

Steady-state numbers are the same within the spread of this machine; what AOT changes is start-up and first use (the JIT attach times include compiling the code). The AOT compile and link took 20–36 s.

### Not measured

- `win-arm64`: not published or run.
- A packaged (MSIX) AOT build, which is how the real app ships.
- DXGI frame statistics under AOT (window covered, as in question 2).
- The AOT `preview` run used shorter counts than the JIT run (300 seeks instead of 800, 20 s of drag instead of 60).

### Recommendation

Build the engine as it is designed, with no AOT-specific accommodation: `[GeneratedComInterface]` for the three small COM interfaces, `partial` XAML classes, Vortice as today. Keep managed COM callbacks out of the engine unless they are tested under AOT. For unpackaged tools around it, set `EnableMsixTooling` to true.

## Recommended engine design

Everything on **one D3D11 device** (the same one the recorder already shares), with **one Direct2D scene renderer** for every consumer. In the spike the same `SceneRenderer` drew the headless preview, both window presenters and the export, and the export showed the expected frame numbers on every frame.

### Preview decode

- One `MediaPlayer` per clip in frame-server mode (`IsVideoFrameServerEnabled`, `CommandManager.IsEnabled = false`), all on one `MediaTimelineController`. A clip that starts late gets a **negative** `TimelineControllerPositionOffset`.
- Each `VideoFrameAvailable` does `CopyFrameToVideoSurface` into that clip's BGRA texture and wakes the render thread. Nothing else happens in the callback. The texture is no larger than the clip appears in the preview, in physical pixels; when paused and zoomed, pull the frame again at full size.
- The render thread composites when a texture has a newer frame than it last drew, without waiting for the other clip. The frame number of a texture is `floor(PlaybackSession.Position × fps)` read in the callback.
- All paused position changes (seek, scrub, step back, snap) go through a seek policy like `Engine\SeekCoordinator.cs`: one seek in flight, newest request wins, always the middle of a frame, a seek is lost when `SeekCompleted` has been raised and no frame callback has started 40 ms later, a lost seek is repaired by seeking to the neighbouring frame and back.
- After `Pause()`: snap both players to the frame the screen shows. Step forward: `StepForwardOneFrame` on every player, then set `Controller.Position` to the middle of the new frame. Step back: a seek.
- The renderer decides from timeline time whether a clip is visible; a player outside its range just parks on its first or last frame. `Controller.Duration` is set to the project length.
- Playing across a cut is a seek during playback. Measured cost: both clips are back on matching frames after 102 ms on average (p95 207 ms). The design should either accept that hitch at cuts or keep it short some other way; the spike did not try alternatives.

Expect at 1x: every frame delivered, the two clips a frame apart for about 0.3–1.9 % of the time on screen, 20–30 % of one core.

### Presentation

**(b)**: a plain `SwapChainPanel` with a DXGI composition swap chain (BGRA, 2 buffers, flip-sequential) attached through `ISwapChainPanelNative`, the renderer drawing straight into the back buffer. Present from the render thread only when there is something new: a frame, a layout change, a resize. Buffers are the panel's DIP size times `CompositionScaleX`, with the inverse scale as `MatrixTransform`; resize them on the render thread on `SizeChanged` and `CompositionScaleChanged`. XAML overlays (handles, labels) go on top of the panel as ordinary children.

### Export

One loop on a worker thread:

1. Source readers with `MF_SOURCE_READER_D3D_MANAGER` and advanced video processing, output RGB32; their textures are wrapped as Direct2D bitmaps without a copy.
2. For each output frame, take the source frame that covers the **middle** of the frame (the same instant the paused preview uses), draw the scene into a texture from the sink writer's sample allocator, `WriteSample`.
3. Audio: decode to PCM, trim by sample count, write to the sink writer's AAC stream a little ahead of the video.
4. Sink writer with **low latency off and writer throttling on**.
5. Seek a reader only across a cut that contains a keyframe (the spike's rule: a gap of more than one second); otherwise decode through. Clamp positions to the duration.

Expect 4–8 times real time at 2560×1440 on this class of GPU, frame-exact output, and audio that is not shifted (0.00 ms measured, for a source written by Media Foundation).

### Camera encode while recording

A second hardware H.264 sink writer on the shared device: the camera's resolution up to **1920×1080, 30 fps**, same settings as the screen stream (low latency, throttling off, allocator-owned textures), on its own paced thread. Software encoding at 1280×720 as the fallback.

One suggestion from question 1 rather than question 4: paused seeks in the preview cost about 2 ms per 2560×1440 frame between the previous keyframe and the target (11 ms on a keyframe, 128 ms at the far end of a 2 s GOP). Recording Studio sources with a keyframe every second instead of every two would halve the worst case. The bitrate cost of that was not measured.

### Pitfalls the real implementation must avoid

Preview:

1. A paused seek sometimes delivers no frame at all (0–2.7 % per batch). Without the seek policy the preview shows a stale frame with the right timecode.
2. Seeking to a frame's start time lands on the previous frame for most frames and on the right frame for some. Always seek to the middle.
3. `TimelineControllerPositionOffset` is added, not subtracted; and a `Controller.Position` assigned right after changing it is lost on that player.
4. `StepBackwardOneFrame` steps two frames a quarter of the time. `StepForwardOneFrame` leaves the controller's clock behind, and playback then stalls until the clock catches up.
5. After a bare `Pause()` the picture can be one frame behind the clock, and after fast playback the clips can be a frame apart.
6. Any GPU readback or other slow work inside `VideoFrameAvailable` drops frames at 2x; full-size copy targets alone make 2x marginal.
7. Before a clip's first frame its player shows frame 0 and says nothing. The controller runs on past the end unless `Duration` is set.

Rendering and presenting:

8. The immediate context is shared by `MediaPlayer` copies, Direct2D, readbacks and the swap chain. D3D11's multithread protection makes single calls safe, not Direct2D's sequences of them; the spike serializes them with one lock (`GraphicsDevice.Gate`). Hold it only while drawing, never across `Present`.
9. Never present in a loop: with sync interval 1 it runs 1–5 frames ahead of the screen. Present on change.
10. `ResizeBuffers` needs every reference to the back buffer released first, including the renderer's cached Direct2D target, and it waits for queued presents (tens of milliseconds seen). Do not call it on the UI thread.
11. A swap chain panel on a fractional pixel is blurred. Keep layout rounding on and avoid arbitrary transforms above the preview.
12. DXGI frame statistics read zero, with a success code, while another full-screen window covers the app.

Export and encoding:

13. The recorder's low-latency encoder settings make an offline export slower, by a factor of 1.3 to 3.8 in the runs here. Same bytes out.
14. The decoder's own NV12 output is a texture array bound for decoding; Direct2D cannot wrap it. Ask the reader for RGB32 with the device manager set. Without the device manager decoding falls back to software at 1.3 times real time.
15. Do not compare sample times with `frame × 10⁷ / fps` for equality: RGB32 output rounds them, NV12 truncates.
16. `SetCurrentPosition` past the end fails with `MF_E_INVALID_POSITION`. After an audio seek the first block starts up to 20 ms before the target.
17. Audio ends 16 ms after video (AAC padding), and an MP4 written by ffmpeg reads 21 ms late through Media Foundation. Neither is an export bug.
18. The output is not tagged with a colour matrix, primaries or transfer function unless the exporter sets them.
19. `IMFSinkWriter.GetStatistics` through Vortice 3.8.3 fails with `E_INVALIDARG` (`cb` not set). Call it through the vtable.

Build and capture:

20. A XAML application object that is not `App.xaml` in the project root must be declared as `ApplicationDefinition`. Without it the build succeeds silently and the window dies at start with `XamlParseException` 0x802B000A.
21. Windows.Graphics.Capture delivered a frame only every other refresh of this 100 Hz display (20 ms apart) until `MinUpdateInterval` was lowered.

## What was measured and what was not

| Question | Measured | Not measured, and why |
|---|---|---|
| 1. Preview | Composite time; drops and sync at 0.5x, 1x, 2x; pause; paused seek accuracy, latency and loss rate; steps both ways by three methods; seek during playback; re-render while paused; offsets and edges; half-size copy targets. All read from pixels, headless. | Audio: the players were muted, so audio output through the same players under a `MediaTimelineController` was not exercised. Real recordings (wall-clock timestamps, irregular frames). A display other than 100 Hz. More than two clips. |
| 2. Presenting | Both paths in a real WinUI 3 window: pixel-exactness, cost, pacing and on-screen sync from screenshots, UI-thread independence, resize (settled and continuous), simulated scale change, code size. | A real monitor DPI change and a second monitor (one monitor; changing its scale would disturb others using the machine). DXGI frame statistics in the final run (the window was covered by another application's full-screen window; quoted from earlier runs). |
| 3. Export | Speed at three sizes and four encoder settings; direct wrapping of decoder output; frame accuracy on every output frame; duration; audio/video alignment to 0.01 ms; reader seeking, cuts, trim-in off a keyframe. | Real recordings; HEVC; the DX12 encoder; other GPUs; long exports. Why low latency is slower was not established, only that it is. |
| 4. Two encoders | One against two real-time encoders at 1920×1080 and 1280×720, hardware and software: drops, late ticks, write times, backlog, CPU, GPU load, and headroom when unpaced. | A real camera and real screen capture feeding them; other GPUs and their session limits; recordings longer than 30 s; picture quality. |
| 5. NativeAOT | Publish with `PublishAot`, all trim warnings, every mode run from the AOT executable including the window with both presenters, start-up and steady-state numbers against JIT. | `win-arm64`; a packaged AOT build; DXGI frame statistics (window covered). |

All timings were taken on one machine shared with other agents running builds and tests. Where repeats disagreed, the report gives the range and says so; the largest unexplained spread is export speed with the same settings (131 to 238 fps).
