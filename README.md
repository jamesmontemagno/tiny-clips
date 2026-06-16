# Tiny Clips

[![Build](https://github.com/jamesmontemagno/tiny-clips/actions/workflows/build.yml/badge.svg)](https://github.com/jamesmontemagno/tiny-clips/actions/workflows/build.yml)
[![Release](https://github.com/jamesmontemagno/tiny-clips/actions/workflows/release.yml/badge.svg)](https://github.com/jamesmontemagno/tiny-clips/actions/workflows/release.yml)
[![GitHub release](https://img.shields.io/github/v/release/jamesmontemagno/tiny-clips?style=flat-square)](https://github.com/jamesmontemagno/tiny-clips/releases/latest)
![macOS](https://img.shields.io/badge/macOS-15.0+-blue?style=flat-square&logo=apple)
![Windows](https://img.shields.io/badge/Windows-11-0078D6?style=flat-square&logo=windows11)
[![License: MIT](https://img.shields.io/github/license/jamesmontemagno/tiny-clips?style=flat-square)](LICENSE)

A lightweight menu-bar (macOS) and system-tray (Windows) app for capturing screenshots (PNG),
video (MP4), and animated GIFs of a selected screen region — on **macOS** and **Windows**.


![tiny-clips-promo (1)](https://github.com/user-attachments/assets/0afc2c8a-a83b-4703-9873-b4fb0c315c06)


## Features

- **Screenshot** — Select a region, screen, or window and capture a PNG screenshot
- **Video Recording** — Record to MP4 with hardware-accelerated H.264 encoding
- **GIF Recording** — Record a screen region as an animated GIF
- **Capture Picker** — Choose **Region**, **Screen**, or **Window** before any capture
- **Editor & Trimmers** — Post-capture screenshot editor plus video and GIF trimmers
- **Menu Bar / Tray App** — Lives in the macOS menu bar or Windows system tray with no Dock/taskbar icon
- **Region Selection** — Drag to select any portion of any screen
- **Global Hotkeys** — Quick capture from anywhere
- **Configurable** — Save location, clipboard, reveal-in-Finder/Explorer, GIF quality, trimmer toggles, and more

> macOS uses Sparkle for auto-updates; Windows distributes via **winget** (`winget upgrade`) and the Microsoft Store.

## macOS

### Requirements

- macOS 15.0 (Sequoia) or later
- Xcode 16.0 or later (to build from source)

### Installation

**Homebrew**

```bash
brew tap jamesmontemagno/tiny-clips
brew install --cask tiny-clips
```

**Download**

Download the latest release from the [Releases](https://github.com/jamesmontemagno/tiny-clips/releases) page.

**Build from Source**

1. Clone the repository:
   ```bash
   git clone https://github.com/jamesmontemagno/tiny-clips.git
   cd tiny-clips
   ```
2. Open in Xcode:
   ```bash
   open mac/TinyClips.xcodeproj
   ```
3. Add the Sparkle package dependency (see [Sparkle Setup](#sparkle-setup))
4. Build and run (⌘R)

### Permissions

TinyClips requires **Screen Recording** permission. On first launch, macOS will prompt you to grant access. After granting, restart the app.

### Keyboard Shortcuts

| Action | Shortcut |
|--------|----------|
| Screenshot | ⌃⌥⌘5 |
| Record Video | ⌃⌥⌘6 |
| Record GIF | ⌃⌥⌘7 |
| Picker: Region / Screen / Window | R / S / W |
| Picker: Cancel | Esc |
| Stop Recording | ⌘. |
| Settings | ⌘, |

## Windows

A native **WinUI 3 / Windows App SDK** port lives under [`/windows`](windows/README.md).

### Requirements

- Windows 11 **22H2 (build 22621)** or later
- [.NET 10 SDK](https://dotnet.microsoft.com/) and Windows SDK `10.0.26100` (to build from source)

### Installation

**winget** — Tiny Clips is published in the [winget community repository](https://github.com/microsoft/winget-pkgs), so you can install it from any Windows 11 terminal:

```powershell
winget install Refractored.TinyClips
```

Keep it up to date with:

```powershell
winget upgrade Refractored.TinyClips
```

A Microsoft Store listing (with optional Pro add-ons) is planned.

**Build from Source**

WinUI 3 requires an explicit platform (`x64` or `ARM64`; `AnyCPU` is not supported).

```powershell
dotnet restore windows/TinyClips.Windows.slnx
dotnet run --project windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64
```

See the [Windows README](windows/README.md) for full build, layout, and CI details.

### Keyboard Shortcuts

| Action | Shortcut |
|--------|----------|
| Screenshot | Ctrl+Shift+5 |
| Record Video | Ctrl+Shift+6 |
| Record GIF | Ctrl+Shift+7 |
| Picker: Region / Screen / Window | R / S / W |
| Picker: Cancel | Esc |
| Stop Recording | Ctrl+Shift+S |

## Usage

1. Click the Tiny Clips icon in the menu bar (macOS) or system tray (Windows)
2. Choose **Screenshot**, **Record Video**, or **Record GIF**
3. Choose **Region**, **Screen**, or **Window** in the capture picker (with an optional countdown)
4. For region recordings, confirm audio/mic options in the floating **Record** panel
5. Click the floating **Stop** button when done (or use the stop shortcut)

## Settings

| Option | Description |
|--------|-------------|
| Save Directory | Where captures are saved (default: Pictures/TinyClips) |
| Copy to Clipboard | Auto-copy captures to clipboard |
| Reveal in Finder / Explorer | Reveal the saved file after capture |
| GIF Frame Rate | 5–30 fps (default: 10) |
| GIF Max Width | 320–1920 px (default: 640) |
| Video Frame Rate | 24, 30, or 60 fps |
| Open Trimmer | Show trim editor after recording |

## Sparkle Setup (macOS)

Sparkle must be added manually via Xcode:

1. Open `mac/TinyClips.xcodeproj` in Xcode
2. Go to **File → Add Package Dependencies...**
3. Enter URL: `https://github.com/sparkle-project/Sparkle`
4. Select version rule: **Up to Next Major Version** from `2.8.1`
5. Add the `Sparkle` framework to the `TinyClips` target

See [docs/sparkle-setup.md](docs/sparkle-setup.md) for full setup including key generation.

## App Store / Store Variants

- **macOS:** to ship both a direct (Sparkle, non-sandbox) build and a Mac App Store (sandboxed, no Sparkle) build from one codebase, see [docs/app-store-variant-setup.md](docs/app-store-variant-setup.md).
- **Windows:** the WinUI 3 app targets a free **winget**/direct build now and a Microsoft Store listing later; see [`/plans/windows-winui3-port-plan.md`](plans/windows-winui3-port-plan.md).

## Architecture

**macOS**

```
ScreenCaptureKit (SCStream / SCScreenshotManager)
       │
       ├── ScreenshotCapture → CGImageDestination → PNG
       ├── VideoRecorder → AVAssetWriter → MP4
       └── GifWriter → CGImageDestination → GIF
```

**Windows**

```
Windows.Graphics.Capture (Direct3D11CaptureFramePool)
       │
       ├── ScreenshotService → BitmapEncoder → PNG/JPEG
       ├── VideoRecordingService → Media Foundation → H.264 MP4
       └── GifRecordingService → BitmapEncoder → GIF
```

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Windows README](windows/README.md) for component-level detail.

## License

MIT License. See [LICENSE](LICENSE) for details.

## Contributing

Contributions are welcome! Please feel free to submit a [Pull Request](https://github.com/jamesmontemagno/tiny-clips/pulls).

Found a bug or have a feature request? [Open an issue](https://github.com/jamesmontemagno/tiny-clips/issues/new).
