# Changelog — Tiny Clips for Windows

All notable changes to the Windows (WinUI 3) port are documented here. The macOS app has its
own `CHANGELOG.md` at the repository root.

## [Unreleased]

### Changed
- **Native MSIX auto-updates for direct installs.** Windows GitHub Releases now publish x64 and
  ARM64 `.appinstaller` files and embed the matching configuration in direct MSIX packages. A
  dedicated `windows-latest` GitHub Release provides stable update metadata URLs, and Windows checks
  on launch when about 24 hours have passed. Microsoft Store packages remain on Store-managed
  updates and do not include the direct update channel.
- **Clips Library rewritten** as a proper MVVM feature (`ViewModels/ClipsLibrary`,
  `Views/ClipsLibrary`, `Controls/ClipsLibrary`) with macOS Clips Manager parity. New: collapsible
  sidebar with Smart Collections / Collections / Tags and live counts; search across names, tags and
  notes with suggestions; Sort & Filter menu (five sort orders, type incl. Favorites, date); grid or
  list view with compact density; right-hand details pane with inline `MediaPlayerElement` video
  preview, animated GIF preview, file facts, and an editor for display name, tags, notes and
  collection; favorites; multi-select with a batch action bar (select all, favorite, tag, copy, share,
  delete with a count-aware confirmation); a shared context menu on cards, rows and the details pane;
  Windows Share contract and drag-out of clips to other apps; rename (display name or file on disk);
  move to Archive; Uploadcare upload with the link persisted on the clip; contextual empty states
  (loading, no clips, no match, folder missing); keyboard accelerators (`Ctrl+F`, `Ctrl+A`, `Enter`,
  `F2`, `Delete`, `Ctrl+C`, `Ctrl+Shift+G/L/D/E`, `Ctrl+R`, `Esc`); AutomationIds/names on every
  control.
- **Library stays current automatically.** A `FileSystemWatcher` on the capture folders (debounced)
  refreshes the library as captures are saved, renamed or deleted; an optional auto-refresh timer
  remains available. Thumbnails are generated once off the UI thread and cached as JPEG in the app's
  local data folder, keyed by path + modified time + size, and pruned when clips disappear.
- **Clip metadata store.** Favorites, names, tags, notes, collections and upload links are kept in
  `clip-metadata.json` (`TinyClips.Core.Services.ClipsLibrary.JsonClipMetadataStore`) with debounced
  atomic writes; records follow renames/archives and are pruned when files are gone. Auto-upload after
  save now records the Uploadcare link with the clip.
- **Settings → Clips Library** section: remember last state or choose default view/sort/filters,
  notes preview, quick actions, compact list, upload status, confirm delete, only-Tiny-Clips files,
  auto-refresh interval, archive-old-clips after N days, and reset. Legacy `clipsManager*` keys are
  migrated once.
- `IClipLibraryService` gains `GetClipsAsync(onlyTinyClipsFiles)`, `GetLibraryDirectories()` and
  `Rename(...)`; new Core services `ClipQueryEngine`, `ClipArchiveService`, `ClipLibraryWatcher`,
  `ClipsLibrarySettings`, `IClipFileOperations`, all covered by unit tests (29 new).
- Removed the old code-behind `ClipsManagerWindow`.

### Added
- `tests/ui/ClipsLibrary.Tests.ps1` — `winapp ui` automation smoke test for the Library window
  (shell, details editing, favorites, sidebar filters, search/empty state, filter flyout, view modes,
  selection mode, delete confirmation, context menu, accessibility-name audit).

## [v1.7.4-windows] - 2026-08-25

### Changed
- **Back to a framework-dependent MSIX.** 1.7.3's self-contained package did not change winget's
  arm64 `Validation-Executable-Error`, so it bought nothing for ~100 MB. The package again declares
  `Microsoft.DotNet.DesktopRuntime.10` and `Microsoft.WindowsAppRuntime.1.8` as winget
  dependencies and the release workflow guards are restored.
- **Dependency updates** — Win2D 1.4.0,
  H.NotifyIcon.WinUI 2.4.1, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.* 10.0.11,
  System.Drawing.Common 10.0.11, Vortice 3.8.3, Windows SDK BuildTools 10.0.28000.2526 /
  WinApp 0.6.1, NAudio 3.0.1 (its `ISampleProvider`/`IWaveProvider.Read` are now `Span<T>`-based;
  the limiter, mute and timeline providers were updated and `MMDevice.CreateAudioClient()` replaces
  the obsolete property). Tests moved to **xUnit.net v3** (`xunit.v3` 4.0) running on
  Microsoft.Testing.Platform — xUnit v2 is deprecated. A root `global.json` opts `dotnet test` into
  the new MTP runner; the test project is now an executable and no longer references
  `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio` or `coverlet.collector`. The
  `dotnet test` command is unchanged.
- **Windows App SDK stays pinned at 1.8.260529003.** Newer 1.8 SDKs raise the MSIX's
  `Microsoft.WindowsAppRuntime.1.8` `MinVersion` (e.g. 1.8.260804001 → `8000.946.1701.0`) beyond
  the runtime winget's `Microsoft.WindowsAppRuntime.1.8` package can install (1.8.9 =
  `8000.879.2017.0`), which would break every winget install. The release workflow now asserts
  `MinVersion` ≤ the winget-available runtime (`WINGET_WINDOWSAPPRUNTIME_MAX_VERSION`).

### Added
- **Windows Sandbox validation scripts** (`windows/packaging/sandbox/`) — one command installs both
  runtimes in a fresh Sandbox, installs a built or released MSIX, launches it exactly like winget's
  harness (full `WindowsApps` path, foreign working directory), clicks through onboarding and
  requires the process to stay alive, collecting `crash.log` and event-log stacks on failure.

## [v1.7.3-windows] - 2026-08-25

### Changed
- **Self-contained MSIX** — the release package now bundles the .NET 10 runtime and the Windows
  App SDK 1.8 runtime (`SelfContained=true`, `WindowsAppSDKSelfContained=true`, trimming off), and
  the winget manifest no longer declares `Microsoft.DotNet.DesktopRuntime.10` /
  `Microsoft.WindowsAppRuntime.1.8` dependencies. The package is larger (~3×) but installs and runs
  on a clean machine with nothing else present, and removes the target machine's runtime versions
  as a variable while we chase winget's `Validation-Executable-Error`. See
  `windows/packaging/README.md` for the trade-offs and how to revert.
- **Tray icon registration retries back off exponentially** (5 → 10 → 20 → 40 s, ~2.5 min total)
  instead of every 2 s. A failed `Shell_NotifyIcon` call can block the UI thread for the shell's
  4-second reply timeout, so the old cadence could keep the process "Not Responding" for minutes on
  a machine whose taskbar was slow to answer. H.NotifyIcon still re-adds the icon on
  `TaskbarCreated` after the retries stop.
- **Welcome screen shows the real app icon** — the first onboarding step now uses the Tiny Clips
  app icon instead of a generic camera glyph on an accent tile.

### Fixed
- Window title-bar icon is resolved to an absolute path under the app's install folder and any
  `AppWindow.SetIcon` failure is logged rather than propagated from `Window.Activated`.

## [v1.7.2-windows] - 2026-08-24

### Fixed
- **Startup no longer crashes when the shell has no taskbar yet** — root cause of the
  `Validation-Executable-Error` that blocked the 1.5.3, 1.7.0 and 1.7.1 winget submissions. On the
  validation VM (reproduced on a GitHub `windows-11-arm` runner) `Shell_NotifyIcon(NIM_ADD)` fails
  because Explorer's taskbar is not available in that session; H.NotifyIcon's `ForceCreate()`
  threw `InvalidOperationException: TryCreate failed`, and because the exception left
  `OnLaunched`, XAML fail-fasted the process with `0xC000027B` even though the
  `Application.UnhandledException` handler had marked it handled. Tray-icon shell registration is
  now separated from constructing the icon, failures are caught and retried every 2 s for up to
  a minute (after which H.NotifyIcon still re-adds the icon on the shell's `TaskbarCreated`
  broadcast), and every remaining step in `OnLaunched` runs under a per-step guard so nothing can
  escape it. Hotkeys and capture flows keep working while the icon is pending.

### Added
- **`Windows Launch Smoke` workflow** (`.github/workflows/windows-launch-smoke.yml`) — manual
  workflow that mirrors winget's Installation Validation on real x64 and ARM64 runners: installs
  the .NET 10 Desktop Runtime and Windows App Runtime 1.8, installs either a released MSIX or a
  freshly built one from the current branch, launches `TinyClips.App.exe` from
  `C:\Program Files\WindowsApps`, and fails with the exit code plus `crash.log` / event-log
  stack if the process dies. Run it before opening a winget PR.

## [v1.7.1-windows] - 2026-08-24

### Changed
- **Screenshot editor image alignment is now a 3×3 placement grid** — the Background section's
  separate Horizontal / Vertical alignment combo boxes are replaced by a single labeled 3×3 grid
  of toggle cells, so any export-frame placement (e.g. bottom-right) is one click and every
  combination is visible at once. The selected cell is highlighted; arrow keys move between cells
  and each cell has a Narrator name ("Top-left image alignment", …). The grid is disabled when the
  frame is Original (no free space on either axis). Export alignment logic is unchanged. Mirrors
  the macOS 1.6.0 editor. Closes #296.

