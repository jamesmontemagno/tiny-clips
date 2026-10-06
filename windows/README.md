# Tiny Clips for Windows

A native **WinUI 3 / Windows App SDK** port of Tiny Clips — a tray-based screen-capture app
(screenshots, video, GIF). See the full design in
[`/plans/windows-winui3-port-plan.md`](../plans/windows-winui3-port-plan.md).

> Status: **Phases 1–3 substantially complete** — tray app with the region/screen/window
> capture picker, screenshot, MP4 video + animated GIF recording, post-capture screenshot editor
> and video/GIF trimmers, an on-screen recording indicator, global hotkeys, pre-capture countdown
> with a region outline, save toasts, launch-at-login, a full native Settings window, first-run
> onboarding, and a Guide. Mouse-click visual overlays, the branding overlay, and microphone +
> system-audio capture are implemented. Packaging/Store work is deferred. See the plan for the
> full roadmap.

## Features

- **Capture picker** — choose **Region**, **Screen**, or **Window** (R / S / W) before any capture,
  mirroring the macOS picker. Video/GIF captures then show a pre-record setup panel before countdown.
- **Screenshot** (PNG/JPEG/WebP, scale, JPEG/WebP quality) — full screen, a specific window, or a drag-selected **region**.
  The region selector shows a live snapshot of the screen and dims only outside the selection.
- **Scrolling capture** — pick **Scroll** (P) in the Screenshot picker, select a region, then scroll
  the page; a floating panel shows the frame count with **Done** (Enter) to stitch everything into
  one tall image and **Cancel** (Esc) to discard. Sticky headers/footers are suppressed and memory
  use is bounded (the capture stops and saves automatically when a limit is reached). Enter/Esc
  apply while the panel has focus; use its buttons after clicking into the page.
- **Video recording** → hardware-accelerated **H.264 MP4** (configurable frame rate, audio sources,
  microphone device, mouse-click visuals, and time limit before each recording).
- **GIF recording** → animated GIF (frame rate, max-width downscale, infinite loop).
- **Recording indicator** — a floating always-on-top panel shows the elapsed time and a Stop button
  (with the stop hotkey) while recording.
- **Editor & trimmers** — an optional post-capture **screenshot editor** (crop, copy, save / save-a-copy),
  a **video trimmer**, and a **GIF trimmer**, each openable automatically after capture.
