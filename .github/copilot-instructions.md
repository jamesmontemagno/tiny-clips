# TinyClips - Agent Instructions

TinyClips is now a cross-platform repository with:
- macOS app in `mac/` (SwiftUI + AppKit, Xcode project)
- Windows app in `windows/` (WinUI 3 + Windows App SDK, .NET solution)

If I ask a question or need something and I don't specify Windows or macOS or both, ask me.

Use this file for repo-wide rules and platform selection. Keep behavior platform-aware and avoid cross-app assumptions.

## Start Here

- For mac architecture, capture flows, and contribution workflow, read [CONTRIBUTING.md](../CONTRIBUTING.md).
- For Windows app status, structure, and commands, read [windows/README.md](../windows/README.md).
- For deeper platform details, link to docs instead of re-embedding:
  - [docs/retina-display-capture.md](../docs/retina-display-capture.md)
  - [windows/docs/dpi-and-coordinates.md](../windows/docs/dpi-and-coordinates.md)
  - [windows/docs/gpu-recording-pipeline.md](../windows/docs/gpu-recording-pipeline.md)
  - [docs/app-store-variant-setup.md](../docs/app-store-variant-setup.md)
  - [windows/packaging/README.md](../windows/packaging/README.md)

## Platform Routing

- If a request touches only `mac/**`, follow mac conventions and build mac schemes.
- If a request touches only `windows/**`, follow WinUI conventions and build/test Windows projects.
- If a request touches both, validate both platforms before finishing.

Do not mix platform-specific implementation patterns:
- mac: Swift/SwiftUI/AppKit, `#if APPSTORE`, `#if canImport(Sparkle)`
- windows: C#/XAML/WinUI 3, `TinyClipsStoreBuild` MSBuild property for Store flavor

## Build and Validate

### macOS (always validate both schemes for mac changes)

```bash
xcodebuild test -project mac/TinyClips.xcodeproj -scheme TinyClips -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClips -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClipsMAS -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
```

- mac unit tests cover deterministic logic only; do not add permission, hardware, or UI dependencies.
- If sandbox blocks `xcodebuild` (for example `Operation not permitted` or SwiftPM write errors), rerun unsandboxed.

### Windows (required for `windows/**` changes)

```powershell
dotnet restore windows/TinyClips.Windows.sln
dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64
dotnet test windows/tests/TinyClips.Core.Tests/TinyClips.Core.Tests.csproj -c Debug
```

- WinUI 3 does not support `AnyCPU`; use `-p:Platform=x64` or `-p:Platform=ARM64`.

## Critical Conventions

### macOS

- Keep MAS-only logic inside `#if APPSTORE`.
- Never include Sparkle in MAS builds; guard Sparkle usage with `#if canImport(Sparkle)`.
- Use `ObservableObject` / `@Published` / `@StateObject` and `@AppStorage` patterns used by existing code.
- Follow targeted instruction files when they apply:
  - `.github/instructions/capture-windows.instructions.md`
  - `.github/instructions/mas-storekit.instructions.md`

### Windows

- Organize UI code by responsibility:
  - Models in `Models`
  - ViewModels in `ViewModels`
  - Windows/pages in `Views`
  - Reusable controls in `Controls`
  - Group related classes into appropriate feature folders
- Keep UI app concerns in `windows/src/TinyClips.App` and reusable/domain logic in `windows/src/TinyClips.Core`.
- Follow DPI-safe coordinate handling from [windows/docs/dpi-and-coordinates.md](../windows/docs/dpi-and-coordinates.md).
- Preserve tray-first behavior (no main window at startup) unless explicitly requested.
- Keep global hotkey defaults aligned with Windows conventions documented in [windows/README.md](../windows/README.md).
- Prefer compact flyout-based filtering for the Windows Clips Library, inspired by the macOS app, while keeping grid and list presentations polished and action buttons well organized.

## PR Checklist

- Build/test only what changed at minimum, and both platforms when cross-platform files are touched.
- Update changelog for the affected platform:
  - root [CHANGELOG.md](../CHANGELOG.md) for mac/shared changes
  - [windows/CHANGELOG.md](../windows/CHANGELOG.md) for Windows app changes
- Preserve accessibility quality gates:
  - mac: VoiceOver + keyboard for affected flows
  - windows: keyboard navigation and accessible names for new/changed controls
