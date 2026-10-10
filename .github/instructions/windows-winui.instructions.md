---
description: "Use when editing Windows app code in windows/src or windows/tests (WinUI 3, Windows App SDK, capture pipeline, tray app behavior, build/test validation)."
applyTo: "windows/src/**/*.cs, windows/src/**/*.xaml, windows/tests/**/*.cs, windows/Directory.Build.props, windows/**/*.csproj, windows/**/*.props"
---

# TinyClips Windows Conventions

## Scope

This instruction applies to the WinUI 3 app and core libraries under `windows/`.

Reference docs for details:
- [windows/README.md](../../windows/README.md)
- [windows/docs/dpi-and-coordinates.md](../../windows/docs/dpi-and-coordinates.md)
- [plans/windows-winui3-port-plan.md](../../plans/windows-winui3-port-plan.md)

## Architecture Boundaries

- Keep UI/window/tray integration in `windows/src/TinyClips.App`.
- Keep reusable capture/business logic in `windows/src/TinyClips.Core`.
- Keep tests in `windows/tests/TinyClips.Core.Tests` and prefer adding/updating tests for `TinyClips.Core` changes.

## Build and Test

For Windows changes, run:

```powershell
dotnet restore windows/TinyClips.Windows.slnx
dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64
dotnet test windows/tests/TinyClips.Core.Tests/TinyClips.Core.Tests.csproj -c Debug
```

Rules:
- WinUI 3 does not support `AnyCPU`; use `x64` or `ARM64`.
- For Store flavor validation, add `-p:TinyClipsStoreBuild=true` to `dotnet build`.
- Treat compile warnings as build blockers: inspect warnings from restore/build/test, fix them in the same patch, and only leave a warning behind intentionally with a documented justification.
- For concurrency or analyzer warnings in Windows code, fix them before completion instead of deferring them to a later cleanup pass.

## Behavior and UX Rules

- Preserve tray-first startup behavior unless a task explicitly asks to change it.
- Keep capture flow parity with documented behavior (picker -> optional countdown -> capture/record -> editor/trimmer -> save).
- Maintain hotkey defaults and conflict checks using current Windows settings model and UI patterns.

## DPI and Coordinates

- Treat mixed-DPI setups as a correctness boundary.
- Keep all coordinate conversions explicit and consistent with [windows/docs/dpi-and-coordinates.md](../../windows/docs/dpi-and-coordinates.md).
- When touching capture bounds logic, validate region/screen/window capture coordinates against monitor scale factors.

## Accessibility and Theming

- Ensure keyboard access for tray menu actions, dialogs, and editing flows.
- Add/update automation names and labels for icon-only or custom controls.
- Preserve light/dark/system theme behavior and do not hardcode colors that break contrast.