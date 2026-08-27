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
- **Screenshot** (PNG/JPEG, scale, quality) — full screen, a specific window, or a drag-selected **region**.
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
```

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

For how the screen, webcam, microphone, and system audio are kept in sync (shared timeline,
WASAPI capture, drift/discontinuity correction, audio back-pressure, the *Audio offset* setting, and
the end-of-recording sync report), see [`docs/audio-video-sync.md`](docs/audio-video-sync.md).

For the experimental **GPU recording pipeline** (zero-copy WGC → Direct2D overlays → hardware
encoder), the per-recording performance report, and the `RecordingBenchmark` harness with measured
CPU-vs-GPU numbers, see [`docs/gpu-recording-pipeline.md`](docs/gpu-recording-pipeline.md).

## CI

`.github/workflows/windows-build.yml` builds `x64` + `ARM64` and runs the Core tests on
`windows-latest`. It is path-filtered to `windows/**`, so it only runs when Windows code changes.

## Accessibility release gate

Before shipping a Windows release, complete the manual [accessibility release-gate matrix](docs/accessibility-release-gate.md)
against the packaged candidate. It covers keyboard-only and Narrator behavior for the tray,
settings, capture flows, indicators, editor, trimmers, onboarding, and guide. A build or static
automation review does not replace the documented hands-on results.

## Distribution

- **Direct (available now):** install with `winget install Refractored.TinyClips`, or use the
  architecture-specific `.appinstaller` from a Windows GitHub Release as a stable bootstrap for the
  current version. Both routes install the same MSIX, which checks for signed updates in the
  background and on launch. winget also installs the required .NET 10 Desktop and Windows App SDK
  runtimes; standalone App Installer use requires the
  [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) on a clean machine.
  `winget upgrade` and App Installer can both update the same package family. Fully free.
- **Microsoft Store:** Store-managed updates only. Store packages do not include the direct
  `.appinstaller` update channel. Feature set matches Direct; no Windows Pro tier.

See the plan for the full phased roadmap, packaging, and signing details.

For the Microsoft Store listing copy, screenshot plan, compliance answers, and protected CI/CD
setup, see [docs/microsoft-store-listing.md](docs/microsoft-store-listing.md).
