# Changelog

All notable changes to this project will be documented in this file.

## Unreleased

### Added
- Screenshot editor gains an **Emoji** tool. Pick from a categorized palette (plus a Recent row and a "type or paste" field for any emoji), click to drop a sticker, then use the Move tool to drag it, resize it from any corner (aspect-locked about its center), and rotate it with the new grip above the sticker — hold Shift to snap to 15° steps. A rotation slider in the inspector offers precise control, and stickers export with their rotation baked into the saved image.

## v1.7.1.0-mac - 2026-08-24

### Added
- macOS adds dedicated Screenshot Region (⌃⌥⌘1) and Screenshot Window (⌃⌥⌘2) global hotkeys that skip the capture picker and go straight to region or window selection, similar to macOS's built-in ⇧⌘4 shortcuts. Rebind or clear them anytime in Settings > Shortcuts.

### Fixed
- Fixed macOS screenshot editor exports showing a light hairline around the screenshot card when centered in a preset frame (for example 16:9 or 9:16) with an odd amount of leftover space. The card origin could land on a half pixel, anti-aliasing its edge; on a transparent background exported as JPEG that edge flattened to a visible line. Export layouts are now snapped to whole pixels while the live preview keeps fractional geometry.

## v1.6.1-mac - 2026-08-18

### Fixed
- Fixed macOS region screenshots being slightly soft. ScreenCaptureKit resamples the frame when cropping via `sourceRect`, even when the crop is pixel-aligned, so region screenshots now capture the display at native resolution and crop losslessly instead. This also applies to Copy Text from Region.
- Fixed macOS region selections being cropped on fractional pixel boundaries, so saved dimensions now match the size shown while dragging. Window screenshots and capture buffers are also sized from ScreenCaptureKit's own point-to-pixel ratio rather than `NSScreen.backingScaleFactor`.

## v1.6.0-mac - 2026-08-13

### Changed
- The macOS screenshot editor now supports 25%–400% fit-relative zoom with accessible controls, percentage presets, Command shortcuts, focal-point pinch or Command-wheel zoom, scrollbars, trackpad panning, and Space-drag navigation without changing exported image pixels.
- The macOS screenshot editor now uses a labeled 3×3 image-alignment grid with every export frame placement combination directly selectable.
- macOS now persists the concrete per-type capture folder defaults, so custom-folder settings are populated with Pictures/TinyClips or Movies/TinyClips before a folder is selected.
- macOS capture folders now default to Pictures/TinyClips for screenshots and Movies/TinyClips for videos and GIFs. Turning off "Use default folders" reveals separate folders for screenshots, videos, and GIFs.
- macOS teleprompter preview now starts only on demand with a reliable Start/Stop control, a speed slider that supports 0–100 pt/s in 1 pt/s increments with a larger interaction target, small/medium/large text-size and panel-height presets, and plain-text transcript file loading up to 1 MB.
- Updated macOS in-app Terms of Use links to open Apple's Standard EULA directly.
- macOS capture settings now offer matching before/after picker controls for screenshots, video recordings, and GIF recordings.
- macOS multi-monitor capture settings now let you choose to ask every time, capture the display under the cursor, or capture the main display.
- macOS and Windows CI workflows now always report a successful check when their platform-specific files are unchanged, while skipping the unnecessary build work.
- macOS Video and GIF settings now let you disable the default behavior that keeps the display awake while recording.