- **Tiny Clips Studio (early preview, off by default)** — records the screen and the camera as
  separate layers and opens a compositing editor when the recording ends: background and padding,
  a rounded screen card, camera shape and four layouts, a draggable camera bubble, scenes that
  change the layout partway through, zooms (added by hand or suggested from your clicks), crops
  for the screen and the camera, cuts, speed changes, trim, and MP4 export.
  The project stays editable afterward. See [Tiny Clips Studio](#tiny-clips-studio-preview).
- **Region outline** — a red outline frames the selected region during the countdown.
- **Onboarding & Guide** — a first-run welcome wizard and an in-app help reference.
- **Clips Library** — browse every saved capture from the tray. Collapsible sidebar with
  **Smart Collections** (All, Recent, This Week, This Month, Large Files, Favorites, Screenshots,
  Videos, GIFs), your **Collections**, and **Tags**; search across names, tags and notes; a
  **Sort & Filter** flyout (type, date, five sort orders); grid or list view; a details pane with
  inline video/GIF preview and an editor for **name, tags, notes, collection**; favorites; multi-select
  with batch favorite/tag/copy/share/delete; Windows **Share** and drag-out to other apps; rename,
  archive, Uploadcare upload/link; live updates via a folder watcher; thumbnails cached on disk.
  Keyboard: `Ctrl+F` search, `Ctrl+A` select all, `Enter` open, `F2` rename, `Delete`, `Ctrl+C`,
  `Ctrl+Shift+G`/`L` grid/list, `Ctrl+Shift+D` details, `Ctrl+Shift+E` select mode, `Ctrl+R` refresh.
  Preferences live in **Settings → Clips Library** (defaults, density, quick actions, confirm delete,
  auto-refresh, archive-old-clips). Metadata is stored in `clip-metadata.json` in the app's local
  data folder; the files themselves are never modified.
- **Global hotkeys** — Screenshot `Ctrl+Shift+5`, Video `Ctrl+Shift+6`, GIF `Ctrl+Shift+7`,
  Stop recording `Ctrl+Shift+S`.
- **Launch at login** — optionally start TinyClips when you sign in to Windows.
- **Pre-capture countdown** and **save toast notifications** (both opt-in via Settings).
- **System-tray** Fluent menu (rounded/acrylic), light/dark/system theming, full **Settings** window.

## Requirements

- Windows 11 **21H2 (build 22000)** or later
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- Windows SDK `10.0.26100`
- **Developer Mode** enabled (Settings → System → For developers) for MSIX sideload/registration
- Optional: [Windows App Development CLI (`winapp`)](https://learn.microsoft.com/windows/apps/dev-tools/winapp-cli/)
  for identity, manifest, signing, and MSIX packaging:
  ```powershell
  winget install Microsoft.winappcli --source winget
  ```

## Project layout

```
windows/
  TinyClips.Windows.slnx        Solution
  Directory.Build.props         Shared TFM / min-version / platforms (x64, ARM64)
  src/
    TinyClips.App/              WinUI 3 packaged app (tray, DI bootstrap, windows)
    TinyClips.Core/             UI-free domain (services, models) — capture pipeline lands here
  tests/
    TinyClips.Core.Tests/       xUnit tests
    ui/                         winapp ui automation scripts (run against a live app by PID)
  tools/
    RecordingBenchmark/         Headless CPU-vs-GPU recording benchmark (manual; see docs)
    StudioRenderCheck/          Headless check of the Studio renderer, exporter and camera recorder
    StudioPreviewCheck/         Check of the Studio live preview engine and its panel
    StudioWindowCheck/          Check of the Studio editor window, opened in a process of its own
    Collect-Diagnostics.ps1     Zips logs, system details and UI-stall timings from any machine
  packaging/
    msix/  winget/              Packaging artifacts (later phases)
  spikes/                       Throwaway de-risking prototypes (not in the solution/CI)
```

## Build & run

WinUI 3 requires an explicit platform (`x64` or `ARM64`; `AnyCPU` is not supported).

```powershell
# Restore
dotnet restore windows/TinyClips.Windows.slnx

# Build the app
dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64

# Run with package identity (uses the winapp CLI under the hood)
dotnet run --project windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64

# Test
dotnet test windows/tests/TinyClips.Core.Tests/TinyClips.Core.Tests.csproj -c Debug
dotnet test windows/tests/TinyClips.App.Tests/TinyClips.App.Tests.csproj -c Debug -p:Platform=x64
```

The App tests compile the shipping Settings view model with fake services and a controllable
transcript-save scheduler, without launching XAML or querying devices, credentials, or real
transcripts. They cover lazy lookup counts, initial TwoWay-binding write-backs, overlapping
realization, edits/imports/reset, reopening, and late results after closure.

Settings restores persisted scalar preferences at construction and repairs only the newly
realized section after its first layout pass (or the rapid-navigation dispatcher fallback).
Realization suppression is reference-counted per section; already-loaded sections can still
persist edits while another section is realizing. Uploadcare credential status and teleprompter
text are loaded only on their relevant section's first realization and cached for that window.
Save/clear/import/reset update those caches without rereading external state; a pending
transcript edit takes precedence over older persisted text and is flushed on close. Reopening
Settings creates fresh caches. Analytics and media-device initialization remain lazy.
Settings uses the strict large-text editing read: an inaccessible transcript produces an inline
error and disables its text editor rather than caching a misleading empty value. Existing
non-editing readers retain their fallback behavior.

### Pending native Settings responsiveness validation

The automated Settings tests establish lookup counts and persistence correctness, not
navigation responsiveness. The native ARM64 and native x64 comparison required by #405 remains
unperformed; neither the tests nor a cross-compiled build satisfies that acceptance criterion.
No navigation timing improvement is claimed.

Before marking hardware validation complete, compare the baseline and this change on a native
ARM64 device and a native x64 device, using the same build configuration and synthetic settings
on each device. Do not use x64 emulation as the ARM64 result or profile concurrently on a shared
host. Keep credentials, transcripts, settings, traces, and local measurements private.

For each build/device pair, repeat a fixed navigation sequence through General, Screenshot,
GIF, Uploadcare, and Teleprompter. Record first realization and cached revisits separately,
including selection-to-first-layout latency and UI-thread stalls, using identical synthetic
transcript sizes and credential fixtures. Also exercise rapid navigation, pending transcript
edits, and closing during delayed initialization. Confirm unrelated sections perform zero
credential/transcript lookups and relevant sections perform one initial lookup without
repeated reads on revisits. Preserve keyboard access and light/dark/system theme behavior.
Record the before/after comparison privately with the build revisions, native architectures,
fixture sizes, repetition count, and measurement method; do not infer responsiveness from
service-call counts alone.

To build the **Microsoft Store** flavor (same feature set, Store distribution behavior), set:

```powershell
dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64 -p:TinyClipsStoreBuild=true
```

The app launches **tray-only** (no window). Left- or right-click the tray icon for the Fluent
menu: **Screenshot**, **Capture Region**, **Record Video**, **Record GIF**, **Settings**,
**Guide**, **Exit**. Capture items first show the **Region / Screen / Window** picker.
Video/GIF captures then show a setup panel before countdown and recording. Recording items toggle
to **Stop Recording** (also `Ctrl+Shift+S`) while active, and a floating recording indicator shows
the elapsed time. Global hotkeys work app-wide.

For coordinate/DPI behaviour across mixed-DPI monitors, see
[`docs/dpi-and-coordinates.md`](docs/dpi-and-coordinates.md).

For opt-in window-construction phase diagnostics and the private cold/warm validation
protocol, see [`docs/first-open-responsiveness.md`](docs/first-open-responsiveness.md).

For how the screen, webcam, microphone, and system audio are kept in sync (shared timeline,
WASAPI capture, drift/discontinuity correction, audio back-pressure, the *Audio offset* setting, and
the end-of-recording sync report), see [`docs/audio-video-sync.md`](docs/audio-video-sync.md).

For the experimental **GPU recording pipeline** (zero-copy WGC → Direct2D overlays → hardware
encoder), the per-recording performance report, and the `RecordingBenchmark` harness with measured
CPU-vs-GPU numbers, see [`docs/gpu-recording-pipeline.md`](docs/gpu-recording-pipeline.md).

For schema-2 timing/cadence/submission definitions, requested versus actual backends, unverified
encoder selection, and the local collector's target-process architecture evidence and required
helper source, see the [diagnostic contract](docs/gpu-recording-pipeline.md#41-diagnostic-contract-schema-2).
Historical benchmark values are not verified encoder/output-frame measurements.

## Tiny Clips Studio (preview)

Studio is off until you switch it on: **Settings › General › Tiny Clips Studio (Preview)**. With
the switch off nothing else of Studio is shown, and a recording is made as it always was.
Switching it off again deletes nothing: the projects stay where they are, are not cleaned up while
it is off, and the line under the switch says how many there are and how much room they take.

With the switch on:

- **Settings › Video** offers **Open in Studio (Preview)** under **After recording**, and the
  recording setup panel has **Record for Studio**. A Studio recording is saved as a project (a
  clean screen track, a camera track, and click and cursor data) and opens in the editor.
- **The editor** has a live preview, an inspector (scene, layout, background, padding, screen and
  camera styling with crops, the zooms, the cuts, and the speed changes), a scene lane, a zoom
  lane, a cut lane, and a speed lane above a trim bar, undo and redo, and Export. A recording
  without a camera has no scenes.
  Keys: `Space` play or pause, `Left`/`Right` step a frame, `I`/`O` start and end the video at the
  playhead, `S` split the scene at the playhead, `Z` add a zoom at the playhead, `X` start a cut
  at the playhead, `R` play the two seconds from the playhead twice as fast, `Delete` remove the
  selected zoom, cut, or speed change, `1`–`4` layout of the scene the playhead is in,
  `Ctrl+Z`/`Ctrl+Y` undo and redo, `Ctrl+E` export, `Esc` stop an export. While a lane has the
  keyboard focus, `Left`/`Right` go to the previous and the next scene, zoom, cut, or speed
  change on it, and `Home`/`End` to the first and the last; on the scene lane, `Delete` removes
  the scene the playhead is in. While something is being dragged, the keys that change the
  project do nothing; `Space` and the arrow keys still work.
  The Camera section has a **Background** choice: keep, blur, or remove what is behind you. The
  people are found on this PC with the MediaPipe Selfie Segmentation model, which ships with the
  app under the Apache License 2.0 (**Settings › About › Third-party notices**).
  **Keep this project**, at the end of the inspector, pins a project against the storage
  cleanup; it is written at once and is not undone by `Ctrl+Z`.
  A project the editor cannot show says why, and where its screen recording is still there,
  **Save the screen recording** saves that as an ordinary video.
- **Clips Library** offers **Open in Studio…** for a video that was exported from a project, and
  choosing such a video in **Recent captures** opens its project instead of the trimmer.
  **Settings › General** shows the space projects take, the cleanup rules, and the drafts: the
  recordings that only their project holds. Those are the ones kept without exporting, the ones
  whose exported video is no longer where it was saved, and any project Studio cannot read. Each
  can be deleted, and has **Save recording** to save its screen recording as an ordinary video.
  Cleanup never removes them, and the storage limit counts only what cleanup may remove.

Projects are kept in the app's local data folder under `TinyClips\Projects`. The format, layout
math and drawing rules are in [`/docs/studio-project-format.md`](../docs/studio-project-format.md),
shared with the macOS app; the design and its status are in
[`/plans/video-studio-plan.md`](../plans/video-studio-plan.md). The renderer and exporter are
described in [`docs/studio-rendering.md`](docs/studio-rendering.md) and the live preview in
[`docs/studio-preview.md`](docs/studio-preview.md). Each has a check tool that runs without the
app, plays no sound and sends no input, and so has the editor window. None runs in CI, and the
preview and window tools are not in the solution.

```powershell
# Renderer, exporter and camera recorder. No window; about four minutes.
dotnet run --project windows/tools/StudioRenderCheck/StudioRenderCheck.csproj -c Release -p:Platform=x64

# Live preview. Needs ffmpeg and ffprobe on PATH and a desktop session; about ten minutes.
# Its window stays behind every other window. Options are in the tool's README.
dotnet build windows/tools/StudioPreviewCheck/StudioPreviewCheck.csproj -c Debug -p:Platform=x64
windows\tools\StudioPreviewCheck\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\StudioPreviewCheck.exe

# Editor window: the real window on real projects, with the real preview and exporter. Needs
# ffmpeg and ffprobe on PATH and a desktop session; about three and a half minutes. Its windows
# stay behind every other window and cannot take the keyboard focus. What it checks is in the
# tool's README.
dotnet build windows/tools/StudioWindowCheck/StudioWindowCheck.csproj -c Debug -p:Platform=x64
windows\tools\StudioWindowCheck\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\StudioWindowCheck.exe
```

## CI

`.github/workflows/windows-build.yml` builds `x64` + `ARM64` and runs the Core and Settings
view-model tests on `windows-latest`. It is path-filtered to `windows/**`, so it only runs when
Windows code changes.

## Accessibility release gate

Before shipping a Windows release, complete the manual [accessibility release-gate matrix](docs/accessibility-release-gate.md)
against the packaged candidate. It covers keyboard-only and Narrator behavior for the tray,
settings, capture flows, indicators, editor, trimmers, onboarding, and guide. A build or static
automation review does not replace the documented hands-on results.

## Distribution

- **Direct (available now):** install with `winget install Refractored.TinyClips`, or use the
  architecture-specific `.appinstaller` from a Windows GitHub Release as a stable bootstrap for the
  current version. Both routes install the same signed x64/ARM64 MSIX, which bundles the .NET
  runtime and Windows App SDK runtime. Clean Windows 11 machines need no separate runtime
  installation. Installs made through the `.appinstaller` check for signed updates in the
  background and on launch; winget installs update with `winget upgrade`. Both operate on the same
  package family. Fully free.
- **Microsoft Store:** Store-managed updates only. Store packages do not include the direct
  `.appinstaller` update channel. Feature set matches Direct; no Windows Pro tier.

See the plan for the full phased roadmap, packaging, and signing details.

For the Microsoft Store listing copy, screenshot plan, compliance answers, and protected CI/CD
setup, see [docs/microsoft-store-listing.md](docs/microsoft-store-listing.md).