### Added
- **Teleprompter text size and panel height presets** — Settings → Teleprompter gains **Text
  size** and **Panel height** pickers (Small / Medium / Large), matching the macOS 1.6.0 presets
  (20/24/30 DIP text; 120/140/220 DIP panel). Both persist, drive the live recording overlay
  immediately — including while a recording is in progress — and are reflected in the Settings
  scroll preview. Keyboard/Narrator accessible. Closes #295.
- **Audio offset setting** — Settings → Video → Audio → *Audio offset* (−500 … +500 ms, default
  0) shifts all recorded audio relative to the video. Positive delays audio, negative plays it
  earlier — the fix for Bluetooth headsets and some USB microphones whose real latency WASAPI
  does not report. Applied at capture time on the shared recording timeline; a reset button
  returns to 0.
- **End-of-recording sync report** — every video recording now writes a `Sync report:` block to
  `webcam-diagnostics.log`: encoder path, elapsed/paused time, last video PTS, frames emitted and
  dropped to encoder back-pressure, audio PTS and the audio−video end delta, starved chunks,
  driver discontinuities, and per-source correction/pad/trim/underrun totals. Lets A/V sync be
  verified from the log without a listen test (see `docs/audio-video-sync.md`).
- **Scrolling capture** — The Screenshot picker gains a **Scroll** action (`P`). Select a region,
  scroll the page normally, and press **Done** (Enter) on the floating panel to stitch every frame
  into one tall image that flows into the usual save / editor / clipboard path; **Cancel** (Esc)
  discards. Frames are aligned with the same row-signature algorithm as macOS 1.6.0 (sticky
  headers and footers are not repeated), and guardrails stop growth automatically at 600 frames,
  a ~1.2 GB memory budget, or the maximum height (16 384 px when the editor is enabled, 50 000 px
  for direct save), saving what was captured so far. Mirrors the macOS scrolling capture.
- **Microphone soft-knee limiter** — Video recordings now run microphone audio through a
  per-sample soft-knee limiter (knee at 0.98 FS, `atan` roll-off toward full scale) before mixing
  so hot microphones round off instead of hard-clipping. On by default; toggle with Settings →
  Video → Audio → Microphone → *Limit microphone peaks*. Matches the macOS 1.6.0 limiter curve,
  never alters sample count or timing (A/V sync unchanged), and leaves system audio untouched.
- **Capture flow latency instrumentation** — `CaptureFlowTrace` records elapsed/delta timings at
  every transition of the capture flow (picker, region overlay, backdrop, countdown, recorder
  start/stop, editor/trimmer). Output goes to the debugger always, and to
  `%LOCALAPPDATA%\TinyClips\Temp\capture-flow-trace.log` in Debug builds or when the
  `TINYCLIPS_CAPTURE_TRACE` environment variable is set, so lag can be measured in packaged builds.
- **Screenshot source setting** — Settings → Screenshot → *Re-capture screen after region
  selection* (off by default). Off saves the frozen frame shown behind the region overlay
  instantly (Snipping Tool behavior, no second capture); on re-captures the live screen after the
  overlay closes, which reflects changes made while selecting.
- **Teleprompter transcript file import** — Settings → Teleprompter can now load a `.txt`, `.md`,
  or `.csv` file up to 1 MB to replace the recording transcript.
- **Direct region/window screenshot hotkeys** — Two new optional global shortcuts, **Screenshot
  region** and **Screenshot window**, skip the capture picker and jump straight to region or
  window selection for a screenshot (macOS ⇧⌘4-style parity). They are unbound by default; assign
  them under Settings → Keyboard shortcuts.
- **Tray Capture Text** — A compact action beside **Clips Library** starts region text recognition
  and copies the result to the clipboard.
- **OCR region capture** — Select **Recognize Text** in the capture picker or press `Ctrl+Shift+T`
  to recognize text in a screen region and copy it to the clipboard without saving an image.
  Large regions are automatically downscaled to fit the OCR engine's supported image size, and the
  capture picker's countdown timer is honored before recognition runs.
- **Display sleep prevention** — Video and GIF recordings can now keep the display awake for
  the duration of the recording, controlled by Settings → Video → Keep display awake while
  recording and enabled by default.

### Changed
- **Much snappier capture flow** — A performance pass over every transition between hotkey/tray,
  picker, region overlay, countdown, recorder, and editor/trimmer:
  - The 150 ms "wait for the tray menu" pause before every capture is gone; the tray popup,
    capture picker, and region overlay are excluded from capture (`WDA_EXCLUDEFROMCAPTURE`) so
    capturing can start immediately.
  - The region overlay's frozen screen backdrop is captured *while the picker bar is still
    showing* and handed to the overlay, which is created immediately and reveals itself as soon as
    the frame is painted (event-driven) instead of after fixed 250 ms + 50 ms delays. The backdrop
    upload uses `SoftwareBitmapSource` off the UI thread instead of a `WriteableBitmap` copy.
  - One process-wide shared Direct3D 11 device (multithread-protected, recreated on device
    removal) replaces the 3–4 devices previously created per capture; single-frame monitor
    captures settle after one frame instead of two; pixel readback uses a single contiguous copy
    when the pitch allows.
  - Region screenshots are cropped from the already-captured backdrop instead of performing a
    second WGC capture (unless a countdown ran or the new *Re-capture* setting is on).
  - The screenshot editor opens directly from the in-memory frame while PNG/JPEG encoding, the
    disk write, and the clipboard copy run in the background; Save / Save a copy / Open folder
    wait for the file if it is still being written. (When a screenshot downscale is configured the
    editor stays file-backed so its pixels match the saved file.)
  - Video/GIF recorders gain `PrepareAsync`: the capture session, H.264 transcoder, webcam, and
    audio devices are pre-warmed during the countdown so recording starts the instant it reaches
    zero; `StartAsync` reuses the prepared pipeline when the target/region match and the pipeline
    is discarded cleanly if the flow is cancelled.
  - The countdown signals completion as soon as it reaches zero (its card is already excluded from
    capture) instead of waiting 140 ms fade + 80 ms before recording starts.
  - The capture picker bar is now a single pooled window that is hidden/shown between captures
    instead of being created and destroyed (with its acrylic backdrop) on every hotkey press.
  - The GIF trimmer shows its first frame immediately and decodes the remaining frames in the
    background (previously it decoded every frame serially before the window became usable).
  - Picker re-open after a capture no longer stacks two 150 ms delays.
  - Release builds publish with ReadyToRun so first-use of each overlay window type doesn't pay JIT
    cost.
- **Updated Guide window** — The guide now covers OCR, configurable shortcuts, screenshot editing,
  recording options, tray quick access, and Clips Library management.
- **Guide tray icon** — The Guide action now uses a document icon instead of a help question mark.
- **Native Settings title bar** — Settings now uses the WinUI `TitleBar` control, keeps the
  navigation pane toggle in the title bar only when the adaptive navigation pane is collapsed,
  places About at the bottom of the navigation pane, and enforces DPI-aware minimum window
  dimensions. The native `AppWindow` icon is also set on activation so the Tiny Clips icon appears
  in taskbar thumbnails and Alt+Tab previews.
- **Native Clips Library title bar** — The Clips Library window now uses the WinUI `TitleBar`
  control with the app icon and a live capture-count subtitle. DPI-aware minimum window dimensions
  (480 × 520 DIP) are enforced and updated automatically when the window is moved between monitors
  with different scale factors.
- **Native Screenshot settings cards** — The Screenshot page now uses Community Toolkit
  `SettingsCard` and `SettingsExpander` controls with Fluent icons and the Gallery-style centered,
  constrained settings layout. Image format now starts expanded with icon-free nested JPEG quality
  and scale controls, while capture picker options are grouped with the other capture behavior
  settings.
- **Native Video settings cards** — The Video page now uses the same Gallery-width Toolkit layout
  with Fluent icons, expanded microphone and webcam groups, and compact icon-free nested cards.
  Microphone recording is controlled from its expander header, while the nested webcam enablement
  row uses a single-label checkbox and the webcam device selector sits in the expander header.
  Capture-picker, countdown, trimmer, and clipboard cards are ordered to follow the recording flow.
- **Gallery-style About page** — About now uses one expanded app-identity `SettingsExpander` for
  version, update information, repository access, and issue-reporting actions, with open-in-browser
  icons on external actions.
- **Native GIF settings cards** — GIF now uses the Gallery-width Toolkit layout with an expanded,
  icon-free nested quality group and recording actions ordered from setup through post-recording.
- **Native Mouse clicks settings cards** — Video and GIF click highlights now use expanded Toolkit
  settings groups with icon-free nested style cards. GIF starts with a single-label option to reuse
  video styling, which disables the GIF-specific cards, and redundant standalone hex fields were
  removed while retaining the color-preview dropdowns.
- **Native General settings cards** — General now uses the Gallery-width Toolkit layout throughout.
  Save locations are grouped in an expanded, header-only expander with the default-location option
  first, followed by Screenshot, Video, and GIF folder cards that show each path as the description
  and collapse while default folders are enabled. The branding overlay setting now lives under
  Capture behavior, and its otherwise-empty standalone navigation page has been removed.
- **Responsive Analytics layout** — Analytics now uses the same centered 1064-DIP maximum content
  width as the other Settings pages without changing its custom dashboard controls. Its Share
  actions now use a Toolkit settings card, while Analytics and General reset actions use compact
  left-aligned hyperlink buttons with additional separation from the preceding cards.
- **Native Uploadcare settings cards** — Uploadcare now uses the shared Gallery-width layout. Its
  account guidance and compact API-key link are integrated with the page title, while enablement,
  keys, automatic uploads, and copied URLs use icon-led Toolkit settings cards.
