# Product

<!-- impeccable:product-schema 1 -->

## Platform

adaptive

## Users

TinyClips serves people who need to capture and annotate screenshots quickly in a native macOS or Windows workflow. They value a fast path from capture to a clear, shareable image more than the breadth of a full design application.

## Product Purpose

TinyClips makes screenshots, recordings, and GIFs quick to capture, lightly edit, and share. Success means common capture and annotation tasks stay fast, understandable, and reliable on each supported desktop platform.

## Positioning

TinyClips is a tray-first native capture utility with an integrated post-capture editor. It keeps capture and lightweight annotation in one focused workflow while adapting controls and interaction details to macOS and Windows conventions.

## Operating Context

- The app starts from a menu bar item on macOS and a system-tray item on Windows.
- Screenshot annotations are created immediately after capture in the native screenshot editor.
- Annotated images are commonly shared outside TinyClips, so exported visuals must remain legible without depending on the viewer's theme or installed app state.

## Capabilities and Constraints

- macOS uses SwiftUI and AppKit; Windows uses WinUI 3, Windows App SDK, C#, and XAML.
- Cross-platform features should share behavior and terminology while retaining native controls, system fonts, keyboard behavior, accessibility, and theming.
- Screenshot editing is intentionally lightweight. Text styling should improve readability without expanding into a general-purpose layout or design tool.
- Text style defaults are scoped to the current editor window unless a feature explicitly requires persistence.

## Brand Commitments

- Preserve the TinyClips name and the existing native, compact, task-focused product character.
- Platform parity means equivalent capability and predictable behavior, not pixel-identical interface chrome.

## Evidence on Hand

- Issue [#435](https://github.com/jamesmontemagno/tiny-clips/issues/435) requests text annotations with configurable borders, backgrounds, padding, and existing typography options.
- The existing macOS and Windows screenshot editors already support text annotations, typography controls, selection, movement, rotation, and baked image export.
- No user research, usage analytics, or externally validated preset preference data is currently available; preset choices must remain small, reversible, and easy to revise.

## Product Principles

- Keep the path from capture to finished image short.
- Prefer a few useful defaults over a large style catalog.
- Make document edits explicit; never let an annotation control unexpectedly modify the full image.
- Share feature semantics across platforms while respecting native interaction patterns.
- Export what the editor preview shows.

## Accessibility & Inclusion

- New editor controls require keyboard access, meaningful accessible names and values, visible focus, and sufficient text/background contrast.
- Presets must not rely on color alone to communicate selection or purpose.
- macOS changes must remain usable with VoiceOver; Windows changes must remain usable through UI Automation and keyboard navigation.
