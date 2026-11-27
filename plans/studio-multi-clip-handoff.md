# Studio multi-clip timeline: implementation and release checks

The [draft PR](https://github.com/jamesmontemagno/tiny-clips/pull/691) started with the shared v2 model at `899e5cf`. The macOS editor uses that model for assembly, playback, export, portable saves and Agent Control. Windows shares the file format and Agent contract, with native implementations for media storage and rendering. Keep [#591](https://github.com/jamesmontemagno/tiny-clips/issues/591) open for the release checks below.

## Decisions to carry to Windows

- Keep at least one playable clip. Removing the last clip, trimming away all playable time, or splitting inside a removed range is refused. A split keeps the left ID and all source-local layers; it does not copy media.
- Append by default. **Insert at Playhead** splits the active clip only if the position is inside it; an exact cut inserts at the boundary. A timeline drop indicates and uses the nearest clip edge. A multi-file picker/drop preserves the supplied order and is **one transaction and one undo step**; a bad or cancelled item leaves the previous edit intact.
- The primary strip and transport are in project time. Existing detail lanes and inspectors are in the active clip's source time. Selection and edits pause playback; during playback the inspector follows the active clip across cuts. Keep project canvas size and frame rate stable, and do not offer native camera or click layers on ordinary videos.
- Each join in the Mac clip strip has a menu: **Cut** (default), **Fade Through Black**, **Fade Through White**, or **Cross-Dissolve**. Only the dissolve overlaps video and audio and shortens the project, by at most half a second. The incoming clip owns the join, not its source media. Match the short-clip limits, split behavior and overlap selection in [the format](../docs/studio-project-format.md#16-multi-clip-projects-v2). Annotations are still clip-local, not a project-wide lane.
- The store stages copies and probes media before changing the manifest. Snapshot a linked single recording, not its export. Adopt a v1 destination's external original before conversion. Undo/removal never deletes a source. The capture flow retains the destination editor and uses ordinary capture permission and cancellation handling.
- Both apps use the shared [`studio.timeline.*` contract](../docs/agent-integration.md#assemble-a-studio-timeline) for CLI/MCP. Discovery is read-only even on v1; recheck access and the expected revision at commit, including *after* a recording finishes. Use the open editor's history.

## macOS code and test map

| Responsibility | Implementation | Regression coverage |
|---|---|---|
| Validation, split/insert and source/project time | `StudioTimelineEditing`, `StudioProject` | `StudioProjectTests`, `StudioFixtureTests` |
| Owned imports, transactional saves, portable folders | `StudioProjectStore` | `StudioProjectFolderTests` (real media, stale edits, rollback, originals removed) |
| Selection, history and input | `StudioEditorModel`, `StudioViewModel`, `StudioViews`, `StudioWindow`, `ClipsManagerWindow`, `CaptureManager` | `StudioEditorModelTests`, `StudioProjectFolderTests`, `StudioCaptureEventsTests` |
| Preview and export | `StudioCompositionBuilder`, `StudioCompositor`, `StudioPlayback`, `StudioExporter` | `StudioTimelineRenderingTests`, with native and rotated flat test cards under `mac/TinyClipsTests/Fixtures/StudioRendering/` |
| Agent access | `AutomationCommandRouter`, shared command/policy catalog and Studio skills | Mac `AutomationPolicyTests` and `agent/tests/TinyClips.Cli.Tests` |

**Verified on macOS (Apple Silicon, Xcode 27.2), including the join effects and preview layout fix:** the direct and Mac App Store Debug builds succeeded. The full Mac suite had 861 passes, one skip and no failures. It exercised atomic batch insertion, undo/redo, reopening with originals removed, decoded video and audio across joins, and detail-lane sizing across edits and selection. The CLI/MCP suite had 109 passes and two Windows-only skips; all three packaged-tool smoke tests passed.

**Exercised by hand on macOS (signed Debug build, synthetic clips):** a project was opened from a Library clip, then extended with a multi-select file import (one undo), a Library clip inserted at the playhead (split and insert undo together), a portrait clip (fitted inside 1920 × 1080) and Record Another Segment. A cancelled take left the project unchanged; a finished take was appended and selected. Split, trim start/end, move earlier/later and remove worked from the clip actions menu. Zoom, cut, speed and arrow, text, rectangle and redaction annotations stayed with their own clips through playback, close and reopen, and export. Export produced 9.48 s for a 9.4 s project with every edit present.

**Preview stability ([#693](https://github.com/jamesmontemagno/tiny-clips/issues/693)):** the walkthrough found that the preview jumped when the active clip had fewer detail lanes. Studio now reserves the height of the tallest clip's lanes while showing only the active clip's edits. In an offscreen Studio window at 1180 × 792 points, the former 82-point preview height change fell to zero. Preview and transport bounds stayed identical across both clip selection and playback, including at the 980 × 672 minimum size.

**Remaining manual observations, none blocking:** overlapping annotation labels draw over each other in one lane; a recorded take also remains as its own Studio draft; and ⌘A does nothing in text fields because the app has no Edit → Select All. That last one predates this work: `ScreenshotEditorScene` replaces the `.pasteboard` command group app-wide.

**Transition regression coverage (added after the hands-on run):** `StudioTimelineRenderingTests` samples the same joins in player-composed frames and the decoded MP4, including a redaction visible on only the outgoing picture. It checks the audio mix and decoded sound, and that four seconds of clips become 3.5 seconds with one half-second dissolve. Model and Agent tests cover short neighboring clips, selection inside an overlap, undo/redo, persistence and revision-checked transition edits.

**Not exercised manually:** the new join menu, keyboard-only and VoiceOver operation of the complete flow, or moving a saved project between Mac and Windows. These remain release checks below, not claims about what the automated suite covers.

## Reproduce the macOS checks

From a local clone, use full Xcode and run the following in zsh or bash. Skip the checkout command if already in the PR worktree.

```sh
gh pr checkout 691
xcodebuild -version
project=mac/TinyClips.xcodeproj
debug=(-project "$project" -configuration Debug CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO)
xcodebuild -resolvePackageDependencies -project "$project" -scheme TinyClips
xcodebuild build -scheme TinyClips "${debug[@]}"
xcodebuild test  -scheme TinyClips "${debug[@]}"
xcodebuild build -scheme TinyClipsMAS "${debug[@]}"
```

For a fast loop, add `-only-testing:TinyClipsTests/StudioFixtureTests -only-testing:TinyClipsTests/StudioProjectTests` to the test command. Start with `testSharedMultiClipProjectFixtureMapsNativeAndFlatSources`: in the [shared fixture](../shared/studio/fixtures/timeline/two-clips-native-flat.json), the native clip's trim, cut, and speed edits leave **6 seconds**. Output time `6.0` belongs to the flat clip at source time `0.0`; total output is **11 seconds**. This checks the **map**, not decoded video or sound.

These commands disable code signing for build and unit-test checks. For manual recording, open `mac/TinyClips.xcodeproj` in Xcode, run the `TinyClips` scheme with your normal local signing setup, and grant the capture permissions the app requests.

## Windows implementation and acceptance criteria

| Step | Where to start | Done when |
|---|---|---|
| 1. Match edit validation | `StudioTimelineEditing.cs` and `StudioJson.cs`; apply the refusal rules above on read and before save. | Duplicate IDs, unknown sources, empty clips and invalid splits are rejected. Opening v1 does not convert it. |
| 2. Own imported media | Windows `StudioProjectStore`: stage, probe and copy the source before changing the manifest. Include a flat destination's external original. Include native layers, referenced assets and the source's current edits in a linked import. | Save As, move the originals, reopen. A bad second file, disk error or cancellation leaves the original project and all user files intact. |
| 3. Connect history | `StudioEditorModel` and `Studio/Editing/StudioEditorSession`: put the lane and registry, not export bookkeeping or selection, in history. | Each edit undoes/redoes in one step, survives reopening and cannot overwrite a stale revision. |
| 4. Render the lane | `Studio/Preview` and `Studio/Rendering`: share the project clock, source times and per-clip layers; decode both neighbors only for a dissolve. | Compare frames and sound before, during and after cuts, both color fades and dissolves, including camera offset, redactions, rotation and speed changes. |
| 5. Wire editor inputs | Studio views, Library and capture service: file picker/drop, Library snapshot and **Record another segment**. | Success returns to the same editor; cancellation preserves selection/edits. Keyboard and Narrator can perform every lane operation. |
| 6. Enable Windows Agent Control | Implement and advertise the existing shared commands only when the Windows handlers support them. Recheck scopes, OS consent, approval and revision after staging/recording. | A paired client assembles, edits and exports without driving the UI. Update capability metadata, policy tests and docs when enabling Windows. |

**Windows store:** `StudioTimelineMediaStoreTests` covers staging, v1 conversion, rollback, stale revisions, linked recording snapshots, and reopening a portable copy after moving or deleting the originals. Unused sources stay in the registry and in portable copies.

**Windows renderer:** run `dotnet run --project windows\tools\StudioRenderCheck\StudioRenderCheck.csproj -c Debug -p:Platform=x64 -- --only timeline`. On Windows, all seven checks passed, including decoded video and audio, posters and live preview across the four joins; the Mac camera-offset and portrait cards; software rendering; and cancellation without replacing the destination. Two two-second clips produced 4.0 seconds for cuts and color fades and 3.5 seconds for a dissolve. The generated signal correlated at 1.0000 for each join. This is not a physical Mac-to-Windows transfer or a listening test.

## Release checks (on the respective hosts)

- **Mac:** repeat both builds and the full tests above. Use a disposable project to assemble at least three clips via recording, file import and Library; change a join, reorder, split, trim, undo/redo, reopen and export. Repeat the editing flow with keyboard and VoiceOver; exercise a cancelled take and a failed import.
- **Windows:** use a Windows host or [Windows CI](../.github/workflows/windows-build.yml), not Xcode. Restore `windows/TinyClips.Windows.slnx`, build the direct x64 and ARM64 targets and the Store flavor (`TinyClipsStoreBuild=true`), and run `TinyClips.Core.Tests` and `TinyClips.App.Tests`. Repeat the three-clip flow with keyboard and Narrator.
- **Portability and Agent:** transfer the saved folder from Mac to Windows and back with the originals unavailable. Compare output dimensions, duration, images and sound at the same boundaries, not encoder bytes. Run the shared policy/contract and CLI/MCP tests, then repeat assembly and export through Agent Control. Keep #591 open until both platforms pass.

One primary lane with optional join effects is the scope. Whole-project merging, independently overlapping video tracks, project-wide annotations, AI editing and independent audio editing are not required.