### Fixed
- Fixed macOS screenshot editor crops to apply immediately and rebase the annotation canvas, so annotations added after cropping retain their intended position and size. Applying a crop now flattens existing annotations into the image, matching Windows.
- Fixed macOS scrolling capture stitching duplicated rows of content by aligning frames with per-row signatures at single-pixel resolution and preferring the smallest matching scroll distance, which also handles slow scrolls and repeating layouts such as card lists.
- Fixed the macOS scrolling capture not showing the red region outline while it records.
- Fixed macOS scrolling capture running out of memory on modest regions by stitching frames incrementally, and guardrails now stop and save the panorama captured so far instead of discarding it.
- Fixed the macOS scrolling capture control panel to match the other capture bars, with a live frame count and enough room for its status text.
- Fixed the macOS teleprompter settings preview blocking its Stop button and other controls while the transcript scrolls, and ensured scrolling stops when leaving Teleprompter settings.
- Fixed the macOS teleprompter preview viewport height and VoiceOver scrolling-status announcement.
- Fixed the macOS and Windows settings sidebars to keep Video and Teleprompter as separate entries, cleaned up the Support label wording, and prevented the screenshot editor’s Horizontal/Vertical alignment labels from being clipped when the window is narrow.
- Fixed macOS capture actions staying disabled after canceling a capture picker, target selection, or recording setup, and after a pre-capture countdown completes.
- Fixed macOS recordings that capture no frames leaving zero-byte MP4 artifacts.
- Fixed the macOS video trimmer so preview playback stops immediately when saving or closing the trimmer.
- Fixed macOS launching a second Tiny Clips process by activating the existing instance instead of duplicating the menu-bar app and global hotkeys.
- Fixed macOS recording writer failures caused by invalid or non-increasing screen, system-audio, microphone, or webcam presentation timestamps.
- Fixed macOS video and GIF recordings allowing the display to idle-sleep or start the screen saver during capture.
- Fixed macOS screenshot editor text annotations previewing larger than copied or saved images when background padding changes the displayed screenshot scale.
- Fixed macOS shortcut changes silently accepting unavailable or conflicting global hotkeys; Settings now validates conflicts and keeps the prior working shortcut when registration fails.
- Fixed macOS video and GIF recordings leaving capture controls active after ScreenCaptureKit unexpectedly stops a stream; recordings now save available partial frames and explain how to recover.

### Added
- Added macOS vertical scrolling capture for selected regions, with bounded ScreenCaptureKit sampling, duplicate-frame rejection, automatic stitching, keyboard stop/cancel controls, and existing screenshot editor/save integration.
- Added deterministic macOS unit-test coverage for capture and Retina coordinate math, recording pause timelines, settings and hotkeys, save-file naming, and capture analytics.
- Added a macOS teleprompter overlay for video recordings: configure it from the dedicated Settings → Video → Teleprompter screen, preview the selected scroll speed, and read from an auto-scrolling, draggable, never-captured panel with a remembered position.
- Added macOS OCR region capture to recognize selected screen text and copy it to the clipboard.
- Video recording controls now offer session-only microphone and system-audio mute buttons for sources that started with the recording.
- macOS microphone recording now applies a default-on soft-knee limiter that prevents loud peaks from hard-clipping before AAC encoding.
- macOS video recording now supports Continuity Camera devices and optional wind-noise removal for supported microphones.
- macOS General settings now support separate screenshot and video/GIF save folders while preserving the shared folder as a fallback.
- macOS screenshot editor exports now offer Original, 1:1, 4:3, 16:9, 3:4, and 9:16 frames plus horizontal and vertical alignment, with image pixels and annotations preserved without stretching.
- macOS screenshot clipboard copies now publish both PNG and TIFF image representations for wider app compatibility.

## v1.5.4-mac - 2026-07-26

### Added
- Added menu-bar shortcuts for opening capture folders and reopening the 10 most recent screenshots, videos, and GIFs in their editor or trimmer.
- Added four-corner resize handles to selected screenshot annotations while preserving move dragging and arrow/line endpoint editing.

### Fixed
- Fixed the macOS screenshot picker reopening before the screenshot editor is closed.

## v1.5.3-mac - 2026-07-25

### Added
- Added a live webcam preview before and during macOS video recording; drag it between corners and the exported video preserves each position change.
- Added a new macOS Settings → Analytics view that tracks daily screenshot, video, and GIF capture counts locally, shows rolling 7-day or 30-day bar charts, and lets you reset the stored history.
- Extended macOS capture analytics with lifetime (all-time) totals per capture type, a per-type series toggle to show/hide screenshots/videos/GIFs on the chart, hover tooltips with exact daily counts, a "busiest day of week" and "most active hour" insights breakdown, and Copy Summary / Share buttons for a quick text summary of your capture activity.
- Added a **Remove audio** option to the macOS video trimmer so exports can deliberately omit the audio track while preserving audio by default.
- Added polished countdown fades plus pause, resume, restart, discard, and stop controls to the macOS recording overlay.
- Added a cross-platform in-app **File a Bug…** flow: macOS menu bar + Settings → About and Windows tray popup + Settings → About now open a quick two-field bug form (title + what happened) and launch a pre-filled GitHub issue using a new lightweight quick bug template.

