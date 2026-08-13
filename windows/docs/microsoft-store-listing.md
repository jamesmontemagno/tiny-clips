# Tiny Clips for Windows - Microsoft Store Listing

Use this file as the source of truth for the Microsoft Store listing and first submission of the
Windows app. It complements the macOS-only `docs/app-store-connect-metadata.md`.

## App identity

| Field | Value |
| --- | --- |
| Product name | Tiny Clips |
| Package identity | `23875RefractoredLLC.TinyClips` |
| Package family name | `23875RefractoredLLC.TinyClips_jcspp7mzn01xr` |
| Publisher | Refractored LLC |
| Primary category | Photo & video |
| Secondary category | Productivity |
| Privacy policy | https://tinyclips.app/privacy.html |
| Support URL | https://github.com/jamesmontemagno/tiny-clips/issues |
| Marketing URL | https://github.com/jamesmontemagno/tiny-clips |
| Copyright | Copyright (c) 2026 Refractored LLC |

## en-US listing copy

### Short description

Fast screenshots, video recordings, and GIFs from your Windows system tray.

### Description

Tiny Clips is a lightweight Windows screen-capture tool that is always ready from the system tray.

Capture exactly what you need:

- Screenshots of a region, screen, or window
- Screen recordings as H.264 MP4 video
- Short animated GIF captures

Built for speed and focus:

- Keyboard shortcuts for screenshot, video, GIF, and stop-recording actions
- Optional countdown and region outline before capture
- Microphone, system-audio, webcam, mouse-click, and branding-overlay controls
- Screenshot editing plus video and GIF trimming after capture
- A Clips Library for browsing, opening, copying, and sharing saved captures
- Configurable folders, formats, quality, themes, notifications, and launch-at-login

Tiny Clips is designed for developers, creators, and anyone who needs to quickly create and share
visual context without a complicated workflow.

### Search terms

screen capture,screen recorder,screenshot,video recorder,gif recorder,screen recording,productivity

### Release notes

Copy the matching version section from `windows/CHANGELOG.md`. Keep the Store "What's new" text
focused on user-visible changes and omit internal refactoring notes.

## Screenshot set

Store screenshots must be captured from the released Windows package, with no development tools,
desktop clutter, or unrelated personal content visible. Capture at least 1366 x 768 pixels;
1920 x 1080 is preferred. Use the same light or dark theme throughout the first set.

| File | Required content | Capture notes |
| --- | --- | --- |
| `01-tray-capture-menu.png` | Tray popup with Screenshot, Video, and GIF actions | Open the tray popup against a clean desktop. |
| `02-region-picker.png` | Region / Screen / Window picker | Show the target-selection options and countdown control. |
| `03-screenshot-editor.png` | Screenshot editor with a small, realistic annotation | Use non-sensitive sample content. |
| `04-video-recording.png` | Recording indicator and selected-region outline | Show elapsed time and the Stop action. |
| `05-video-trimmer.png` | Video trimmer after a recording | Include the trim range and playback controls. |
| `06-clips-library.png` | Clips Library grid or list | Show several non-sensitive captures and visible actions. |
| `07-settings.png` | Settings with capture options | Show the native Windows styling and useful controls. |

Use `windows/docs/store-assets/` as the working directory for the exported PNGs. Do not commit
screenshots containing personal data, API keys, file paths, or third-party content without rights.

## Store assets

The package logo assets are already in `windows/src/TinyClips.App/Assets/`. Upload the Store-logo
variants Partner Center requests and verify they render correctly in both light and dark contexts.
The Store listing screenshots are separate from the package tile assets.

## Compliance answers

### Privacy

- Data collection: No. Tiny Clips stores capture preferences and optional local analytics on the
  device. It does not use an account, advertising ID, or telemetry service.
- Tracking: No.
- Screen, microphone, webcam, and system-audio content are captured only after the user starts a
  recording. Captures are saved locally unless the user configures their own Uploadcare account.

Recheck these statements whenever analytics, account, cloud, or crash-reporting behavior changes.

### Capability justifications

**runFullTrust:** Tiny Clips is a desktop screen-capture utility. Full trust is required for Windows
Graphics Capture, WASAPI system-audio capture, global keyboard shortcuts, tray integration,
desktop capture overlays, and saving user-selected capture files.

**microphone:** Tiny Clips records microphone audio only when the user explicitly enables it for a
video capture. Audio is written into that local recording and is never collected by the app.

**webcam:** Tiny Clips includes an optional picture-in-picture webcam overlay. Camera access occurs
only when the user enables the webcam for a video capture.

### Ratings and review notes

- Complete the IARC questionnaire truthfully. Tiny Clips has no user-generated public content,
  advertising, gambling, mature content, or unrestricted web browsing.
- The app starts in the system tray and has no main window at launch.
- No account, login, or in-app purchase is required.
- Reviewers should test Screenshot, Record Video, and Record GIF from the tray popup. Screen
  capture permissions are requested by Windows when required; microphone and webcam are optional.

## First submission and CI/CD setup

1. Complete every Partner Center listing, privacy, capability, age-rating, and pricing section,
   then manually submit the first package for certification. The Microsoft Store Developer CLI
   supports automated updates only after the free product is live.
2. Create a Microsoft Entra application, add it under Partner Center **User management** with the
   **Manager** role, and add these GitHub environment secrets to the protected
   `microsoft-store` environment:
   `PARTNER_CENTER_TENANT_ID`, `PARTNER_CENTER_SELLER_ID`, `PARTNER_CENTER_CLIENT_ID`, and
   `PARTNER_CENTER_CLIENT_SECRET`.
3. Add the Store Product ID as the repository variable `MICROSOFT_STORE_PRODUCT_ID`. It is not the
   package identity or package family name; retrieve it with `msstore apps list` after configuring
   the CLI.
4. Protect the `microsoft-store` environment with required reviewers. The
   `windows-store-publish.yml` workflow packages every `v*-windows` tag and pauses before
   submission. A manual dispatch can package without publishing, or publish after approval.

The workflow builds the Store flavor with its Store-assigned identity and submits the unsigned
`.msixupload`; Microsoft Store signs the package and delivers updates.
