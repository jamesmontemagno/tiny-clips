---
description: "Use when adding or changing screenshot annotations, annotation styles, or the export canvas and background setup, or when editing Editor Themes, on macOS or Windows."
applyTo: "mac/TinyClips/Views/ScreenshotEditor*.swift, mac/TinyClips/Models/EditorTheme*.swift, mac/TinyClips/Services/EditorThemeStore.swift, mac/TinyClips/Views/Settings/EditorThemesSettingsSection.swift, mac/TinyClips/Automation/AutomationAnnotations.swift, mac/TinyClips/Automation/AutomationEditorThemes.swift, windows/src/TinyClips.App/Controls/ScreenshotEditor/**, windows/src/TinyClips.App/Controls/Settings/EditorThemesSettingsSection.*, windows/src/TinyClips.App/ViewModels/EditorThemesSettingsViewModel*, windows/src/TinyClips.Core/Editing/ScreenshotRenderState.cs, windows/src/TinyClips.Core/Editing/Themes/**, windows/src/TinyClips.Core/Automation/AutomationAnnotationParser.cs, shared/editor-themes/**"
---

# Editor Themes Coverage

A new or changed annotation style or canvas setting must reach Editor Themes in the same change, on each platform that has it. A new annotation type gets its own theme section and its own Settings controls, unless it's excluded below. The design is in [plans/editor-themes.md](../../plans/editor-themes.md), and the format is in [shared/editor-themes/README.md](../../shared/editor-themes/README.md).

## What to Update

| Area | macOS | Windows |
| --- | --- | --- |
| Theme model, validation, and JSON | `Models/EditorTheme.swift` | `Core/Editing/Themes/EditorTheme.cs` |
| Applying a theme | `Models/EditorThemeStyling.swift` | `Core/Editing/Themes/EditorThemeStyling.cs` |
| Modified, Update Theme, and Save as New | `Models/EditorThemeStyling.swift` | `Core/Editing/Themes/EditorThemeCapture.cs` |
| Defaults, canvas, and undo in the editor | `Views/ScreenshotEditorViewModel.swift` | `App/Controls/ScreenshotEditor/EditorController.Themes.cs` |
| Settings controls and live preview | `Views/Settings/EditorThemesSettingsSection.swift` | `App/Controls/Settings/EditorThemesSettingsSection.xaml`, `App/ViewModels/EditorThemesSettingsViewModel*.cs` |
| `editor.annotate` with `themeId` | `Automation/AutomationAnnotations.swift` | `Core/Automation/AutomationAnnotationParser.cs` |

## Required Behavior

- **Editor:** choosing a theme applies the setting to existing annotations and to new ones. Undo restores it in the same step as the rest of the theme switch.
- **Modified:** a manual edit reads as Modified, and Update Theme and Save as New keep the value. Applying a theme and switching tools must not read as Modified.
- **Settings:** the theme editor can change the setting, and the preview shows it.
- **Rendering:** the preview and the exported image match. Export is authoritative.

## Import and Export

Theme import and export, and the agent commands `editor.themes.get` and `editor.themes.save`, all use the same JSON. A new setting must survive a round trip on both platforms:

- Add the field to `shared/editor-themes/README.md`, with its units and range if it has them.
- Add it to `fixtures/default-theme.json` with a default that keeps today's look, so older theme files still import unchanged.
- Set it to a non-default value in `fixtures/brand-theme.json`.
- Add a case to `fixtures/invalid-themes.json` if it has a range or named values.
- Store sizes in Mac editor units, and convert them per image on Windows.
- Keep `version` at 1 for a new optional field. Bump it only if an older app would misread a theme.

The fixture tests compare round trips exactly, so a platform whose theme model skips the field fails them. They don't check the editor or Settings, so test those separately.

## Exclusions

- Redactions and emoji are never restyled, so a theme can't weaken a redaction.
- A setting that can't be shared, such as an image or a one-platform option, goes in the exclusions in `plans/editor-themes.md` with the reason. Don't leave it out silently.

## Tests

Update `mac/TinyClipsTests/EditorThemeTests.swift` and `windows/tests/TinyClips.Core.Tests/EditorThemeTests.cs`. Cover the round trip, applying the theme, and Modified.