### Fixed
- Removed the macOS New Window command for Tiny Clips windows and wired the video/GIF trimmers' existing frame-copy action into Edit → Copy and Command-C.
- Fixed macOS screenshot editor Edit menu commands so Undo, Redo, Copy, and Clear Annotations are available from the standard menu and invoke the canvas actions.
- Fixed macOS capture workflows from leaving unmanaged temporary files behind; stale TinyClips files, including webcam companions, are now cleared after 24 hours, failed video overlay outputs are removed, and Settings → General → Advanced provides controls to open or purge the TinyClips temp folder.
- Fixed the macOS Settings → Analytics view freezing with "Publishing changes from within view updates is not allowed" faults, caused by the analytics history being pruned (and its published state reassigned) while the view was rendering. Pruning now runs only at launch and when a capture is recorded.
- Fixed the macOS pre-capture countdown number transition so each tick now animates smoothly instead of rebuilding the SwiftUI hosting view every second (which prevented the numeric text transition from running).
- Fixed macOS screenshot editor exports so curved arrows keep their preview-aligned curve direction and position when background padding is applied.
- Fixed canceling a pre-recording countdown with the stop hotkey leaving the region indicator on screen.

### Improved
- Added a "Download the Latest Version" link to the macOS Settings → About section (Direct Download builds) so that when the in-app Sparkle update check fails with "An error occurred in retrieving update information", users always have a reliable path to update by downloading the newest release directly from GitHub.
- Moved the macOS branding overlay toggle into a new Settings → Branding section to match the Windows settings organization.
- Screenshot editor saves now use a document-style flow on macOS: Save overwrites the current file, Save As creates a new file, Open Folder reveals the active save destination, and Close dismisses the editor without forcing an export.
- macOS Settings → About now detects when a direct-download build is running outside the Applications folder and gently points users there first, reducing the extra permission prompts Sparkle may need during updates.
- The macOS menu-bar **Check for Updates…** command now opens Settings directly to **About** first, so direct-download builds outside Applications see the smoother-update guidance before Sparkle prompts for extra permission.
- Added SF Symbol icons to each action in the macOS menu bar menu so capture and app commands are easier to scan at a glance.
- macOS screenshot editor image-corner and shadow controls now apply to the actual screenshot content instead of the padded export background, matching the editor preview and saved output.
- macOS screenshot editor color controls (stroke, fill, text, number badge, and background) now show common preset color swatches first with a **Custom…** option that opens the full native color picker, so picking a common color is a single click while full precision stays available.
- macOS screenshot editor color controls are now compact dropdowns that preview the current color and its name; the shape **Fill** control adds a **None** option so rectangles and circles can be left unfilled.

## v1.5.1-mac - 2026-07-03

### Improved
- Screenshot editor background settings now apply the corner-radius control to the screenshot content itself (not just the background frame), so rounded image corners are reflected in both preview and exported files.

## v1.5.0-mac - 2026-06-30

### Changed
- **macOS in-app purchase model:** Refactored from feature-gated Pro subscription to all-features-free model with optional Pro "tip" support. All previously Pro-only features (batch actions, clip organization/tagging, editing, mouse click effects, Uploadcare uploads) are now available to all users. Users can optionally tip via monthly/yearly subscription or one-time Pro purchase to show their support; Pro supporters see a "Pro Supporter" badge in settings. This emphasizes the app-first experience while allowing users to contribute.
- **macOS Pro gating removed:** Removed Pro checks from mouse clicks settings, video/GIF settings sections, Clips Manager toolbar buttons (Select/Settings), Clips Manager content editing, and start recording panel mouse click toggle. Renamed `StoreService.isPro` to `hasProTip` to reflect badge-only status.

### Added
- Added a capture-device catalog layer for macOS that now includes webcam discovery (built-in wide-angle, external, Continuity Camera, and Desk View where available) with stable IDs, user-friendly stable sorting, and reusable connect/disconnect/interruption notification hooks.
- Added macOS webcam setup controls in recording start and video settings (enable toggle, device picker, shape/corner/size presets), plus start-panel plumbing so webcam selections flow into the video capture start path. Enabling webcam now auto-enables microphone by default (still manually overridable).
- Added macOS export compositing support for recorded webcam artifacts, including corner placement, size presets, and shape masking (circle, rounded rectangle, rectangle) merged into the existing video post-processing pipeline alongside branding overlays.
- Added macOS Finder "Open With Tiny Clips" support for image files so opening PNG/JPG/JPEG (plus HEIC/WebP when decodable by the current macOS runtime) launches directly into the screenshot editor.

