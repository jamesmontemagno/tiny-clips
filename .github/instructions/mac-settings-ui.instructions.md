---
description: "Use when editing macOS Settings pages or SwiftUI views under mac/TinyClips/Views/Settings."
applyTo: "mac/TinyClips/Views/SettingsView.swift, mac/TinyClips/Views/Settings/**/*.swift"
---

# macOS Settings UI Styleguide

Use these conventions for all macOS Settings tabs and shared Settings controls.

## Layout

- Keep Settings in a `ScrollView` and preserve enough room for a natural macOS settings window:
  `820 pt` minimum width, `860 pt` ideal width, `580 pt` minimum height, and `720 pt` ideal height.
- Use one clear content card per logical settings section. Do not mix unrelated controls into a
  compact wall of rows.
- Keep card spacing consistent:
  `18 pt` outer padding, `6 pt` spacing between setting rows, `4 pt` between cards,
  `10 pt` or `12 pt` corner radius,
  and an appearance-aware border.
- Use full-width cards with `maxWidth: .infinity`; avoid fixed widths except for intentional
  popovers or control value columns.

## Shared SwiftUI Helpers

- Prefer `SettingsFormSection(title, icon:)` for titled cards. The title is part of the card, and
  the icon should describe the section.
- Use `.settingsCard()` for standalone controls that are not a normal Form section, such as a
  styled `DisclosureGroup`.
- Use `.settingsDescription()` for explanatory copy. It uses readable callout text, secondary
  foreground color, and fixed vertical sizing.
- Use `.quaternary.opacity(0.35)` for card fill and `.quaternary` for the border unless a specific
  status state (success, warning, destructive) requires stronger semantic color.

## Controls and Hierarchy

- Put a concise headline above a supporting description.
- Use `.headline` for card titles and primary row names, `.callout` for descriptions, and
  `.caption` or `.caption2` only for metadata.
- Use `.toggleStyle(.switch)` for a primary enable/disable action. Use standard toggles and
  pickers for ordinary settings.
- Use bordered buttons for deliberate secondary actions and copy controls. Use `.borderless`
  only for compact icon controls that already belong to a strong row.
- Group conditional controls immediately below the setting that enables them. Do not leave
  disabled controls visually disconnected from their explanation.
- Give every configurable or fixed keyboard shortcut row a meaningful SF Symbol through
  `HotKeyAction.iconName`; key badges alone are not sufficient.

## Text and Command Rows

- Never rely on `.caption` alone for setup instructions or important descriptions.
- Wrap setup text naturally with `fixedSize(horizontal: false, vertical: true)`.
- Put copyable commands and endpoint values in a padded row with a subtle fill and border.
  Align the copy button at the trailing edge and use `.bordered` with `.controlSize(.small)`.
- Keep copy status visible after an action and clear enough to read.

## State and Feedback

- Use semantic colors only for meaning: green for connected/listening, red for errors or
  destructive actions, secondary for inactive or metadata states.
- Preserve error text and retry actions when a setting or service has an error.
- Keep paired-agent names, permission summaries, last-used metadata, and action buttons aligned
  in a single row when space allows; wrap naturally at narrower widths.

## Accessibility

- Provide stable accessibility labels and hints for icon-only copy, permissions, revoke, reset,
  and file-picker actions.
- Keep descriptive text exposed in VoiceOver; do not merge it into a label unless the label
  accurately represents the full control.
- Preserve keyboard navigation and `.destructive` roles for destructive actions.
- Do not introduce UI that only works with a pointer; all Settings actions should remain usable
  by keyboard and VoiceOver.

## Validation

Build and test macOS Settings changes with both app schemes:

```bash
xcodebuild test -project mac/TinyClips.xcodeproj -scheme TinyClips -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClips -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClipsMAS -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
```
