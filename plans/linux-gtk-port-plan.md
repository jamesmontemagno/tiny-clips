# Tiny Clips for Linux — native GNOME port plan

**Status: planned, not shipped.** Build a Rust application for GNOME on Wayland and distribute it as a Flatpak. The first question to answer is whether someone on a clean desktop can capture, annotate, and copy a region, then record, stop, and play back an MP4 with sound. Neither workflow should require a shell extension, a global shortcut, or terminal access. Occasional **10–30 minute video recordings** must work; 30 minutes is a test case, not a cutoff.

## 1. Platform and framework decision

GNOME is the desktop, GTK is its native UI toolkit, and Wayland is the display system. Unlike an X11 application, a Wayland application cannot simply read the desktop or watch other applications’ input. A *portal* asks the desktop to authorize a source; PipeWire delivers the authorized frames. Flatpak packages the application and its dependencies in a sandbox. Changing UI toolkits or shipping a non-Flatpak binary would not remove Wayland's restrictions.

| Option | Fit for Tiny Clips |
|---|---|
| **Rust + [GTK4](https://gtk-rs.org/) + [libadwaita](https://gnome.pages.gitlab.gnome.org/libadwaita/doc/main/)** | **Chosen.** Native GNOME controls, keyboard, accessibility tree and theming. [ashpd](https://docs.rs/ashpd/) covers portals; [GStreamer Rust bindings](https://docs.rs/gstreamer/) cover media. Cost: maintaining a third language and implementing the editor and Studio behavior again. |
| C# + GTK4 via [Gir.Core](https://gircore.github.io/) | Same native widgets and a familiar language. Generated bindings add another compatibility boundary for GTK, async callbacks, and native buffer lifetimes; an alternative if Rust ownership becomes a blocker. |
| [Qt 6](https://doc.qt.io/qt-6/) / QML | Strong native option for a KDE-first product. No capture-permission advantage over GTK; adds a different UI stack and desktop idiom for the chosen GNOME audience. |
| [Avalonia](https://docs.avaloniaui.net/) / [Uno](https://platform.uno/docs/) | C# and custom-rendered controls; Uno could preserve some WinUI patterns. Neither makes Windows capture, audio, or window-management code portable. Favor these if sharing UI code becomes more important than native GNOME controls. |
| [.NET MAUI](https://learn.microsoft.com/dotnet/maui/supported-platforms), Electron, Tauri | Linux is not an official MAUI desktop target. The web shells can call portals, but would replace native widgets with a web UI without addressing the media risks. |

Target **GNOME 48+ on Wayland**, initially x86-64 Ubuntu 26.04 LTS and a supported Fedora Workstation release. Probe actual portal interfaces, source types, cursor modes, audio devices, and encoders rather than inferring them from the distro version. KDE, X11, wlroots desktops, other CPU architectures, and other package formats are outside the initial support claim, not reasons to add private compositor hooks. No Linux Pro/IAP or self-updater is planned; existing platform entitlements do not change.

## 2. What parity means

Match the saved result and the user's ability to finish the task, not the shape of the macOS menu or Windows overlay. The [product principles](../PRODUCT.md), [current Windows feature set](../windows/README.md), and [Studio format](../docs/studio-project-format.md) are the baselines. **Target** below means a planned capability, not one tested on Linux yet.

| Capability | macOS / Windows reference | Linux target or deliberate difference |
|---|---|---|
| Screen, window, region, countdown | ScreenCaptureKit / Windows Graphics Capture, native pickers | Interactive still via [Screenshot](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Screenshot.html); for a specific source, use [ScreenCast](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.ScreenCast.html) and a PipeWire frame. The user chooses a monitor or window in GNOME's picker. Draw region selection **on a frozen frame in our window**, not over other applications. Crop subsequent video frames to that rectangle. |
| Video, pause/resume, restart, save | AVFoundation / Media Foundation; screen, mic and system tracks | GStreamer → H.264 video + mixed audio → seekable MP4. Capture mic and system output separately from screen video. Software encoding must work in the installed Flatpak; hardware is an optimization, not a prerequisite. See §3. |
| Animated GIF and trimmers | Animated capture; video/GIF trim and frame export | Bounded frame sampling, explicit delays/loop/width, palette quantization and incremental GIF writing. Start with a Rust GIF writer and review dependency licenses. Trim to a **new** clip; preserve frame timing. Do not apply the MP4 endurance requirement to GIF. |
| Screenshots, annotations, OCR | Native editors; Vision / Windows OCR | PNG, JPEG, WebP; crop, shapes, arrows, pen, text, emoji, redaction, undo and export. Use the same drawing code for preview and output. Tesseract with declared language data for OCR and sensitive-text suggestions; do not promise identical recognition. |
| Camera, branding and effects | Webcam capture, overlays and separate Studio tracks | [Camera portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.Camera.html) when available. Composite branding/camera into ordinary video, keep camera separate for Studio. Defer person cutout until model redistribution, preview speed and export are proven. |
| Cursor, clicks, zooms | Native pointer samples and click-triggered effects | Request an advertised cursor mode. Metadata may give **stream-local position**, not click or keyboard events; embedded cursor is already in the pixels. No global click/key logging, raw input devices or remote-control portal workaround. Manual zooms remain useful without events; do not invent clicks for Studio. |
| Start, stop, reminders, shortcuts | Menu bar / tray; global hotkeys; floating controls | Single-instance app: launching it again opens a compact capture bar with recording status and **Stop**. Add StatusNotifierItem where a tray host exists, notifications and user-approved [GlobalShortcuts](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html). A stock GNOME desktop need not show a tray; do not require an extension, always-on-top stop panel, region outline, or click-through overlay. |
| Save, share and preferences | Finder / Explorer, clipboard, notifications, launch at login | GTK clipboard; file chooser/document grants and OpenURI; copy/open file instead of assuming a system share sheet. GSettings and libadwaita preferences, accessible first-run permission guidance, Guide and What's New. Request background/autostart only by user choice. |
| Clips Library and upload | Search, thumbnails, tags, notes, favorites, collections, filters | Local SQLite catalog, reindexing, grid/list and GTK popover filtering. Local results first; add optional Uploadcare only with opt-in, credential storage, network permission review and the existing sensitive-action approval. |
| Scrolling screenshot | Capture frames and stitch as content scrolls | Sample an authorized source while the **user or external agent** scrolls. Port overlap/sticky-content detection and size bounds. No synthetic system scrolling. |
| Studio and presenter tools | Editable screen/camera media, events, layout, zoom, timeline, audio, MP4 export | Read and write the shared project format, import existing video and preserve edits when opening a project from another platform. Test layout/time mapping against shared goldens before building playback/export. A teleprompter can be a normal presenter window; do not promise that GNOME will keep it above, or exclude it from, a captured monitor. |

Shared file formats, post-capture editing and local storage are the best parity bets. Picking a source, remembering it, tray presentation, hardware acceleration, and shortcuts are **conditional**. Passive global input monitoring, desktop-wide window enumeration, and guaranteed self-exclusion on a monitor recording are **not v1 capabilities**. For clean footage, prefer a window source or hide the capture bar before recording; make reopening it predictable. Don't claim HDR or protected-content capture based on an SDR desktop test.

## 3. Architecture and long recordings

Proposed `linux/` Cargo workspace (not a port of the Windows .NET assembly):

| Crate | Owns |
|---|---|
| `tinyclips-core` | Source/region types, recording state, file naming, edit operations, Studio serialization/layout/time map and Agent Control policy. Unit-test without a display, microphone or GPU. |
| `tinyclips-platform` | Portal request/session lifetime, PipeWire/GStreamer, audio/camera, file grants, SQLite, OCR and Unix socket. Convert device loss, denied access and disk errors into explicit state transitions. |
| `tinyclips-app` | GTK/libadwaita shell, permission setup, editor and capture bar. UI and automation call the same capture/edit services; the GTK main thread never encodes or waits for disk. |

**Geometry.** Map widget coordinates through the displayed image rectangle (including letterboxing) into the negotiated stream raster. Clamp to bounds, handle stream transforms, and compare the exported crop to known pixels. GNOME's logical monitor size, the stream raster, and physical display pixels need not agree. For agent regions, honor the contract's top-left, display-local **physical pixels** only when the mapping is known; otherwise do not offer that target/mode. Renegotiate or stop explicitly on a resize/disconnect; never silently switch monitor.

**Recording path.** Open the PipeWire remote for the authorized session, negotiate format → bounded queue → crop/convert → optionally composite → H.264 encoder. Probe runtime plugins (e.g. `vah264enc` for VA-API); choose a packaged, redistributable software encoder if the hardware path fails to initialize. Bring microphone and output-monitor sources onto one pipeline clock, resample/mix and encode audio (target AAC). ScreenCast is not a system-audio grant: show which tracks are available and never silently replace system audio with a mic. Keep timestamps monotonic across pause/resume; never block capture waiting for preview or queue frames without a bound.

**Do not risk the entire take at Stop.** Prototype GStreamer's [splitmuxsink](https://gstreamer.freedesktop.org/documentation/multifile/splitmuxsink.html) with closed keyframe-aligned segments in private persistent storage. On stop, finalize and remux completed segments without re-encoding, validate the result, then publish a new file. Delete recovery media only after successful save or deliberate discard. Budget for **both** the segments and final file; on low space stop cleanly where possible. On crash/revocation/finalization failure, offer recovery of completed segments at next launch; the open segment may be incomplete. Agent-initiated destructive actions retain their normal approval requirements.

**Endurance gate.** Record **30 minutes** of changing screen content with mic and system audio from the installed Flatpak on the software path. Take memory/queue samples after warm-up and near the end: usage must plateau rather than grow with duration. Put visible/audible markers near both ends to measure sync and drift; record resolution, fps, drop counts, duration and observed offset with the test result. Reopen the MP4 in a separate player and seek into the final seconds. Separately test pause/resume, portal session closure, process interruption, full disk during finalization and recovery. A playable five-second demo is not a substitute.

## 4. Agent Control is part of each milestone

Use the existing [.NET CLI/MCP server](../agent/README.md), [command schema](../shared/automation/commands.json), [policy cases](../shared/automation/fixtures/policy.json) and [protocol](../docs/agent-integration.md). Do not build a Linux-only public API.

- **Prove host ↔ sandbox first.** Candidate Unix socket: `$XDG_RUNTIME_DIR/app/com.refractored.tinyclips/automation.sock`. Verify reachability from the host CLI in the installed Flatpak. Use a private per-user directory, mode `0600`, same-UID peer credentials, existing line-delimited JSON framing, token pairing and revocation. No network listener. [CLI discovery](../agent/src/TinyClips.Cli/CliEnvironment.cs) currently tries macOS paths on non-Windows hosts: add a Linux candidate without disturbing existing endpoints or the explicit override.
- **Separate human consent from an agent request.** The user selects a source in the app. Advertise only live authorized sources usable without a picker, with ephemeral IDs. A portal restore token is single-use and revocable; trying to restore can **show a picker** if the grant or source is gone. Attempt restoration in a user-initiated flow, not during an unattended command. Never return a synthetic “pick” target. On lost permission return `permission_required` with a reauthorization action; for an unavailable feature omit it from discovery and return `not_supported` on direct calls.
- **Test each new workflow through CLI and MCP in the same change as the UI.** Preserve opt-in, least-privilege scopes, local audit without secrets, approval for upload/delete/discard, and restrictions on input/output paths. Verify a returned path is usable by the host user and resolves to an allowed capture; a sandbox path alone is not proof. Test denied access, expired targets, client disconnect and disabling Agent Control during a session. Extend the shared schema/guide and integration skills when behavior changes, not speculatively in the planning change.

## 5. Delivery and acceptance

| Phase | Deliverable and exit criteria |
|---|---|
| **0 — Installable walking skeleton** | Build a CI Flatpak and install it on clean Ubuntu and Fedora Wayland guests. Prove portal grant/deny/revoke, one PipeWire frame, packaged encoders, mic/output monitor, and host CLI pairing/status. Demonstrate a short seekable MP4. Probe shortcut, tray, cursor metadata, camera and headless-test availability; record failures and keep fallbacks. Do not substitute successful host builds for installed-bundle tests. |
| **1 — First usable release candidate** | Two end-to-end workflows: (1) select region → annotate → copy PNG; (2) select screen/window → record with audio → stop from capture bar → save and play. Include countdown, file destination, minimal settings/onboarding, and corresponding safe agent commands. Pass the 30-minute and recovery gates in §3 before calling video ready. |
| **2 — Editors and recording options** | Remaining screenshot formats/tools, OCR/redaction, GIF capture/timing, video/GIF trim, frame save, webcam/branding. Test preview vs saved pixels and source preservation. Add keyboard and Orca tests as controls land. |
| **3 — Library and integration** | Catalog, filters, metadata, full settings, guides, optional upload, shortcut/background integration and user-visible permission recovery. Test file moves, revoked file grants, destructive approvals and no-tray operation. |
| **4 — Studio and scrolling** | Validate [shared Studio fixtures](../shared/studio/README.md) in Rust; round-trip real project folders and render a short project from each existing platform. Add manual zooms, camera/audio editing, export and compatible agent commands. Test scrolling stitch limits independently of Studio. |
| **5 — Distribution and support** | Submit to Flathub once the app ID and redistribution rights are verified. Publish a tested-desktop/capability matrix with known gaps, install/upgrade/recovery instructions and release artifacts. Gate hardware-accelerated and mixed-monitor claims on physical hardware results. |

## 6. Distributing a build to testers

Start with one **Flatpak** manifest and a GNOME runtime/SDK, using `com.refractored.tinyclips` as the proposed app ID; confirm domain control before publishing. Match the ID in the desktop file, icons and AppStream metadata. Pin the SDK and vendor Cargo dependencies from the lockfile for offline sandbox builds. Check the *installed runtime's* audio/video plugins and licensing of bundled codecs, GIF dependencies, OCR data and models; do not assume either host codecs or a particular runtime extension is present. If a redistributable H.264 + audio path can't pass the test, revisit the packaging choice before claiming MP4 parity.

Use Wayland and portals for source selection, camera, notifications and file selection. Justify any explicit audio socket (including its broader audio-device access) and GPU device access. Prefer a persistent grant to a user-chosen save directory over unrestricted home/host access; no raw input devices, broad session-bus access, or blanket device permissions. Verify [Flatpak permissions](https://docs.flatpak.org/en/latest/sandbox-permissions.html) from the manifest **and** in the installed app.

CI should export a `.flatpak` bundle for review before any Flathub submission. Download it **in the guest**, enable the runtime remote if needed, and install as the test user. Example once the artifact exists:

```sh
flatpak remote-add --user --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
flatpak install --user ./TinyClips.flatpak
flatpak run com.refractored.tinyclips
```

Include runtime requirements and a checksum with the bundle. For release, distribute updates through the Flatpak remote/software center; a standalone tester bundle is not itself an update channel. Defer Snap (second store/confinement model), AppImage (separate update/dependency path), and `.deb`/`.rpm` (distro-specific maintenance).

## 7. Verification and repository integration

| Where | What it can prove |
|---|---|
| Pull requests / Linux runner | Cargo formatting, Clippy, deterministic core tests (including shared policy and Studio fixtures), fixture-based media tests using synthetic video/audio, and a Flatpak build. Headless GNOME/Wayland smoke if the portal backend can be exercised; mocks/Xvfb do not establish consent or live capture behavior. |
| Hyper-V from Windows | Use desktop ISOs, **basic console session**, and verify `echo "$XDG_SESSION_TYPE"` gives `wayland`; RDP/enhanced sessions and WSLg are not equivalent. Snapshot a clean guest, install the artifact, test both workflows, denial/regrant, relaunch-to-stop without tray/shortcuts, fractional scale, clipboard, keyboard/Orca, and upgrade with clips retained. Run the endurance test on demand/release, not on every PR. If a guest lacks a mic, feed a known virtual input for the pipeline test and record the real-device case as unverified. |
| Physical GNOME machine before hardware claims | Actual mic/webcam, Intel/AMD VA-API or other advertised encoder, driver fallback, cross-display fractional scaling, output-device changes and suspend. Seek volunteer hardware results if only Hyper-V is available; mark untested combinations as such. |

When implementation starts, add Linux build/run instructions and changelog under `linux/`, platform routing for contributors, a scoped Linux CI workflow, and agent-integration examples. Add Linux to the parity audit and release/tag tooling once there is an artifact to release. Keep the current macOS/Windows builds and shared contracts compatible. The first go/no-go decision is the **packaged capture loop**, not completion of the editor backlog: if it fails on a clean GNOME guest, fix it before investing in Studio.

For implementation references, [Kooha](https://github.com/SeaDve/Kooha) is a Rust/GTK recorder to study for the portal-to-media handoff; [OBS’s PipeWire plugin](https://github.com/obsproject/obs-studio/tree/master/plugins/linux-pipewire) illustrates stream negotiation; [Satty](https://github.com/Satty-org/Satty) illustrates annotating an already captured image. Treat them as behavior references, not dependencies or code to copy.