### Improved
- macOS now requests microphone and camera permission the moment you enable the mic or webcam in the start recording panel (and pre-warms them for already-enabled inputs), so the system prompt no longer interrupts the countdown or delays capture. If access was previously denied, the app opens the relevant System Settings pane and automatically re-enables the toggle once you return with permission granted.
- Streamlined the macOS start recording panel by placing the microphone picker next to the mic toggle and moving webcam device/shape/corner/size choices into a compact settings popup.
- Moved the macOS video recording time-limit picker to the initial capture picker next to the countdown control.
- Reorganized macOS Video settings into clearer groups (Video Quality, Audio, Webcam Overlay, and Effects), and made webcam shape/corner/size settings configurable without first enabling the webcam overlay toggle.

### Fixed
- Fixed macOS webcam overlay audio sync: the webcam track is now aligned to the screen/audio timeline using each source's first-frame timestamp, correcting drift caused by camera warm-up delivering its first frame later than ScreenCaptureKit. The leading screen-only/silent segment recorded while the camera and microphone warm up is now trimmed so the exported clip begins once everything is rolling.
- Fixed macOS webcam overlay exports so circular overlays keep a square crop instead of stretching the camera aspect ratio, and so the webcam track is clipped instead of the screen recording track.

## v1.4.1.0 - 2026-06-08

### Improved
- Reorganized the repository so the macOS Xcode project lives under `mac/` (alongside the existing `windows/` WinUI port); updated CI/CD workflows, VS Code tasks, validation hooks, and documentation accordingly. History preserved via `git mv`.
- Redesigned the screenshot editor with a left flyout for tools, style/background controls, and export actions; added configurable canvas padding/background options and curved arrow styles.

### Added
- "Captured on Tiny Clips" branding overlay: a global setting (off by default) that burns a semi-transparent watermark into the bottom-right corner of screenshots, video recordings, and GIFs.
- Recommendation link for installing ClickLight was added to mouse click settings for enhanced click animation visuals.
- **Windows (WinUI 3) port — Phase 1 capture core:** native system-tray app with screenshot and drag-to-select region capture, hardware-accelerated H.264 MP4 video recording, animated GIF recording, app-wide global hotkeys (Ctrl+Shift+5/6/7), pre-capture countdown, save toast notifications, and a Fluent Settings window. Plus winget packaging templates and a packaging guide.

### Fixed
- Start recording panel now falls back to the system default microphone when the previously selected device is no longer connected, and clears the stale saved selection.
- Video trimmer "Save Without Trimming" and "Save Trimmed" actions now route through `CaptureManager`'s completion callback, ensuring consistent post-save handling (notifications, Finder reveal, uploads). The trimmed export callback is dispatched on the main queue for AppKit safety.
- GIF trimmer "Save Trimmed" now routes through `CaptureManager`'s completion callback instead of calling `SaveService` directly, preventing duplicate save notifications/uploads when the GIF was already saved immediately.

## v1.4.0.3 - 2026-05-27

### Added
- Release pipeline automation for Homebrew cask version and SHA updates.

## v1.4.0.2 - 2026-05-23

### Added
- Video recording start controls now include a time-limit picker (default: Unlimited) that can automatically stop recording after the selected number of minutes.
- Homebrew cask support with release pipeline automation to keep `Casks/tiny-clips.rb` version and SHA in sync on each tagged release.

## v1.4.0.1 - 2026-05-17

### Fixed
- Keyboard overlay processing now emits staged progress updates to the processing indicator window.
- Modernized AVFoundation APIs in mouse click overlay processor: replaced deprecated `tracks(withMediaType:)`, property access, and `exportAsynchronously` with async/await equivalents (`loadTracks`, `load(.duration)`, etc.).
- Deprecated AVAssetImageGenerator API in video trimmer window updated to use `generateCGImageAsynchronously(for:)`.
- Eliminated unused result warnings in trimmer frame-save operations.
- Resolved "will never be executed" warning in StartRecordingPanel by moving target-conditional logic into compile-time branches.

