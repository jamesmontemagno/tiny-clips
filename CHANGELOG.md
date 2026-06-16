# Changelog

All notable changes to this project will be documented in this file.

## Unreleased

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
