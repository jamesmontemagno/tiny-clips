---
description: "Use when editing macOS Settings pages or SwiftUI views under mac/TinyClips/Views/Settings."
applyTo: "mac/TinyClips/Views/SettingsView.swift, mac/TinyClips/Views/Settings/**/*.swift"
---

# macOS Settings UI Styleguide

Settings is a grouped `Form`, laid out the way System Settings is. The form draws the cards, the
separators between rows, and the spacing. Do not draw your own.

## The one rule

**One group is one `Section`, and one setting is one row.** Every direct child of a section
becomes a row with a separator above it and the form's own padding around it.

- Never wrap a section's settings in a `VStack`, and never give a section a background, border, or
  padding. That puts a card inside the form's card and removes the row separators, and no spacing
  value will then look right.
- Never put the `Form` inside a `ScrollView`. The form scrolls itself.
- Do not set spacing between settings by hand. If rows look cramped, something is stacked inside
  one row.

## Building blocks

All are in `SettingsViewHelpers.swift`. Use them instead of laying a row out by hand.

| Use | For |
|---|---|
| `SettingsFormSection(title, icon:, footer:)` | A group. The title and SF Symbol go above the card. |
| `SettingsToggleRow` | A switch. Title on the left, switch on the right. |
| `Picker("Label", selection:)` | A choice. Give it a plain label with no trailing colon. |
| `SettingsSliderRow` | A slider with its value. All sliders share one width so they line up. |
| `SettingsActionRow` | Something you do: what it is on the left, its buttons on the right. |
| `LabeledContent("Label") { … }` | A value to read, or a control with no helper above. |
| `SettingsAdvancedSection` | Settings most people never change. Starts collapsed. |
| `.settingsDescription()` | Secondary text inside a row you had to build yourself. |

## Grouping

- Name a group for what its settings have in common: "Save Location", "Before Recording". Do not
  use a catch-all like "Advanced" or "Other" for an ordinary group.
- Aim for two to six rows in a group. Split a longer one, and fold a single stray setting into the
  group it is closest to.
- Order groups by how often they are used, and by time where there is one: before, during, after.
- Show a setting that depends on another directly under it, and only while it applies. For
  example, the countdown duration appears under the countdown switch when it is on.
- A setting that belongs to another tab gets a `SettingsActionRow` with a button that goes there.

## Advanced settings

Use `SettingsAdvancedSection` for the collapsed group at the end of a tab. Its header matches the
other groups and is what opens it. Pass `startsExpanded:` when one of its settings is changed from
the default, so a setting in effect is never hidden. Do not use a bare `DisclosureGroup` in the
form: it has no header and does not look like the other groups.

## Text

- Add a description under a title only when the title does not say what the setting does, or the
  setting has a consequence worth knowing. Most switches need none. Keep the detail in `.help`.
- Text about a whole group goes in the section `footer`, not in a row.
- A row title is sentence case with no trailing colon or period. A button that opens a dialog or
  another window ends with an ellipsis.
- When a row title already says what a button is for, keep the button short ("Choose…",
  "Reset…") and give it the full name as its accessibility label.

## Custom rows

Some content is not a setting: a chart, a preview, a setup step with commands. That is one row
with its own layout. Keep it flat, with no card of its own. Text to copy goes in a field with a
copy button that confirms on the button itself.

## Color

Use semantic colors only for meaning: green for connected, red for errors and destructive
actions. Do not tint decoration with the accent color.

## Accessibility

- Section titles are headers. `SettingsFormSection` does this.
- A slider, color well, or stepper with a hidden label needs an `accessibilityLabel`, and a
  slider needs an `accessibilityValue`. `SettingsSliderRow` does both.
- Icon-only buttons need a label. When several rows have the same button title, each needs a
  label that says which row it is for.
- Every keyboard shortcut row has an SF Symbol from `HotKeyAction.iconName`, in a frame of
  `ShortcutRecorderField.iconWidth` so the names line up.
- Keep `.destructive` roles, and keep every action reachable by keyboard and VoiceOver.

## Check your work

Look at the tab in the running app, in light and dark, before finishing. Quit any running copy of
TinyClips first: opening the app while another copy runs only brings that one forward, and it may
be an older build.

```bash
xcodebuild test -project mac/TinyClips.xcodeproj -scheme TinyClips -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClips -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClipsMAS -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
```