## v1.4.0 - 2026-05-17

### Fixed
- Window video and GIF recording now use the selected window bounds with the existing region recording path.
- Processing indicator after recording stops now reliably appears and animates. Replaced the custom spinner (which failed to animate in borderless `NSPanel` + `NSHostingView`) with the system `ProgressView`. Added a spring fade-in entrance and a smooth window-level fade-in via `NSAnimationContext`.
- Eliminated the blank gap between the processing indicator dismissing and the trimmer window appearing by moving `dismissProcessingIndicator()` to after `showTrimmer`/`showGifTrimmer` is called.

## v1.3.4 - 2026-04-05

### Improved
- Redaction blur handling and performance optimizations.
- Rename of blur-related properties and methods for pixelation functionality clarity.

## v1.3.3 - 2026-04-05

### Added
- Guidelines for capture-time window and panel conventions in documentation.
- Redaction blur presets and blur strength control in ScreenshotEditorView.
- Dynamic color and size controls for annotations in ScreenshotEditorView.
- Enhanced number text color controls in ScreenshotEditorView.

### Improved
- Project guidelines for clarity and consistency in architecture and conventions.
- Purchase restoration alert display.
- Shortcut display in GuideWindow.

### Fixed
- Missing comma in points array for arrow and line tools in EditorViewModel.

## v1.3.2 - 2026-04-04

### Added
- VoiceOver capture announcements for screenshot, video, and GIF lifecycle events providing spoken feedback for capture start, recording stop, save success, and save errors.

## v1.3.1 - 2026-03-28

### Improved
- Increased default width of screenshot editor window for better usability and editing space.

## v1.3.0 - 2026-03-28

### Added
- Require changelog update hook to enforce changelog entries on each commit.
- Independent "show capture picker" settings for each capture type (screenshot, video, GIF).
- Number annotation tool to screenshot editor for adding numbered callouts.
- Customizable keyboard shortcuts for capture actions.
- Fill color option to image editor rectangle and circle shapes.
- Privacy Policy and Terms of Use links in About section of Settings.

### Improved
- Encapsulated countdown properties in CapturePickerState for better state management.
- Extracted number tool constants and improved export path font alignment in code review.
- App Store metadata and legal links for compliance.

### Fixed
- Ambiguous EventModifiers by qualifying as SwiftUI.EventModifiers.
- Settings window now opens before checkForUpdates from menu bar to prevent Sparkle "Update failed" error.

## v0.0.23 - 2026-03-17

### Improved
- RegionIndicatorView now has cleaner appearance with background removed.

## v0.0.22 - 2026-03-17

### Added
- CONTRIBUTING.md guide for new contributors.

### Fixed
- Button out of frame on first setup.
- True pixelation for redact tool so content is fully obscured.

## v0.0.21 - 2026-03-09

### Improved
- Refactored video recording and clips manager logic for improved state persistence and microphone handling.
- Enhanced microphone state management and recording flow coordination.
- Updated UI settings for better user experience in recording panels.
- Improved copilot instructions documentation.

## v0.0.20 - 2026-03-09

### Added
- Microphone selection and status indicator in audio recording settings.
- Automatic update checks with enhanced release notes in appcast.

### Improved
- Refactored settings window activation logic for improved reliability and behavior when opening from menu bar.
- Refactored CaptureManager methods to streamline recording flow and prepare for new capture requests.
- Audio capture handling with improved microphone functionality and adjustable capture thresholds.
- UI styling enhancements in StartRecordingPanel and StopRecordingPanel for improved appearance.

### Fixed
- Microphone capture robustness improvements addressing interruption handling.

## v0.0.19 - 2026-03-08

### Added
- Subscription management and restore purchase functionality.
- Clips Manager with Uploadcare integration for user uploads.
- Smart default file naming templates with live preview.
- Option to include TinyClips windows in captures.
- Collapsible sidebar toggle in Clips Manager.
- Sidebar filters and tag management in Clips Manager.
- Accessibility enhancements across various views, including keyboard shortcuts and hints.
- Help text and improved issue reporting template in settings.
- Enhanced clipboard options in CaptureSettings.

