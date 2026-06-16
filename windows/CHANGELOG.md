# Changelog — Tiny Clips for Windows

All notable changes to the Windows (WinUI 3) port are documented here. The macOS app has its
own `CHANGELOG.md` at the repository root.

## [Unreleased]

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
  `Dependencies.PackageDependencies`, so winget installs the runtime first on any clean machine.
>>>>>>> origin/main
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
