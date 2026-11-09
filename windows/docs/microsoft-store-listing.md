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

Every feature is free. Optional supporter plans (monthly, yearly, or one-time) help fund independent
development and add a Supporter badge in Settings; they do not unlock anything else.

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
- Studio captions are made on the device, and sound is not sent anywhere. The speech model is
  downloaded from Hugging Face the first time someone chooses to generate with it. Nothing else is
  sent or fetched for captions.
- Supporter plans are processed entirely by the Microsoft Store. Tiny Clips reads only which plan
  the signed-in Microsoft account holds, and never sees or stores payment details.

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
- No account or login is required. In-app purchases are optional and unlock no features: the
  Settings → Support page offers supporter plans that add a Supporter badge in Settings.
- Reviewers should test Screenshot, Record Video, and Record GIF from the tray popup. Screen
  capture permissions are requested by Windows when required; microphone and webcam are optional.

## Supporter add-ons

Settings → Support finds its plans by product ID, so create these add-ons in Partner Center with
exactly these IDs. They match the Mac App Store products.

| Product ID | Plan | Product type | Properties |
| --- | --- | --- | --- |
| `com.refractored.tinyclips.pro.monthly` | Monthly supporter | Subscription | 1 month, no trial |
| `com.refractored.tinyclips.pro.yearly` | Yearly supporter | Subscription | 1 year, no trial |
| `com.refractored.tinyclips.pro.lifetime` | One-time supporter | Durable | Product lifetime: Forever |

Before publishing, note what Partner Center will not let you change later:

- A product ID and product type are permanent once published.
- A subscription's billing period and trial are permanent, and its price can be lowered but never
  raised. New add-ons start at **Free**, so set the price first.

Use the supporter wording in each add-on's Store listing: say it adds a Supporter badge and unlocks
no features, and do not call it a donation. Store policy 10.8.2 treats pure donations separately
from Store purchases, and 10.8.4 requires being clear about what is sold.

Publish the add-ons before the app update that shows them; until then the page reports that plans
aren't available. `Windows.Services.Store` has no simulator, so test real purchases through the
`Internal Testing` flight, with a Store-installed copy of the app, covering:

- Buying each plan, and the supporter state and message afterward.
- Canceling the Store dialog, which should say nothing.
- Restoring on a second device or after reinstalling, with the same Microsoft account.
- **Manage subscription**, shown only for monthly and yearly.
- A canceled subscription returning to the plans after its billing period ends.

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
4. Add the comma-separated Partner Center tester group IDs as the repository variable
   `MICROSOFT_STORE_FLIGHT_GROUP_IDS`. The workflow uses these only when it needs to create the
   `Internal Testing` flight; an existing flight and its tester groups are left unchanged.
5. Protect the `microsoft-store` environment with required reviewers. The
   `windows-store-publish.yml` workflow packages every `v*-windows` tag, pauses for approval to
   submit the artifact to `Internal Testing`, then requires a separate approval before submitting
   that same verified artifact to the main Store listing. A manual dispatch can package without
   publishing, or submit to both stages after approval. If Partner Center has an unsubmitted
   portal-created flight draft, it is preserved by default; a manual publish can explicitly opt in
   to replacing only that `PendingCommit` draft.

The workflow builds the Store flavor with its Store-assigned identity and submits the unsigned
`.msixupload`; Microsoft Store signs the package and delivers updates.