- **Native Hotkeys settings cards** — Screenshot, video, and GIF shortcuts now use separate
  icon-led Toolkit cards in the shared Gallery-width layout while preserving the existing shortcut
  recorder, conflict validation, status feedback, and reset actions.
- **Teleprompter overlay settings card** — Teleprompter enablement now uses an icon-led Toolkit
  settings card and the shared Gallery-width layout, while the transcript, speed, and preview
  controls retain their custom presentation.
- **Native editor and trimmer title bars** — The Screenshot Editor, Video Trimmer, and GIF
  Trimmer windows now use the WinUI `TitleBar` control with the app icon instead of a custom
  drawn title bar. DPI-aware minimum window dimensions are enforced and updated automatically
  when a window moves between monitors with different scale factors.
- **Native Guide, bug-report, and onboarding title bars** — The Guide, File a Bug, and Welcome
  windows now use the WinUI `TitleBar` control with the app icon. DPI-aware minimum window
  dimensions are enforced and updated automatically when a window moves between monitors with
  different scale factors (Guide 460 × 400 DIP; File a Bug 480 × 500 DIP; Welcome 480 × 520 DIP).

### Internal
- Added the Community Toolkit Settings Controls package as the foundation for migrating Settings
  pages to native `SettingsCard` and `SettingsExpander` layouts.
- **Layered acrylic tray flyout** — The system-tray quick-access window now uses a PowerToys-style
  layered acrylic content surface with a distinct bottom command bar and compact 32-DIP Fluent
  action buttons, while retaining the existing Tiny Clips popup dimensions and actions.
- **Window consistency and accessibility** — Secondary windows now honor app themes and
  high-contrast resources, while recording indicators and capture pickers use consistent surfaces
  and accessible control names.
- **Shared overlay infrastructure** — Capture-exclusion (`WDA_EXCLUDEFROMCAPTURE`) and
  rounded-region (`SetWindowRgn`/`CreateRoundRectRgn`, with correct HRGN ownership on failure)
  are consolidated into `OverlayWindowHelpers`; drag-anywhere behavior is consolidated into
  `FloatingWindowDragger`. These helpers are consumed by the borderless capture pickers,
  countdown, recording/processing indicators, recording-setup, teleprompter, webcam preview,
  and region-indicator windows, removing duplicated P/Invoke declarations and structs. The
  `TrayPopupWindow` now unsubscribes its `Activated` handler on closure.

### Fixed
- **Pausing a recording for more than 2 seconds no longer silences the rest of the clip** — while
  paused the audio muxer had no captured frames to hand over, hit its 2 s wait cap and returned a
  `null` sample, which `MediaStreamSource` treats as end-of-stream for the audio track. The muxer
  now waits through pauses without a cap, only ends the audio stream at a real stop, and fills a
  genuinely starved request (no audio device activity for 2 s) with silence instead of ending
  the track.
- **Pause/resume no longer shifts audio earlier than video** — pausing used to discard audio that
  had been captured but not yet muxed, and resuming appended the next packet contiguously, so every
  pause moved the remainder of the audio track earlier by the discarded amount. Captured audio is
  now retained through the pause and the resumed packet is placed on the paused-adjusted timeline.
  The screen pump also keeps its last frame through a pause so video resumes immediately on a
  static desktop.
- **Audio can no longer drift or jump out of sync over a recording** — audio was appended
  contiguously forever after the first packet, so a WASAPI buffer overrun (dropped packet), a
  microphone whose clock runs fast or slow against the system clock, or two sources at slightly
  different rates would silently slide the audio track relative to the video for the rest of the
  recording. `TimelineAlignedWaveProvider` now tracks each source's expected position on the shared
  timeline and, only when the deviation exceeds a 30 ms tolerance (or the driver flags
  `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY`), inserts silence or trims once to snap back. Ordinary
  per-packet jitter stays far below the tolerance, so the earlier crackle fix is preserved. Zero
  padding the muxer consumes during an underrun now also counts toward the source's timeline
  position so the following packet cannot land late behind it.
- **Audio track now ends where the video does** — stopping a recording ended the audio stream
  immediately, discarding up to a few hundred milliseconds of already-captured audio. The recorder
  now stops the devices, drains the remaining captured audio into the muxer (bounded to 500 ms),
  and only then ends the stream.
- **Resampled microphones (e.g. 44.1 kHz) no longer crackle at the read edge** — the muxer's
  back-pressure gate now keeps a 20 ms margin for sources that go through the resampler, so the
  resampler's lookahead never reads past captured data.
- **Hardened tray-first startup** — Launch no longer eagerly creates the off-screen UI Automation
  announcement window; it is created lazily on the first Narrator announcement and degrades to a
  log line if window creation fails. Hotkey registration, onboarding, and file activation are
  each guarded so a failure in one step cannot abort launch. A framework (XAML-thread) exception
  raised *during launch* — until the work launch queued on the UI thread has drained — is now
  logged and marked handled instead of terminating the process with a stowed-exception crash
  (`0xC000027B`), the failure mode reported by winget's `Validation-Executable-Error` check.
  Exceptions after launch are still fatal so mid-operation failures cannot leave the app running
  in a partially mutated state.
- **Persistent crash diagnostics** — Unhandled XAML, AppDomain, and unobserved task exceptions
  (plus guarded startup-step failures) are appended to `%LOCALAPPDATA%\TinyClips\Logs\crash.log`
  (capped at 512 KB, rolled to `crash.previous.log`) so packaged-app failures can be diagnosed
  without a debugger. The Logs folder is separate from the Temp folder purged by Settings.

## [v1.7.0-windows] - 2026-08-13

### Internal
- **Microsoft Store release path** — Store builds now use the Partner Center-assigned package
  identity, and a protected GitHub Actions workflow produces and submits the Store upload package.
  The Windows Store listing pack documents the submission copy, screenshots, and compliance answers.

### Changed
- **Refreshed Clips Library** — The wider media gallery wraps captures into a compact responsive three-column grid. Grid cards and list rows keep Open and Copy visible, with labeled More menus for Reveal, Delete, and Uploadcare actions. A macOS-inspired Sort & Filter flyout adds persisted type and date filters (Today, This week, and This month) without crowding the toolbar.
- **Clearer Uploadcare setup** — Settings now explains that Uploadcare uses your own account and links directly to the existing account and API-key page.

### Added
- **Screenshot editor zoom and pan** — The editor now opens fit-to-viewport and provides 25%–400% zoom controls, percentage presets, Ctrl++/Ctrl+-/Ctrl+0 shortcuts, pinch and Ctrl+wheel zooming, scrollbars, and Space-drag panning while keeping edits and exported pixels unchanged.
- **Persisted default capture folders** — Tiny Clips now saves concrete Pictures/TinyClips and Videos/TinyClips values for every capture type, including existing settings and reset settings.
- **Independent capture folders** — Settings → General now uses Pictures/TinyClips for screenshots and Videos/TinyClips for videos and GIFs by default. Turn off **Use defaults** to choose separate folders for screenshots, videos, and GIFs.
- **Teleprompter transcript overlay** — Settings → Video → Teleprompter lets you paste a script, enable the teleprompter, tune the scroll speed (10–200 DIPs/s), and start or stop a speed-accurate preview. During video recordings a small semi-transparent black overlay auto-scrolls the transcript on screen. The overlay appears only after recording starts, pauses and resumes with recording, fails closed if Windows cannot exclude it from capture, and remembers a monitor-relative position across mixed-DPI display changes.

### Fixed
- **DPI-correct secondary windows** — Settings, onboarding, guides, the Clips Library, tray popup, and capture overlays now size and position correctly across mixed-DPI monitors.
- **Reliable curved arrows** — Switching arrow styles no longer crashes the screenshot editor during short or active draws.
- **Clips Library opens reliably** — Replaced unsupported folder symbol values in clip action menus with valid Fluent font icons.
- **Settings opens reliably** — Replaced the unsupported Uploadcare navigation symbol that caused a `XamlParseException` when creating the Settings window.
- **Usable File a Bug form** — The bug report now opens immediately in a foreground, correctly sized window with scrollable content, wrapped automatically included details, and fixed action buttons instead of being constrained and hidden by the tray popup.
- Fixed the video trimmer so preview playback stops immediately when saving or closing the window.
- **Clearer tray popup actions** — The Clips Library and File a Bug buttons now use dedicated media-gallery and bug icons. Actions are grouped into library, app controls, help/support, and an isolated Exit button, while the wider popup no longer clips its picker controls.

### Added
- Added a live webcam preview to video setup and recording on Windows. The in-recording preview can be dragged or moved with the keyboard between capture corners, and every move is synchronized to the exported recording timeline.
- **Uploadcare cloud uploads** — Settings → Uploadcare now stores the public key and upload preferences locally while keeping the optional signing secret only in Windows Credential Locker. Tiny Clips can upload finalized screenshots, videos, GIFs, and exported frames automatically, optionally copy the delivery URL, and lets the Clips Library upload a capture manually with Copy URL and Open URL actions.
- **Clips Library** — A dedicated media library window (accessible from the tray popup) that lets users browse saved screenshots, videos, and GIFs without opening File Explorer. Each clip shows a thumbnail, filename, timestamp, and file size. Per-clip actions: Reveal in Explorer, Open in editor/trimmer, Copy to clipboard, and Delete with confirmation. Supports filter by capture type (All / Screenshots / Videos / GIFs), sort by newest or oldest first, and a grid/list view toggle. Window state (view mode, filter, sort) persists across sessions.
- **Temporary-file controls in Settings → General** — an Advanced card now shows the Tiny Clips temporary-file count and size, opens its dedicated `%LOCALAPPDATA%\TinyClips\Temp` folder, and can purge those files without affecting saved captures.
- **Narrator capture-status announcements** — Tiny Clips now announces screenshot, video, and GIF save results plus recording start and stop through a tray-lifetime UI Automation notification anchor, even when capture pickers and popups are closed.
- **Windows accessibility release gate** — releases now require a documented keyboard-only and Narrator pass across the tray, settings, capture flow, indicators, editor, trimmers, onboarding, and guide. The shared trim range is now keyboard-operable.
- **Flexible screenshot export frames** — the screenshot editor now offers Original, 1:1, 4:3, 16:9, 3:4, and 9:16 frames plus horizontal and vertical image alignment, without stretching annotated content.

