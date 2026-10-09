# Cross-Platform Text Box Styles Plan

Tracks [#435](https://github.com/jamesmontemagno/tiny-clips/issues/435).

## Goal

Upgrade the existing screenshot-editor Text annotation into a lightweight text box that can remain plain or use a readable boxed style. macOS and Windows will expose the same capabilities and preset names, but each platform will use its native system font, controls, focus behavior, and visual details.

The feature must also remove the ambiguous no-selection behavior reported in #435: text controls may edit a selected text annotation or configure the next text annotation, but they must never modify the screenshot/export canvas.

## Confirmed Decisions

- Ship four small, semantic built-in presets with native platform details.
- Remember the current text style only for the lifetime of one editor window.
- Preserve Plain as the backward-compatible text-only option.
- Keep all existing typography controls.
- Treat this as an enhancement to the existing Text tool, not a second annotation tool.
- Keep macOS and Windows behavior equivalent without requiring pixel-identical editor UI or exported typography.

## Product and UX Contract

### Editing contexts

The inspector and text-entry UI must always be in exactly one of these contexts:

1. **Selected text** — controls mutate the selected text annotation and create one undoable document edit.
2. **New text defaults** — controls update only the style used by the next text annotation in this editor window. Changing a default does not dirty the document.
3. **No text context** — text controls are hidden or disabled. This is the required state when the Select/Move tool has no selected text annotation.

A selected non-text annotation must never receive text properties. Clearing selection must immediately leave the Selected text context; stale text controls must not remain active.

### Presets

Presets apply the text foreground and box treatment. They do not replace the annotation's content, font family, font size, or bold/italic/underline/strikethrough choices.

| Preset | Purpose | Text | Background | Border | Padding | Corner treatment |
| --- | --- | --- | --- | --- | --- | --- |
| **Plain** | Preserve today's text-only behavior | Current text-tool color | Transparent | None | 0 | 0 |
| **Light** | Readable label on dark or busy captures | Near-black | Native light neutral | Subtle neutral, 1 px | 8 px | Native compact radius |
| **Dark** | Readable label on light or busy captures | White | Native dark neutral | Subtle light, 1 px | 8 px | Native compact radius |
| **Accent** | High-emphasis callout | Contrast-safe white or black | Snapshot of the platform accent color | None | 8 px | Native compact radius |

Platform details:

- **macOS:** System font, macOS control accent resolved when the preset is applied, and the compact radius used by the editor's native controls.
- **Windows:** Segoe UI, the current Windows accent resolved when the preset is applied, and the WinUI control corner radius.
- Preset colors are copied into the annotation. Existing annotations do not change when the OS theme or accent changes later.
- Light, Dark, and Accent must meet at least WCAG AA contrast for normal text at creation time. If the resolved accent cannot meet that threshold with white or black text, choose the higher-contrast foreground and expose that resolved result in the preview.

Selecting a preset updates its owned properties. Editing text color, background, border, padding, or corner radius afterward changes the displayed preset state to **Custom**. Custom is a state, not a fifth reusable preset.

### Custom controls

Retain the existing text controls and add a Box section:

- Text color
- Background color, including transparent
- Border color, including transparent
- Border width, `0...12` image pixels
- Uniform padding, `0...48` image pixels
- Corner radius, `0...32` image pixels

Use one uniform padding value for this release. Separate horizontal/vertical padding, shadows, gradients, arbitrary preset creation, and saved user presets are out of scope.

### Insertion and selection flow

- Selecting the Text tool shows the current per-editor defaults.
- Choosing a preset before placement updates the next-text defaults.
- Creating text uses a snapshot of those defaults; later default changes do not alter existing annotations.
- Selecting an existing text annotation loads its complete style and typography into the controls.
- Applying a preset to selected text updates that annotation immediately and is undoable.
- Deselecting text returns to the Text tool defaults only if the Text tool is still active. Under Select/Move with no selection, text controls disappear.
- Double-clicking existing text keeps its box style while editing the content.
- Deleting or blanking text removes the whole annotation, including its box.

### Geometry and interaction

- Store box dimensions in source-image pixels so preview zoom and exported output agree.
- Keep the existing text layout bounds as the content bounds. Derive the outer decorated bounds by expanding for padding and half the border width.
- Use the decorated bounds for hit testing, selection marquee, resize handles, clipping checks, and rotation center.
- Use the same decorated bounds in live preview and export.
- Changing padding, border width, font, font size, emphasis, or text content must remeasure geometry while preserving the annotation's visual top-left anchor.
- Resizing a text annotation scales font size and box metrics together. Clamp all values to their supported ranges.
- Background and border rotate with the text as one annotation.

## Shared Data Contract

Both implementations should model the same value even though the language types differ:

```text
TextBoxStyle
  preset: plain | light | dark | accent | custom
  textColor
  backgroundColor
  borderColor
  borderWidth
  padding
  cornerRadius
```

Typography remains on the text annotation:

```text
fontFamily
fontSize
bold
italic
underline
strikethrough (Windows today; add only if separately approved for macOS)
```

Rules:

- `TextBoxStyle` is copied by value into each annotation.
- Annotation snapshots and undo/redo history include the complete style.
- Per-editor defaults live in the editor view model/controller, not global capture settings.
- No new `UserDefaults`, `CaptureSettings`, settings-store keys, migrations, or Settings UI are needed.
- Existing in-memory annotations without explicit box values behave as Plain.

## Rendering Contract

For both preview and export:

1. Resolve the text layout and content bounds.
2. Expand to decorated bounds using padding and border width.
3. Apply the annotation rotation around the decorated-bounds center.
4. Draw the rounded background if its alpha is nonzero.
5. Draw the border centered inside the decorated bounds if width and alpha are nonzero.
6. Draw text at the content origin using the existing typography and decoration behavior.

The export renderer is authoritative. Preview may use native text controls, but measurements, line breaks, padding, border placement, and rotation must visually match the baked output at fit and native-size zoom.

Do not reuse the editor's export-canvas background fields for text-box backgrounds. The two concepts require separate names, models, handlers, and events.

## macOS Implementation

### Model and defaults

Update `mac/TinyClips/Views/ScreenshotEditorModels.swift`:

- Add `TextBoxPreset` and value-type `TextBoxStyle`.
- Add `textBoxStyle` to `ScreenshotAnnotation` with a Plain default.
- Add deterministic preset factories that resolve native colors into concrete annotation colors.
- Add pure geometry helpers for decorated bounds and clamped box metrics.

Update `mac/TinyClips/Views/ScreenshotEditorViewModel.swift`:

- Add the current per-editor text-box default.
- Include style in text creation, annotation snapshots, history, equality/dirty handling, resize, hit testing, and selection geometry.
- Add selected-text accessors/mutators matching the existing typography pattern.
- Make preset application one history operation rather than one operation per changed field.
- Keep default-only changes out of `hasUnsavedChanges`.

### Native UI

Update `mac/TinyClips/Views/ScreenshotEditorWindow.swift` and, where needed, `ScreenshotEditorControls.swift`:

- Add a four-item style picker to the Text inspector, with a small `Aa` preview inside each swatch.
- Add a Box disclosure group below typography for background, border, width, padding, and radius.
- Bind controls through explicit Selected text versus New text defaults context.
- Show **Custom** when the current value no longer matches a preset.
- Preserve keyboard navigation, focus order, tooltips, and VoiceOver labels/values.
- Ensure the inline text editor previews the chosen foreground and box treatment without turning the editor chrome into exported content.

### Preview and export

Update `mac/TinyClips/Views/ScreenshotEditorCanvas.swift`:

- Render the background and border behind live text.
- Use decorated bounds for selection, handles, rotation, and accessibility frames.

Update the Core Graphics annotation path in `ScreenshotEditorViewModel.swift`:

- Draw the same rounded fill and border before the attributed string.
- Keep font traits, underline, multiline behavior, crop offsets, scale, and rotation intact.

### macOS tests

Extend `mac/TinyClipsTests/CaptureMathTests.swift` or add a focused deterministic editor-style test file covering:

- Plain style preserves the old no-box geometry.
- Decorated bounds include padding and border correctly.
- Preset matching changes to Custom after an owned property changes.
- Metric clamping.
- Contrast foreground selection for accent colors.
- Resize scales and clamps font/box metrics.
- Selected-text edits and default-only edits take different dirty/history paths.

Manually validate VoiceOver, keyboard-only use, light/dark appearance, rotation, crop/export, fit zoom, and native-size zoom.

## Windows Implementation

### Model and snapshots

Update `windows/src/TinyClips.Core/Editing/ScreenshotRenderState.cs`:

- Add a public or internal immutable `TextBoxStyle`/`TextBoxPreset` model in Core.
- Add style to `ScreenshotAnnotation` and `AnnotationSnapshot`.
- Keep the default equivalent to Plain so existing annotation construction and tests remain compatible.
- Add pure decorated-bounds and preset/contrast helpers in Core where they can be unit tested without WinUI.

Update `windows/src/TinyClips.App/Controls/ScreenshotEditor/EditorModels.cs` only for app-specific presentation models, not duplicate rendering state.

### Controller and selection fix

Update `windows/src/TinyClips.App/Controls/ScreenshotEditor/EditorController.cs`:

- Add per-editor text-box defaults and style mutators.
- Include style in add/edit text paths, immutable render snapshots, resizing, hit testing, and selection geometry.
- Separate methods for selected-annotation edits from methods that change next-text defaults.
- Stop generic color/style setters from mutating an unrelated selected annotation.
- Remeasure text and decorated bounds after every typography or box metric change.
- Emit annotation invalidation for selected edits and a dedicated defaults-changed signal for default-only edits.

Update `windows/src/TinyClips.App/Controls/ScreenshotEditor/EditorInspector.xaml.cs`:

- Remove the current behavior that deliberately leaves the last selection panel visible after deselection.
- On `SelectionChanged(null)`, show Text defaults only when the active tool is Text; otherwise hide annotation-specific controls.
- Guard every text handler by the explicit editing context.
- Keep export-background handlers isolated from text-box handlers.

This is the direct fix for the no-selection portion of #435.

### Native UI

Update `windows/src/TinyClips.App/Views/TextEntryDialog.xaml(.cs)`:

- Add the four preset swatches and Box controls to the existing live preview.
- Return a complete `TextBoxStyle` with the typography result.
- Preserve style when the dialog edits an existing annotation.
- Keep Tab order, Enter/Escape behavior, automation names, high-contrast visibility, and focus on the text entry.

Update `windows/src/TinyClips.App/Controls/ScreenshotEditor/EditorInspector.xaml(.cs)`:

- Add the same preset set and Box controls for the selected annotation or next-text defaults.
- Use WinUI theme resources for editor chrome, but store resolved concrete colors in the annotation.
- Announce preset selection and expose numeric values through UI Automation.

### Preview and export

Update `windows/src/TinyClips.App/Controls/ScreenshotEditor/EditorCanvas.xaml.cs`:

- Add retained background and border visuals behind each text visual.
- Apply one shared rotation transform to box and text.
- Use decorated bounds for marquee, handles, and hit testing.

Update the Win2D text case in `EditorController.cs`:

- Draw rounded fill and border before `CanvasTextLayout`.
- Use one shared layout/geometry helper for measurement and rendering inputs.
- Preserve underline, strikethrough, multiline text, rotation, crop, and export-frame behavior.

### Windows tests

Extend `windows/tests/TinyClips.Core.Tests/ScreenshotExportTests.cs` and add focused Core tests covering:

- Snapshot copying includes every text-box field.
- Plain snapshot output remains backward compatible.
- Decorated bounds, clamping, preset matching, contrast selection, and resize scaling.
- Render-state snapshots are immutable after live annotation/default changes.

Add app-level controller tests under `windows/tests/TinyClips.App.Tests` for:

- A selected text style edit changes only that annotation and marks the document dirty.
- A Text-tool default edit with no selection changes only the next insertion and does not mark dirty.
- No selection under Select hides/disables text editing and does not change image/export properties.
- Selecting a non-text annotation prevents text handlers from mutating it.
- TextEntryDialog add/edit round-trips the complete style.

Add or update the Windows UI automation batch for keyboard traversal, preset selection, dialog cancel/confirm, selection clearing, and automation names.

## Delivery Sequence

1. **Pure contracts and tests**
   - Add preset/style values and geometry/contrast helpers on each platform.
   - Lock Plain backward compatibility and snapshot behavior with deterministic tests.
2. **Rendering**
   - Implement export rendering first, then make live preview match it.
   - Validate fill, border, padding, radius, rotation, crop, zoom, and multiline text.
3. **Selection semantics**
   - Introduce explicit Selected text/New text defaults/No text context handling.
   - Fix Windows stale-inspector behavior before wiring new controls.
4. **Preset and custom UI**
   - Add native preset swatches, box controls, and live previews.
   - Wire add, edit, double-click, selection, undo/redo, and deletion.
5. **Accessibility and regression validation**
   - Run automated tests and builds.
   - Perform one bounded native visual/accessibility pass on each platform.
6. **Release notes**
   - Add the cross-platform feature to root `CHANGELOG.md`.
   - Add the Windows-specific editor fix and feature to `windows/CHANGELOG.md`.

## Acceptance Criteria

- Plain text annotations render and export as they do before this feature.
- Light, Dark, and Accent presets create a background box with correct text contrast, border, padding, and native corner treatment.
- Users can customize text color, background, border color/width, padding, and radius.
- Selecting an existing text annotation exposes and edits its full style.
- Choosing a preset or custom style before insertion affects the next text annotation only.
- Style defaults survive multiple insertions in one editor window and reset when a new editor window opens.
- Clearing selection under Select/Move prevents all text controls from changing the document or export canvas.
- Background, border, text, selection marquee, hit target, resize handles, and rotation remain aligned at all supported zoom levels.
- Preview and saved output match for rotation, crop, export framing, multiline text, and transparent colors.
- One undo restores the complete prior style after a preset or grouped custom style change.
- macOS is usable with keyboard and VoiceOver; Windows is usable with keyboard, UI Automation, high contrast, and light/dark themes.
- macOS tests and both app schemes pass.
- Windows Core/App tests and x64 app build pass.

## Validation Commands

### macOS

```bash
xcodebuild test -project mac/TinyClips.xcodeproj -scheme TinyClips -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClips -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClipsMAS -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
```

### Windows

```powershell
dotnet restore windows/TinyClips.Windows.slnx
dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64
dotnet test windows/tests/TinyClips.Core.Tests/TinyClips.Core.Tests.csproj -c Debug
dotnet test windows/tests/TinyClips.App.Tests/TinyClips.App.Tests.csproj -c Debug -p:Platform=x64
```

## Risks and Mitigations

| Risk | Mitigation |
| --- | --- |
| Preview/export text metrics diverge | Centralize measurement inputs per platform and test decorated geometry independently; compare fit and native-size zoom manually. |
| Padding changes move or rotate annotations unexpectedly | Preserve the decorated top-left anchor during remeasurement and rotate around decorated bounds. |
| Accent colors produce poor contrast | Snapshot the native accent, calculate white/black contrast, and choose the higher-contrast foreground. |
| Inspector events mutate stale or unrelated state | Model the three editing contexts explicitly and test every transition. |
| Style controls create many undo entries | Apply presets and committed grouped edits as single history operations; do not add default-only changes to history. |
| Cross-platform scope grows into a design tool | Keep four built-ins, uniform padding, solid colors, one border, and no saved custom presets in this release. |