### Improved
- Dimension display in RegionSelectionView to include both point and pixel values for clarity.
- Capture region handling with pixelWidth and pixelHeight properties for accuracy.
- DPI settings with scaleFactor parameter in saveImage function.
- Pixel dimension calculations for capture region settings in trimmer views.
- Clips Manager UI with collapsible sidebar and improved grid layout stability.
- Settings sections organization and layout improvements.
- Image capture and display logic in ScreenshotCapture and RegionIndicatorPanel.

### Fixed
- Save notifications are now always presented with Finder open on click.
- Grid cell overlap issues in Clips Manager.
- Sidebar toggle no longer shifts with split view state.
- Grid thumbnail overlap issues in Clips Manager.

## v0.0.14 - 2026-02-17

### Added
- Multi-monitor support for region selection and full-screen capture.
- Display picker UI for selecting target screen on multi-monitor setups (full-screen capture mode).
- "Always capture main display" setting in General preferences to bypass display picker if desired.
- Global hotkey functionality for screenshot, video, and GIF recording.

### Improved
- Fixed region selection overlay rendering on secondary displays through corrected coordinate space initialization.
- Enhanced window focus and activation for improved multi-screen usability.
- Improved event handling for escape key cancellation in display picker and region selector.
- Better window management with centralized activation logic.

### Fixed
- Region selection now works correctly on secondary displays.
- Escape key handling in display picker and region selector on menu bar app context.

## v0.0.13 - 2026-02-16

### Added
- Speed control for GIF and video trimming with multiple speed options (0.5x, 0.75x, 1x, 1.1x, 1.25x, 1.5x, 2x).
- Immediate save setting for screenshots and GIFs with option to skip editor.
- Saving state and progress overlay to GIF and screenshot editors.

### Improved
- Enhanced screenshot and GIF saving options with better editor toggle controls.
- GIF and video trimmer speed options and playback speed handling.
- Image rendering and scaling in EditorViewModel for better visual fidelity.
- Trimmer window frame width adjustments for better usability.

### Changed
- Default screenshot format changed from PNG to JPEG for faster saves.

## v0.0.12 - 2026-02-15

### Added
- Full-screen capture override by holding Option when starting Screenshot, Video, or GIF capture.
- New Guide window from the menu bar with usage help and shortcut documentation.

### Improved
- Menu bar capture labels now update live while Option is held to clearly indicate full-screen capture mode.
- Guide UI refreshed with segmented sections, improved spacing, and clearer content grouping.
- Guide window sizing refined to reduce excessive vertical space.
- Video and GIF trimmer windows are now resizable for larger capture regions.

### Fixed
- Removed fixed-size constraints from Video and GIF trimmer views so window resizing works correctly.

## v0.0.11 - 2026-02-15

### Added
- First-run onboarding wizard for permissions setup.
- Save notification preference in settings (default off).
- Reset all settings to defaults option for easier testing.

### Improved
- Onboarding welcome screen visuals with app icon and clearer guidance.
- Screen Recording step now includes explicit restart guidance.
- Added dedicated re-check action for Screen Recording permission status.

### Fixed
- Avoided potential QoS priority inversion in permission checking.
- Prevented duplicate popups during Screen Recording permission requests.
- Only mark onboarding complete when user explicitly finishes or dismisses.

### Maintenance
- Updated appcast for release metadata.

## v0.0.10 - 2026-02-14

### Added
- Mac App Store variant (`TinyClipsMAS`) from the same codebase.
- App Store-related documentation and project setup guidance.

### Improved
- Editor image handling and output flow refinements.
- Video trimming and timeline behavior improvements.
- Better main-thread handling around file panels and UI operations.

### Fixed
- Added `ITSAppUsesNonExemptEncryption` where required.
- Corrected plist path/signing-related project configuration issues.

### Maintenance
- Removed obsolete CI workflows and refreshed docs.

## v0.0.9 - 2026-02-14

### Added
- Countdown before Video and GIF recording.
- Release workflow step to generate changelog content.

### Improved
- Screenshot editor bottom bar layout and organization.

## v0.0.8 - 2026-02-13

### Added
- Screenshot format selection (PNG/JPEG), scale, and JPEG quality settings.
- Additional entitlement updates to support distribution/security requirements.

### Maintenance
- Updated appcast for release metadata.