### Changed
- Screenshot, video, and GIF settings now each provide matching before/after capture-picker controls.
- Video recordings now always request the H.264 High profile for the best quality and smallest files; the automatic Baseline fallback remains when the system encoder cannot initialize.

### Added
- Video recording controls now offer session-only microphone and system-audio mute buttons for sources that started with the recording.

## [v1.6.0-windows] - 2026-08-04

### Added
- **WebP files can now open in the screenshot editor** — File Explorer lists Tiny Clips as an Open with handler for `.webp` images, and the editor reports a clear message if Windows cannot decode an image.
- **Faster tray access to saved work** — The tray popup now opens configured capture folders and keeps the 10 most recent screenshots, videos, and GIFs available for reopening in their editor or trimmer.
- **Share capture analytics summaries** — Settings → Analytics now offers a Share button alongside Copy Summary, opening the native Windows share dialog with the selected-range activity summary.
- **Resizable screenshot annotations** — Selected annotations now show four corner handles for direct resizing, while arrows and lines retain dedicated endpoint handles and inside dragging still moves annotations.
- **Reset All Settings to Defaults** — Settings → General now has a "Reset All Settings to Defaults…" button under the new Advanced section. Clicking it shows a confirmation dialog; confirming resets every TinyClips setting to its default value and immediately refreshes the Settings window to reflect the restored state.

### Fixed
- **Winget-installed app launch is less likely to trip startup validation** — Tiny Clips now defers Windows app-notification registration until it actually needs to show a toast instead of registering during process launch, avoiding extra Windows App Runtime startup-task activation during install validation while preserving save/update/error notifications during normal use.

## [v1.5.3-windows] - 2026-07-25

### Fixed
- **Windows keyboard shortcut changes now provide clear feedback and reliably take effect** — Settings now uses a focused shortcut recorder with a live chord preview and explicit Save/Cancel actions, rejects incomplete or duplicate bindings (including the fixed Stop recording shortcut), reports shortcuts owned by Windows or another app, and restores the previous binding if registration fails. Native hotkeys are fully unregistered before replacement so stale registrations no longer prevent changes from sticking.

### Added
- **In-app update checks with guided upgrade actions (Windows Direct build)** — Tiny Clips can now check GitHub Releases for newer stable versions once per launch and via a new tray **Check for updates** action. Settings → About now shows update status and, when a newer version exists, provides guided actions to copy `winget upgrade Refractored.TinyClips` and open the latest GitHub Release page.
- Added in-app **File a Bug** entry points in both the tray popup and Settings → About. Both now open a lightweight two-field bug form (title + what happened) and then launch a pre-filled GitHub issue using the new quick bug template.
- Screenshot editor saves now follow a document-style workflow on Windows: **Save** overwrites the current file, **Save As** writes a new file, **Open Folder** reveals the active destination, and **Close** dismisses the editor without forcing an export.

### Fixed
- Fixed canceling a pre-recording countdown with the stop hotkey leaving the region indicator on screen.

### Internal
- **Settings window is now modular and lazy-loaded** — the ~2,000-line `SettingsWindow.xaml` monolith is split into nine focused `UserControl` sections (General, Analytics, Screenshot, Video, GIF, Mouse Clicks, Branding, Hotkeys, About) under `Settings/Sections`, each constructed only on its first navigation and cached afterwards. `SettingsWindow` keeps the title bar, `NavigationView`, and shared `SettingsViewModel`, but now only realizes the General section at startup instead of all nine. Capture-analytics refresh and microphone/webcam enumeration are deferred until the Analytics/Video sections are first opened instead of running eagerly for every Settings window. Replaced the single one-shot "ready" flag with a re-entrant, reference-counted persistence-suppression scope so compiled `x:Bind` TwoWay controls in a lazily realized section can't write back transient initial values over previously persisted settings. The suppression scope now completes via the section's `Loaded` event with a dispatcher-queue fallback, so persistence can never get stuck suppressed even if a section is swapped out of view before its first layout pass (rapid Settings navigation) or the window closes mid-realization. No behavior, bindings, or visuals changed for users.
- **Screenshot editor window is now modular** — the ~2,400-line `ScreenshotEditorWindow.xaml.cs` monolith is split into a shared `EditorController` (owns all editing state: annotations, tool/style defaults, coordinate mapping, undo, crop, Win2D bake/redaction) plus three focused `UserControl`s under `ScreenshotEditor/` — `EditorToolbar` (tool rail), `EditorInspector` (color/stroke/fill/text/counter/redaction + background/export panel), and `EditorCanvas` (preview image, crop selection, and annotation rendering). `ScreenshotEditorWindow` now only owns window-only concerns: HWND/file-picker interop, clipboard/save coordination, theme/lifecycle, keyboard shortcuts, and top-level command wiring. No behavior, bindings, tools, or visuals changed for users.
- **Screenshot editor annotation rendering no longer rebuilds the whole canvas on every pointer move** — `EditorCanvas` now retains one visual per annotation (created once, then mutated in place: brush colors set directly, geometry/position updated, freehand strokes append only their new points) instead of tearing down and redrawing every annotation's shapes on every `PointerMoved` event, which the previous implementation did (previously ~29 call sites into a single `RedrawOverlay()` that cleared and rebuilt the full overlay `Canvas.Children` collection each time). Structural changes (add/delete/undo/crop/tool switch/reset) still do a full reconcile pass; only the hot pointer-move path was changed. A moved redaction block also skips its expensive Win2D re-blur while actively dragging and recomputes it once when the drag settles, instead of recomputing the blur on every move.
- **Recording setup window no longer rebuilds its microphone/webcam flyouts on every selection** — `RecordingSetupWindow.xaml` now hosts two focused `UserControl`s under `RecordingSetup/`: `AudioDeviceControl` (system audio + microphone toggle and device picker) and `WebcamOptionsControl` (webcam toggle plus the camera/shape/corner/size/rounded-corner settings flyout); the window keeps only placement/DPI, drag movement, key handling, completion guarding, and the microphone/webcam permission coordination (including auto-enabling microphone when webcam video is granted). Previously, every microphone selection called `MenuFlyout.Items.Clear()` and reconstructed every device `ToggleMenuFlyoutItem` from scratch, and every webcam camera/shape/corner/size/rounded-corner-value selection rebuilt the *entire* webcam settings flyout — 6 submenus and ~17+ leaf items (plus one new closure per leaf item) — from scratch, up to 9 call sites in total. Both flyouts now cache their menu items: the static shape/corner/size/rounded-corner-value groups are built exactly once and never rebuilt, device items (microphone/camera) are only rebuilt when the enumerated device collection actually changes (not on every selection or loading toggle), and ordinary selection changes just flip `IsChecked` on the existing cached items. Device item click handlers now read the associated device from the item's `Tag` through one shared handler instead of a new per-item lambda closure each rebuild. No behavior, permission prompts, result values, or visuals changed for users.

## [v1.5.2-windows] - 2026-07-07

### Added
- **Richer in-progress recording controls** - the Windows recording overlay now supports pause, resume, restart, discard, and stop while keeping the panel out of captured output.
- **Capture analytics in Settings** - Tiny Clips for Windows now tracks screenshots, videos, and GIFs taken per day on the local device, shows the last 7 or 30 days in a stacked chart under Settings → Analytics, and lets you reset that history.
- **Extended capture analytics** - added lifetime (all-time) totals per capture type, per-type checkboxes to show/hide screenshots/videos/GIFs on the chart, hover tooltips with exact daily counts, a "busiest day of week" and "most active hour" insights breakdown, and a Copy Summary button that puts a quick text summary of your capture activity on the clipboard.
- **Remove audio export option for video trimming** - the Windows video trimmer now offers a clear Remove audio checkbox and renders no-audio exports when enabled.
- **Webcam picture-in-picture overlay for video recordings** - added webcam controls in Settings (enable, device, shape, corner, size, rounded-corner value) and integrated webcam compositing into recorded video frames.
- **Video encoder profile is now selectable in Settings** - choose between **High** (default; B-frames + CABAC for the best quality and smallest files) and **Baseline** (no B-frames, for maximum player compatibility) under Settings → Video → Encoder profile.
- **Webcam capture pipeline in Core** - added `MediaCapture` + `MediaFrameReader` based webcam capture service and overlay compositor support in `VideoRecordingService`.
- **Automatic microphone default when webcam is enabled for video** - starting a video recording with webcam enabled now auto-enables microphone for that recording flow.
- **Webcam capability declaration** - app manifest now declares webcam device capability for packaged app access.
- **Webcam controls in the Windows pre-record setup panel** - video recordings can now toggle the webcam and pick the camera device from the compact setup overlay before countdown.
- **Webcam-unavailable notification** - if the webcam can't start (for example camera access is blocked in Privacy settings) or is lost mid-recording, the app now shows a toast explaining why and that the screen recording continued without it, instead of silently dropping the overlay.

