# Studio annotation clipboard (Windows)

In **Annotate**, **Copy** / **Paste** (`Ctrl+C` / `Ctrl+V`) transfer one editable annotation
between clips, projects, or open Studio windows. Paste stays available without a selection or
existing annotations when the clipboard contains the custom format. Text and numeric-entry
controls retain native copy/paste. Playback, dragging, export, project save, and caption generation
block annotation clipboard actions. Duplicate (`Ctrl+D`) is unchanged.

## Interchange

- Custom format: `com.tinyclips.studio.annotation.v1`.
- Raw UTF-8 JSON: `{ "version": 1, "annotation": <full StudioAnnotation>, "outputDurationSeconds": <positive finite double> }`.
- Maximum encoded payload: 1 MiB. No plain-text JSON fallback, media, source paths, or project schema fields.
- All existing annotation fields are required, with their proper JSON types. Known kinds and
  enum values only; every numeric field must be finite and `end > start`. Negative source
  starts and geometry outside editor-control ranges are preserved exactly: project-format
  clamps apply during rendering, not clipboard decoding. Copied output duration must remain
  positive and finite. Unknown annotation extension fields are retained.
  Malformed, unsupported, duplicate-field, and
  oversized data produces explicit feedback without editing.
- This is UI-only interchange, not an Agent API command or agent clipboard access.

Copy measures duration with the source clip's edited time map (trim, cuts, variable speed).
Paste maps the displayed destination frame through the selected clip's output time, retains
that playback duration until clamped to its edited end, and stores source seconds. An end
exactly on a cut boundary uses the preceding kept edge (tolerance `1e-9`). If less than the
existing `0.1` source-second minimum fits, Paste reports no room without shifting earlier.
Geometry and screen/frame attachment never change. Paste-specific visibility feedback resolves
the committed annotation against the destination layout at its mapped start, including
camera-only destinations and cut skips. The inspector's existing unknown-kind hint is unchanged.

Each paste appends a fresh top-layer ID, selects it, and commits one existing edit-and-select
transaction with undo/redo, autosave, and portable Save Project. Success requires that the
generated ID actually exists in committed state; a rejected mutation is not success.

## Implementation and validation

Core owns strict encoding/decoding, timing, destination-intent capture, and insertion. The app
adapter owns `Clipboard`, raw random-access streams, bounded reads, `Flush` for source-close
durability, and content-change subscription/disposal. Async reads capture the session/project,
selected clip, editable state, edit generation, and playhead; all are rechecked, together with
closed/playing/gesture/export/save state and the window's modal state, before insertion.
Intervening edits or playhead moves invalidate an intent even if subsequently undone.

Focused coverage:

- `StudioAnnotationClipboardTests`, `StudioAnnotationTransferModelTests`,
  `StudioAnnotationTransferSessionTests`: shared single-envelope fixture, all kinds/fields,
  invalid payloads, finite out-of-range geometry and negative-start preservation,
  resolved destination visibility, source/destination speeds and cuts, clamping/no-room, rejected commits,
  fresh IDs, Duplicate, undo/redo, autosave/reopen/portable save, and stale intents.
- `StudioAnnotationClipboardViewModelTests`: deferred provider reads, stale destinations,
  busy/closed windows, clipboard failures, no-selection Paste, committed-state feedback,
  camera-only visibility, and observer disposal with a fake clipboard.
- `StudioWindowCheck` annotation checks: actual native custom format without text fallback,
  source-close durability, empty destination controls, keyboard/text routing, repeated Paste,
  undo/redo, and content-change availability. These checks do not capture screenshots.

Required Windows-host validation remains solution restore, Debug app build with `Platform=x64`,
Core and App tests, and existing StudioWindowCheck/StudioPreviewCheck runs. A macOS host cannot
execute WinUI's Windows-native `XamlCompiler.exe`, SDK `mt.exe`, DirectWrite `dwrite.dll`, or
the native clipboard/UI checks; compilation of managed code alone does not validate these.

Managed Core checks can run on Apple Silicon using supported MSBuild properties, without
editing runtime configuration files:

```sh
dotnet build windows/tests/TinyClips.Core.Tests/TinyClips.Core.Tests.csproj \
  -p:EnableWindowsTargeting=true -p:DisableTransitiveFrameworkReferences=true -p:Platform=ARM64
dotnet windows/tests/TinyClips.Core.Tests/bin/ARM64/Debug/net10.0-windows10.0.26100.0/TinyClips.Core.Tests.dll \
  -class '*StudioAnnotationClipboardTests' \
  -class '*StudioAnnotationTransferModelTests' \
  -class '*StudioAnnotationTransferSessionTests'
```

The framework-reference property omits the NAudio meta-package's transitive Windows Forms
framework from this managed test host; it does not substitute for Windows-native testing.
