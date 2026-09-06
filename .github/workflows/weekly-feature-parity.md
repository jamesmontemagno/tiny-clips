---
name: Weekly feature parity report
description: Compares TinyClips Windows and macOS implementations and opens scoped issues for actionable parity gaps.
on:
  schedule: weekly on sunday around 09:00
  workflow_dispatch:

permissions:
  contents: read
  issues: read
  pull-requests: read
  copilot-requests: write

engine: copilot
model: auto

tools:
  github:
    toolsets: [default]

network: defaults

safe-outputs:
  create-issue:
    title-prefix: "[parity] "
    max: 5

---

# TinyClips weekly feature parity report

Every Sunday, compare the TinyClips Windows and macOS implementations and produce a concise feature-parity report.

## Instructions

Review the current repository state, including:

1. The macOS app in `mac/`.
2. The Windows app in `windows/`.
3. Platform documentation and changelogs, especially `CONTRIBUTING.md`, `CHANGELOG.md`, `windows/README.md`, `windows/CHANGELOG.md`, and docs linked from those files.
4. Existing open issues and pull requests that may already cover a parity gap.

Create a report with these sections:

1. Features present on macOS but missing or incomplete on Windows.
2. Features present on Windows but missing or incomplete on macOS.
3. Shared behaviors that appear inconsistent across platforms.
4. Deferred items that are intentionally platform-specific, blocked, already tracked, or not actionable yet.

For each potential gap, decide whether it can reasonably be implemented now using the repository context. Do not assume every platform-specific behavior must be identical; respect TinyClips platform conventions:

1. macOS uses Swift, SwiftUI, AppKit, `#if APPSTORE`, and `#if canImport(Sparkle)` patterns.
2. Windows uses WinUI 3, Windows App SDK, .NET, and the `TinyClipsStoreBuild` MSBuild property.
3. Keep UI-app concerns in the platform UI projects and reusable/domain logic in platform core/shared areas as the repo already does.

If there are no actionable feature gaps, state that no new issue is needed and stop.

If actionable feature gaps exist:

1. Search existing open issues first and do not create duplicates.
2. Create one GitHub issue per distinct implementable parity gap, up to the configured safe-output limit.
3. Scope each issue to one affected platform or one clearly bounded cross-platform behavior.
4. Include the current behavior, desired parity behavior, relevant files/docs to inspect, an implementation outline, and acceptance criteria.
5. Mention when a gap is intentionally deferred instead of creating an issue for it.

Keep generated issues precise and implementation-ready. Prefer minimal, platform-appropriate work that preserves each app's conventions.