### Improved
- **Countdown animation polish** - the Windows countdown overlay now fades in/out, animates each number, and gives the final second stronger emphasis while remaining excluded from capture.
- **Smoother countdown number handoff** - each countdown tick now performs a quick ease-out before the next number eases in, and the final second now also shifts to a bolder weight for clearer emphasis.
- **Faster editor color picking with preset swatches** — the screenshot editor color controls (stroke, fill, text, and number badge) now show common preset color swatches first with a **Custom…** button that opens the full native color picker, so the common case is a single click while full precision/opacity stays available. The reusable `SwatchColorPicker` control is applied across those inspector color surfaces.
- **Editor color controls are now preview dropdowns** — each `SwatchColorPicker` collapses into a compact button that previews the current color and its name, opening the swatch grid and Custom picker on demand. The shape **Fill** control adds a **None** (transparent) swatch so rectangles and circles can be left unfilled directly from the color control.
- **Screenshot editor background settings now round the screenshot content itself** — the Image corners control now explicitly clips the screenshot preview content to match the rounded-corner export behavior.
- **Video/GIF recording now matches the macOS target-first setup flow** — after choosing Region,
  Screen, or Window, Windows now shows a pre-record panel before countdown. Video captures can pick
  system audio, microphone on/off, the microphone device defaulting to Settings, and mouse-click visuals;
  GIF captures keep the no-audio setup with mouse-click visuals.
- **Recording time limit now lives in the first capture picker with countdown** — for video captures,
  the time limit is now selected on the initial Region/Screen/Window picker bar alongside countdown.
- **Windows recording setup pickers are now compact flyout menus** — microphone and webcam device choices now use chevron menus with a checked selected item instead of wide inline pickers.
- **Settings are now grouped under clear sub-headings** — each Settings section now clusters related options under labeled groups (for example General → Appearance / Files & saving / Startup / Capture behavior; Video → Video quality / Audio / Webcam overlay / Recording & output; plus Screenshot, GIF, and Mouse Clicks), making it easier to see which options belong together.
- **Settings now show separate effective save paths for screenshots and video/GIF** — the Save locations card now displays both paths side-by-side while still using one optional custom folder override for all capture types.
- **Webcam overlay Settings can be configured while disabled by default** — the video webcam toggle now only controls whether the webcam starts on by default; device, shape, corner, size, and rounded-corner settings stay editable and persist independently.
- **Webcam pre-record options now live in a compact gear menu** — the Windows recording setup bar keeps the webcam on/off toggle visible, while camera, shape, corner, size, and rounded-corner controls move into a single settings flyout to free horizontal space.

### Fixed
- **Capture pickers no longer flash as oversized blank windows** — the Region/Screen/Window picker surfaces are now sized and positioned before first activation, and the region selector stays transparent until its screen-snapshot backdrop is ready to present.
- **Recorded audio was crackly and broke up** — two compounding issues. First, the shared-timeline audio path re-derived every ~10 ms WASAPI packet's position from its (frame-rounded, jittery) QPC timestamp and reconciled it against the running write cursor, inserting or dropping a sample or two on every packet (~100 micro-edits per second); only the first packet from each source is now aligned to the shared origin (preserving inter-source start offsets for sync) and later packets are appended contiguously. Second, the custom WASAPI capture polled a tiny ~22 ms buffer on a normal-priority thread, so under the CPU load of software H.264 fallback encoding the capture thread was starved and the buffer overran, dropping whole chunks. The capture now requests a 200 ms buffer and polls on a fixed 8 ms interval at highest thread priority.
- **Recorded audio lagged the video by ~1 second** — the audio source pads silence on demand (`ReadFully`), so the `MediaTranscoder` drained the audio stream far faster than real time and raced the whole audio track ~1 s ahead of the video; captured sound then landed ~1 s late on playback. Each audio sample request is now gated on captured-frame availability (proper producer/consumer back-pressure): the muxer only receives a 20 ms chunk once that many real frames have been captured across all sources. Audio stays locked to real capture progress (no racing/delay) and never reads an empty buffer (no silence-splicing crackle). Recorded audio is additionally advanced by the source's WASAPI stream latency so it lines up with video captured at the same instant. See [`docs/audio-video-sync.md`](docs/audio-video-sync.md).
- **Recordings had no audio because WASAPI capture never started** — the timeline-based audio capture exposed the raw WASAPI shared-mode mix format (a `WaveFormatExtensible`) to NAudio's mixing pipeline, which rejected it with `ArgumentException: Unsupported source encoding`. Both the system-audio (loopback) and microphone sources failed to start silently, so recordings were produced with no audio track. The capture now standardizes the mix format to its equivalent IEEE-float/PCM `WaveFormat` for the pipeline while still initializing WASAPI with the native mix format.
- **Video recording start now retries Media Foundation profile negotiation safely** — if the default H.264 profile hits `MF_E_TRANSFORM_TYPE_NOT_SET` (`0xC00D6D60`) during transcode preparation, Windows recreates the stream source and retries with a software Baseline profile instead of leaving native callbacks attached after a failed start.
- **Webcam teardown no longer unsubscribes from `MediaCapture.Failed`** — disposing the WinRT `MediaCapture` now owns cleanup and stale failure callbacks are ignored, avoiding a C#/WinRT `ExecutionEngineException` when stopping webcam capture.
- **Camera and microphone permissions are requested when their options are enabled** — Settings and the pre-record setup panel now request packaged-app device access immediately, revert denied toggles, and preserve each independently allowed option when webcam setup also enables the microphone.
- **Screen, webcam, system audio, and microphone now share one recording timeline** — independent Windows capture sources previously discarded their native start timestamps and were mixed from whichever packet happened to arrive first, so camera video and the two audio sources could begin at different offsets and drift apart. Recording now uses a single system-relative QPC origin: WASAPI packet timestamps preserve each audio source's exact offset (inserting silence or trimming overlap as needed), webcam timestamps are normalized against the same origin, and screen frames use that clock for presentation timestamps.
- **Default save locations are split by media type** — screenshots now default to `Pictures\TinyClips`, while video/GIF recordings default to `Videos\TinyClips` (reducing recording-related Controlled Folder Access prompts). A custom Save location still overrides all capture types.
- **Recordings no longer start late with several seconds of dead pre-roll** — the recorded video clock used to start the instant screen capture began, while the encoder, camera and audio were still warming up. That baked the warm-up (a frozen frame with no webcam) into the front of every clip, so a 5-second recording produced an ~8-second file, the webcam was missing from the start, and frames near the end were dropped (the warm-up pre-roll saturated the bounded frame buffer). Frame emission is now deferred until the encode pipeline is actually ready: capture warms up silently, the recorder waits briefly for the first webcam frame, flushes stale buffered audio, and only then starts the presentation clock — so the clip length matches the on-screen timer, the webcam is present from frame one, and audio stays in sync.
- **Video encoder profile no longer forced to Baseline** — earlier builds pinned the H.264 encoder to the Baseline profile to work around clips rendering blank in Media Foundation surfaces. That blank-playback symptom was traced to a transient system-side GPU/decoder issue (it also affected non-TinyClips recordings and cleared after a reboot), not the encoded file — Media Foundation's own decoder reads every frame with `start_time=0`. The encoder now defaults to the higher-quality **High** profile, with **Baseline** available as an opt-in compatibility choice in Settings.
- **Webcam overlay now actually composites into recorded video** — reading each webcam frame's pixel buffer used the `IMemoryBufferByteAccess` COM-interop interface, which fails to `QueryInterface` under C#/WinRT (`InvalidCastException`/`E_NOINTERFACE`). Every webcam frame was dropped before it could be cached, so the overlay never drew (`composited=0`) even though the camera was capturing normally. Frame bytes are now read through a fully WinRT-projected path (`SoftwareBitmap.CopyToBuffer` + `DataReader`). The compositor blends BGR under its own precomputed shape mask and ignores the camera's source alpha, so undefined driver alpha bytes no longer matter.
- **Recording setup panel no longer clips the close button** — the microphone and webcam device labels now use a fixed width so the panel keeps a stable size when device names load asynchronously, keeping the Record and close buttons fully visible.
- **Settings now opens immediately while microphones load in the background** — microphone device enumeration is now asynchronous, and the microphone picker shows a loading spinner until devices are ready instead of blocking the settings window during startup.
- **Picker overlays now match the tray popup shell style** — capture, screen, window, and countdown picker windows now use the same context-menu presenter behavior, 8px rounded window clipping, and filled popup surface treatment as the tray popup, making corners/backgrounds visually consistent.
- **Overlay windows now use a consistent popup presenter** — recording/processing and region select/indicator overlays now use the same context-menu presenter path as other picker overlays, reducing shell/chrome inconsistencies.
- **winget dependency installation no longer forces user scope** — removed `Scope: user` from the
  installer manifest generated for winget submissions. The app MSIX still installs per-user, but
  the .NET Desktop Runtime and Windows App Runtime dependency packages use machine/unknown-scope
  installers; forcing user scope made winget validation reject them with "No suitable installer found."
- **Recording overlays now appear immediately after countdown for video/GIF** — the red capture border and stop controls are shown before the recorder start call, so recording UI no longer appears late after capture has already begun.
- **"Show in Explorer after save" is now respected in all finalize paths** — post-trim and direct video/GIF finalization only reveal files in Explorer when the setting is enabled.
- **Stop panel is now visible during countdown but safely disabled** — for video/GIF captures with countdown enabled, the recording panel appears immediately with Stop disabled, then enables once recording actually starts.
- **Recording panel now prefers positioning above the selected region** — when there is space, the stop panel appears above the red region box; it falls back below or clamped in-view placement when needed.

## [v1.0.7-windows] - 2026-06-16

### Fixed
- **Onboarding "Skip" button is now reliably clickable** — moved it out of the custom title bar
  (where it overlapped the window's minimize/maximize/close caption buttons) into the wizard footer
  next to **Back**, and hid it on the final step where **Get started** already completes onboarding.
- **Settings now load and persist correctly in the installed (packaged) app** — fixed a
  first-load race where WinUI TwoWay bindings (theme, save location, file name template,
  screenshot/video options, microphone) wrote their controls' empty initial values back over
  the loaded settings, which showed blank fields and silently reset choices on every reopen.
  Persistence is now suppressed until the window's first layout completes, then the view model
  re-syncs from storage. An already-empty file name template also heals back to the default.

## [v1.0.6-windows] - 2026-06-16

### Changed
- **Switched to a framework-dependent MSIX** to keep the package small. The app no longer
  bundles the .NET Desktop Runtime or the Windows App SDK runtime. Instead the winget installer
  manifest declares both as package dependencies (`Microsoft.DotNet.DesktopRuntime.10` and
  `Microsoft.WindowsAppRuntime.1.8`), so winget installs the runtimes before the app, and the
  Windows App SDK runtime is also auto-acquired by the OS on machines with the Store/App
  Installer. This reverses the self-contained approach from v1.0.5 (which produced a ~56 MB
  package). The earlier `0x80073cf3` failure we saw was a Windows Sandbox artifact — Sandbox has
  no Store, so MSIX framework auto-acquisition cannot happen there; real machines and winget's
  validation pipeline can deliver the dependencies.
- The release workflow now builds with `SelfContained=false` + `WindowsAppSDKSelfContained=false`
  and asserts the package is genuinely framework-dependent (WindowsAppRuntime dependency present,
  no bundled `coreclr.dll`) before signing.

### Improved
- **Onboarding wizard now defaults to a wider layout** — increased the first-run welcome window
  width and relaxed step content max-widths so introductory copy is less likely to wrap on first launch.

### Fixed
- **Launch at login now survives app updates** — migrated from writing an `HKCU\...\Run` registry
  value (which pointed at the versioned `WindowsApps\<PackageFullName>` install path and silently
  stopped working after every MSIX update) to the supported `windows.startupTask` MSIX extension and
  `Windows.ApplicationModel.StartupTask` API. The Settings toggle now reflects the real OS-owned state
  and explains when Windows has disabled launch at login (e.g. turned off in Windows Settings → Apps →
  Startup, or blocked by policy). Unpackaged developer runs keep the registry behavior as a fallback.

## [v1.0.5-windows] - 2026-06-16

### Fixed
- **Clean-machine and winget installs now succeed** — the MSIX is now built **fully
  self-contained**, bundling both the .NET Desktop Runtime (`SelfContained=true`) and the
  Windows App SDK runtime (`WindowsAppSDKSelfContained=true`). Previously the package was
  framework-dependent and declared `Microsoft.WindowsAppRuntime.1.8` as a winget dependency,
  but winget does not auto-install MSIX framework dependencies, so installation failed at ~95%
  with `0x80073cf3` on clean machines and on winget's network-isolated Installation Validation
  VMs. The self-contained package has no external framework dependency and installs anywhere.
- **No more "install .NET Desktop Runtime" prompt** — the .NET runtime is bundled, so the app
  launches on a machine with no .NET installed.
- The build keeps the self-contained build and MSIX packaging in a single `dotnet build` so the
  reg-free WinRT `activatableClass` registrations are embedded in the app executable (their
  absence previously caused a `REGDB_E_CLASSNOTREG` startup crash). The release workflow now
  asserts those registrations and the absence of a framework dependency before signing.

## [v1.0.4-windows] - 2026-06-16

### Fixed
- **winget Installation Validation now passes** — the framework-dependent MSIX declares the
  Windows App SDK runtime (`Microsoft.WindowsAppRuntime.1.8`) as a package dependency in its
  AppxManifest. That runtime is missing on winget's clean, network-isolated validation VMs, so
  installation failed there (it succeeded locally only because the runtime was already present).
  The winget installer manifest now declares `Microsoft.WindowsAppRuntime.1.8` under
  `  Dependencies.PackageDependencies`, so winget installs the runtime first on any clean machine.
- **Installed MSIX no longer crashes on startup** — the winget/MSIX build was switched to
  self-contained packaging to clear winget validation, but the resulting package shipped an
  AppxManifest with **no** WinRT activation registrations. On a clean machine (without the
  Windows App Runtime installed) the app crashed immediately at `Application.Start` with
  `REGDB_E_CLASSNOTREG` (`0xc000027b`), because the installed package resolves WinRT activation
  from the manifest, not from the executable's embedded reg-free manifest. The release workflow
  now packages a **framework-dependent** MSIX with the supported MSBuild MSIX tooling, which
  emits the full set of `ActivatableClass` registrations and declares the Windows App Runtime as
  a framework dependency (the same configuration as the original, working v1.0.0 release). The
  runtime is acquired automatically by winget/Store at install time.

## [v1.0.0-windows] - 2026-06-15

### Added
- **Branding overlay** — when enabled in Settings, captures get a subtle "Captured on Tiny Clips"
  badge (a rounded black pill with white text) in the bottom-right corner, matching the macOS app.
  It is burned into screenshots, every GIF frame, and every video frame; the badge scales with the
  capture height. Off by default.
- **Multi-monitor capture targeting** — on multi-display setups, Settings now includes a capture
  target mode (**Ask every time**, **Display under cursor**, or **Main display**) for Screen/Region
  captures. Region selection now works across all monitors when needed, and countdown/recording/
  processing overlays are anchored to the selected display.
- **Microphone & system-audio toggles in the recording bar** — while recording a video, the
  floating recording bar now shows two small icon toggles (microphone and system audio) next to
  Stop. Toggling them updates the audio defaults used for your next recording, so you can quickly
  flip audio sources on/off without opening Settings. The toggles are hidden for GIF captures
  (which have no audio).
- **Drag the selected trim region** — on the video/GIF trim bar you can now grab the highlighted
  selection between the two handles and slide the whole range left or right (its length is
  preserved), in addition to dragging each handle individually. The cursor shows a move icon over
  the selection and a resize icon over the handles.
- **Processing indicator after you stop a recording** — when you stop a video or GIF capture, a
  small always-on-top panel with a spinner and "Processing…" / "Finalizing your clip" appears while
  the clip is encoded, so it's clear the app is working before the trimmer or save completes. The
  panel is excluded from screen capture and dismisses automatically when finalizing finishes.

### Removed
- **Clips Manager & upload scaffolding removed** — the unused Clips Manager library service and the
  Uploadcare/auto-upload settings (which had no UI and no working backend on Windows) were deleted
  to keep the app focused. Browse your captures in File Explorer; "Show in Explorer" after each
  capture still works.

### Fixed
- **Trim bar handles & scrubbing now respond to the mouse** — the single-line trim control was
  completely inert: its hit-test surface was disabled (the inner canvas had hit-testing turned off
  and the control's transparent background isn't painted by the default `UserControl` template), so
  no pointer events ever reached it. The track now has a real transparent hit surface, so the start
  and end handles drag, and clicking the dimmed groove scrubs the playhead.
- **Countdown now reliably shows before video & GIF recording** — the pre-capture countdown card
  stopped appearing because the window was clipped to a rounded square (`SetWindowRgn`) before it was
  ever shown, leaving the surface blank. The rounded clip and positioning are now applied after the
  window is activated, so the countdown displays again for all capture types.
- **Editor selection box now covers text & freehand annotations fully** — selecting a text or
  draw (freehand) annotation previously showed a tiny selection box anchored at the start point,
  and the clickable hit area was just as small. Text is now measured to its rendered size and
  freehand strokes recompute their bounds from the drawn path (padded by the stroke width), so the
  selection marquee and hit-testing cover the entire annotation.

### Changed
- **About page now links directly to issue/feature requests with prefilled details** — Settings →
  About now includes an **Open an issue or feature request** link that deep-links to the GitHub
  repo issue form and pre-fills app version + Windows runtime details (similar to the macOS flow).
- **Store-vs-Direct behavior now uses a build flag** — Windows keeps one feature set (no Pro tier),
  and store-specific distribution behavior is now controlled by
  `-p:TinyClipsStoreBuild=true` / `TINYCLIPS_STORE_BUILD` (for example, hiding direct/winget update
  surfaces in Store builds).
- **Windows privacy policy URL is now set for distribution metadata** — the winget locale manifest
  now publishes `PrivacyUrl: https://tinyclips.app/privacy.html`, and Windows packaging docs now
  reference the same URL for Store listing metadata.
- **Repository renamed to `jamesmontemagno/tiny-clips`** — the GitHub repository link on the
  Settings → About page, the winget manifests, and all documentation now point at the new
  `tiny-clips` repository (the old `tiny-clips-mac` URLs continue to redirect). The winget package
  identifier is unchanged (`Refractored.TinyClips`).
- **Default save location is now `Pictures\TinyClips`** — new clips default to a `TinyClips` folder
  inside your Pictures library (matching the macOS app) instead of the Desktop, when you haven't
  chosen a custom save folder in Settings.
- **Video trimmer now uses a single play/pause button instead of the full media transport bar** —
  the built-in media transport controls are hidden (you scrub with the trim bar), replaced by one
  play/pause toggle next to the frame stepper. The icon swaps between play and pause, and preview
  playback stops automatically at the trim end (pressing play again restarts from the trim start).
- **GIF preview button now shows a true play/pause state** — the GIF trimmer's preview toggle now
  swaps its icon between play and pause (and updates its label/tooltip) instead of showing a static
  play glyph.
- **GIF now uses the Settings "Pictures" icon in the tray & picker** — the GIF capture tile in the
  system-tray popup and the GIF mode badge in the capture picker now use the same Pictures glyph
  as the GIF page in Settings, for a consistent icon across the app.
- **Settings has an About page** — a new **About** section in Settings shows the app name and
  version, a link to the project's **GitHub repository**, and a `© <year> Refractored LLC`
  copyright line.
- **Video & GIF trimmers now use a single-line trim bar (macOS-style)** — both trimmers replace the
  previous stacked start/end (and current-frame) sliders with one custom `TrimBar`: a dimmed track
  with an accent-colored selection between two draggable handles and a movable playhead. Drag a
  handle to set the start/end, or click/drag the track to scrub. The playhead follows playback and
  frame stepping, matching the Mac app's trim slider.
- **App & tray icon restyled to match the macOS app** — the Windows icon set now uses the same
  glyph as the Mac app (four corner focus-brackets around a solid center dot) in place of the older
  nested-squares-with-crosshair mark, while keeping the blue gradient background. Every asset
  (tray icon, app icon, Start tiles, Store logo, splash screen, lock-screen logo) was regenerated
  from a single source via `windows/tools/generate-icons.py`.
- **Default save location now matches the macOS app (Desktop)** — newly captured clips default to
  the user's **Desktop** for every capture type, mirroring the Mac app, instead of
  `Pictures\TinyClips` / `Videos\TinyClips`. Picking a custom Save location still overrides this.
- **Settings shows the effective save location** — the Save location card previously rendered a
  blank line until you picked a folder, because the default Pictures\TinyClips path is resolved at
  save time rather than stored. It now displays the resolved folder, labelled `(default)` when no
  custom location has been chosen.
- **More speed presets for video & GIF trimmers** — the playback/output speed dropdown now offers
  finer and wider steps (0.1x, 0.25x, 0.5x, 0.75x, 1x, 1.25x, 1.5x, 1.75x, 2x, 2.5x, 3x, 4x, 5x)
  instead of the previous six, defaulting to 1x.
- **Countdown overlay redesigned (clean rounded square)** — the pre-capture countdown is now a
  single rounded-**square** card with a big centered number and a subtle accent border. The old
  circular clip + inner ring (which read as a "box in a box") was removed; the window is clipped
  to the same rounded square as the card so it fills edge-to-edge. Still excluded from recordings
  and hidden before the first captured frame.
- **System-tray popup redesigned (PowerToys-style)** — clicking the tray icon (left or right)
  now opens a compact custom popup with the three primary capture actions (Screenshot, Video,
  GIF) as large tiles across the top and a row of small icon buttons (Settings, Guide, Exit)
  at the bottom, instead of a vertical context menu. The popup is a borderless acrylic window
  anchored next to the cursor that light-dismisses on focus loss. This also resolves the
  first-open clipping seen with the previous `MenuFlyout`-based menu on high-DPI displays.
- **Screenshot editor: redesigned layout (left tool rail + inspector)** — tools now live in a
  vertical rail on the left with a contextual **inspector** panel beside them (mirroring the macOS
  app), and the output actions (Apply crop, Undo, Delete, Reset, Copy, Save) sit in a top bar.
  Selecting an annotation loads its properties into the inspector so they can be re-edited.
- **Screenshot editor: continuous sizes** — stroke width (1–40 px), number-badge size (50%–400%),
  and text font size (10–200 px) are now sliders instead of fixed presets.
- **No Pro gating on Windows** — the Pro concept was removed entirely from the Windows app. All
  features (mouse-click overlays, separate GIF click styles, branding, uploads, etc.) are always
  available; the Pro settings section, upsell banners, and the `IEntitlementService`/`ProFeature`
  abstraction were deleted to keep the app simple.

### Fixed
- **Screenshot editor: text tool no longer vanishes on click** — adding text used a fragile inline
  overlay box whose focus raced with the pointer release, so it appeared and instantly disappeared,
  and its resize grip was tiny. Text now opens a proper modal **text dialog** with a multi-line entry
  field, so the click-and-it's-gone behavior is gone.
- **Screenshot editor: arrowhead tip poke-through** — the arrow shaft was drawn all the way to the
  tip with a round end cap, so the cap poked past the filled arrowhead. The shaft now stops short of
  the tip and the arrowhead is aligned to the true tangent at the tip, so the point looks clean.
- **Screenshot editor: tool rail clipping** — the tool rail icons could be cut off at fractional
  display scales (e.g. 125%): the buttons kept their default internal padding and the auto
  scrollbar overlapped the right edge. The rail now uses zero-padding 44×44 buttons with centered
  glyphs and reserves space for the scrollbar, so every tool icon is fully visible.
- **Screenshot editor: background panel clipping** — the Background expander's padding/corner/shadow
  sliders and style dropdown were cut off on the right edge of the inspector; the panel now stretches
  to fit and no longer overflows.
- **Screenshot editor: tool rail clipping** — the vertical tool rail is now scrollable, so the
  lower tools (Draw, Text, Number, Redact) are no longer cut off on shorter editor windows.
- **Screenshot editor: arrow/line crash** — drawing an arrow or line that pointed up or to the
  left crashed the app (`ArgumentOutOfRangeException` from a negative-size `Rect`). Lines and
  arrows are now stored as directed endpoints and render correctly in any direction.
- **Screenshot editor: editor failed to open** — the redesigned inspector sliders fired their
  `ValueChanged` handlers during XAML load (before controls existed), throwing a
  `NullReferenceException`/`XamlParseException` so the editor never appeared after a capture or via
  "Open with Tiny Clips". The initialization guard now defaults on.

### Added
- **Screenshot editor: redaction styles (blur, pixelate, solid)** — the redact tool now has a **Style**
  picker in addition to the strength levels. Choose **Blur** (gaussian, the previous behavior),
  **Pixelate** (mosaic blocks whose size scales with strength), or **Solid** (a hard black bar). The
  style applies per-redaction and can be changed after selecting one.
- **Screenshot editor: rich text dialog** — the text tool now opens a dedicated dialog with **bold,
  italic, underline and strikethrough** toggles, font and size pickers, a text color picker and a live
  preview, confirmed with **OK**. Double-click an existing text label to reopen the dialog and edit
  it (clearing the text deletes the label). Styling carries over to the next text you add.
- **Screenshot editor: straight & curved arrows** — the arrow tool gains an **Arrow** style picker
  (Straight, Curved, Curved alt) in the inspector. Curved arrows bow to either side via a quadratic
  bezier shaft, and the style can be changed per-arrow after selecting it.
- **Trimmers: export the current frame as a PNG** — both the video and GIF trimmers now have an
  **Export frame** button that saves the frame currently shown as a still PNG into the Tiny Clips
  folder (with a save notification). For video, the frame is extracted at the exact paused
  position; for GIF, the exact frame on screen.
- **Trimmers: frame stepper** — left/right step buttons move the preview one frame at a time. The
  GIF trimmer adds a current-frame scrubber + "Frame X / N" readout; the video trimmer nudges the
  paused position by a frame and shows the current position.
- **Screenshot editor: image dimensions** — the editor's top bar now shows the current image
  size (`W × H px`) on the right, updating after a crop is applied.
- **Screenshot editor: shape fill color** — rectangles and ellipses can now be filled with a
  color (with adjustable opacity). Fill is **off (transparent) by default**; enable it and pick a
  color in the inspector.
- **Screenshot editor: text font & color controls** — the Text tool now lets you pick a font
  family and size; numbered badges have an independent **number color** (default white) on top of
  the badge fill color.
- **Screenshot editor: Shift to constrain** — hold **Shift** while drawing a rectangle/ellipse for
  a perfect square/circle, or while drawing a line/arrow to snap to horizontal, vertical, or 45°.
- **Screenshot editor: export background, padding, corners & shadow** — a new **Background**
  toolbar control adds a styled backdrop behind the screenshot (Transparent, Solid, or Gradient)
  with 12 solid + 12 gradient presets and a custom color picker, plus **Padding** (0–160 px),
  **Corner radius** (0–60 px), and **Shadow** (0–40) sliders. The screenshot is rendered as a
  rounded, elevated card composited over the chosen background at full resolution on save/copy,
  mirroring the macOS editor's export background feature.
- **Screenshot editor: redaction strength & number-size levels** — the Redact tool now offers
  **Light / Medium / Heavy** blur strength and the Number badge tool offers **50%–200%** size
  presets, both shown contextually in the toolbar (mirrors the macOS app's inspector controls).
- **Screenshot editor: real fuzzy redaction** — redaction now applies a true Gaussian blur of
  the underlying content (intensity driven by the chosen level) in both the live preview and the
  saved/exported image, replacing the previous flat translucent block.
- **Programmable keyboard shortcuts** — the Screenshot, Record video, and Record GIF global
  shortcuts can now be reassigned from Settings (click **Edit**, then press a combination that
  includes Ctrl/Alt/Shift/Win) or **Reset** to the defaults; changes re-register the global
  hotkeys immediately.
- **Per-capture video time limit** — the capture picker now has a time-limit dropdown for video
  (No limit / 1 / 2 / 5 / 10 / 15 / 30 min) that overrides the default from Settings for that
  recording.
- **Open with Tiny Clips** — image files (.png/.jpg/.jpeg) can be opened directly in the
  screenshot editor via the Windows "Open with" menu (file-type association in the package).
- **Microphone device picker** — when "Record microphone" is on you can now choose which input
  device is recorded (defaults to the system default) in the Video settings.
- **Separate Video and GIF mouse-click styles** — the Mouse Clicks settings now have independent
  size, opacity, and color controls for video versus GIF recordings, each with a Fluent
  **color picker** (the GIF group is disabled while "GIF uses video click settings" is on).
- **GIF trimmer preview playback** — a play/pause toggle animates the selected frame range in the
  GIF trimmer, honoring per-frame delays and the chosen output speed so you can preview the result
  before saving.
- **Screenshot editor annotations** — parity with the macOS editor: rectangle, ellipse, arrow,
  line, freehand draw, text, numbered badges, and pixelated redaction, on top of the existing
  crop. Each annotation has a color picker and stroke-thickness selector; annotations can be
  selected, moved, deleted, and undone. Single-key tool shortcuts (V/C/R/O/A/L/D/T/N/B),
  `Ctrl+Z` undo and `Del` to remove the selection. Annotations preview live as XAML shapes and
  are baked into the image at full resolution with Win2D so the saved/copied PNG or JPEG matches
  the preview exactly.
- **Audio recording for video** — microphone and/or system ("desktop"/loopback) audio is now
  captured via WASAPI (NAudio), mixed and resampled to 48 kHz / 16-bit stereo, and muxed into the
  recorded MP4 as an AAC track. Honors the existing **Record system audio** / **Record microphone**
  toggles and the microphone device picker; each source is best-effort (a denied mic still records
  system audio, and vice-versa). GIFs remain silent. Adds the `microphone` device capability to the
  package manifest. *(A/V sync needs an on-hardware listen test — cannot be validated in CI.)*
- **Copy video / GIF to clipboard** settings — recorded MP4s and GIFs can now be copied to the
  clipboard (as a file) automatically after capture, alongside the existing screenshot copy
  (which also places the bitmap for direct paste). Toggles added to the Video and GIF sections.
- **Mouse-click visual overlays** in video and GIF recordings: a global low-level mouse hook
  (`MouseClickMonitor`) records click timing/position, and `MouseClickOverlayCompositor` draws
  expanding, fading pulse rings into each captured frame (parity with the macOS
  `MouseClickOverlayProcessor`). Honors the per-type enable toggle and is gated to monitor/region
  targets (window targets are skipped, matching the mac restriction).
- Mouse-click **highlight color** setting with a live preview swatch in the Mouse Clicks section.
- Region / Screen / Window **capture picker** shown before each capture, with `R` / `S` / `W`
  shortcuts and an inline pre-capture countdown (parity with the macOS picker).
- Per-window and per-monitor capture targets (`CaptureTarget`) wired through screenshot, video,
  and GIF pipelines.
- First-run **onboarding** wizard and an in-app **Guide** (help) window.
- Settings parity sections: **Mouse Clicks**, **Branding**, and a **Pro** status notice.
- **Pro feature gating** (`IEntitlementService` / `ProFeature`) for the direct build; mouse-click
  visuals, branding overlay, and upload are gated and surface an upsell when locked.
- `windows/docs/dpi-and-coordinates.md` documenting the pixel-vs-DIP capture strategy.
- **Screenshot editor** that opens after each screenshot (toggleable): drag-to-crop with
  apply/reset, copy to clipboard, save (overwrite), and save-a-copy.
- **Video trimmer** with a preview player and start/end range sliders that renders a trimmed
  `(trimmed)` MP4 via `MediaComposition`.
- **GIF trimmer** that drops leading/trailing frames and re-encodes a `(trimmed)` GIF with
  preserved per-frame delays.
- Settings toggles to open the editor / trimmers automatically after capture
  (**Screenshot**, **Video**, **GIF** sections).
- Dedicated stop-recording hotkey (`Ctrl+Shift+S`) shown in the tray menu, recording indicator,
  and Guide.
- Region countdown indicator that outlines the selected capture region until recording or
  screenshot capture begins.
- **Recording indicator** — a floating always-on-top panel shown while recording video or GIF,
  with a live `MM:SS` elapsed timer, the stop hotkey, and a **Stop** button.
- **Launch at login** — optional setting that starts TinyClips when you sign in to Windows
  (via the `HKCU\...\Run` registry key).

### Changed
- **Screenshot editor toolbar** is now cleaner and contextual — the stroke-thickness, number-size,
  and redaction-strength controls show only for the tools they apply to, and the stroke widths now
  match the macOS app's **1 / 2 / 4 / 6 / 8 / 10 px** options.
- **Settings** is now organized into a left **NavigationView** with one section per group
  (General, Screenshot, Video, GIF, Mouse Clicks, Branding, Hotkeys, Pro).
- **Pro features are unlocked** in the direct (non-Store) build, matching the macOS direct
  distribution; the Store build will gate them via a StoreContext-backed entitlement service.
- **Region selector** now shows a live snapshot of the screen behind a hole-punch dim, so the
  area being captured stays clear and fully visible (instead of dimming the whole screen).
- **Screen** and **Window** pickers are now compact, centered dialogs that leave the rest
  of the desktop visible rather than graying out the entire display.
- The capture picker, the pickers, and the countdown now use a translucent **acrylic** backdrop
  so the desktop shows through; the **countdown** is a smaller circle.
- The **capture picker** and the **recording indicator** can be dragged to reposition them.
- New **app icon** (512px base + refreshed MSIX tiles) recreating the viewfinder mark crisply.
- **Trimmers** redesigned with a cleaner preview / trim-range / footer layout and a **Speed**
  control (GIF output speed is applied to frame delays; video speed currently affects preview).
- A new **Reopen capture picker after each capture** setting re-shows the picker when a capture
  finishes.
- The **screenshot editor and video/GIF trimmers** now open maximized (full screen) for more
  working room.

### Fixed
- **Screenshot editor text entry** — clicking to place a text annotation no longer immediately
  dismisses the text box. Focus is now deferred past the pointer interaction and the transient
  focus-loss is ignored, so you can type; **Enter** commits and **Esc** cancels.
- **Countdown lingered in recordings** — the countdown badge now hides itself before the final
  frame and is excluded from screen capture, so it no longer appears in the recorded video/GIF and
  no longer hangs at "1".
- **Countdown styling** — redesigned as a clean circular badge (acrylic, clipped to a true circle
  with a thin accent ring) instead of the previous "box in a box" look.
- **Recording region outline** is now a bright, thicker red so it is clearly visible while
  recording a region.
- **Tray menu was clipped on its first open** — the SecondWindow context menu is now
  warmed up invisibly at startup (DWM-cloaked) so the first menu is measured at the correct
  display scale instead of being cut off at the bottom.
- **Drag jitter** when moving the capture picker and recording indicator — dragging is now
  cursor-anchored, so the windows follow the pointer smoothly instead of jumping.
- **Screenshot editor crash** — removed a reference to a nonexistent WinUI resource key
  (`AccentFillColorSelectedContentBackgroundBrush`) that threw during XAML parse and silently
  prevented the editor from opening; opening is also now wrapped with a reveal/toast fallback.
- **Countdown** is now a compact rounded square instead of a large background panel.
- **Region outline is now hollow** (a punched-out frame) so the content being recorded is
  visible through the middle.
- **Recorded MP4 was vertically flipped** — video frames are now written with the correct
  top-down orientation (the GIF path was already correct).
- **Screenshot editor** now reliably opens and comes to the foreground after a screenshot.
- The screen is **no longer dimmed** between finishing a region selection and the recording
  starting for video/GIF.
- A **region outline** now stays visible (click-through and excluded from capture) while
  recording a region, and the outline is drawn just outside the captured area.
- The **recording indicator** is excluded from capture so it no longer appears in recordings.
- Quitting the app while a **GIF** recording is active now finalizes the GIF instead of
  abandoning it, and the exit path no longer blocks the UI thread.
- Launch-at-login registry value is now **quoted** so executable paths with spaces work.
- Hotkey labels now render punctuation/symbol keys (e.g. `-`, `=`, `,`) instead of `?`.

### Removed
- The redundant **Capture Region** item from the tray menu; region capture is still available
  via the **Capture Screenshot** flow's picker (`R`).
- The **Clips Manager** library window (and its `ClipTile` view-model) for now; captures still
  save to the configured output folders and surface via Explorer + save toasts.

### Notes
- Captures are recorded to the configured output folders and surfaced by save toasts /
  reveal-in-Explorer — no separate database to keep in sync.
- Real-time mouse-click & branding compositing, microphone/system-audio muxing, and MSIX/Store
  packaging are **not yet implemented** in this port.

## [0.1.0] — Phase 1 capture core

### Added
- Tray-only WinUI 3 app with a Fluent menu, light/dark/system theming, and a custom tray icon.
- Screenshot (PNG/JPEG), drag-selected region capture, H.264 MP4 video, and animated GIF.
- Global hotkeys (`Ctrl+Shift+5/6/7`), pre-capture countdown, and save toast notifications.
- Native Settings window (General / Screenshot / Video / GIF / Shortcuts).