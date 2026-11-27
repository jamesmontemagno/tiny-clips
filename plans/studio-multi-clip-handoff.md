# Studio multi-clip timeline: macOS implementation and Windows pickup

The [draft PR](https://github.com/jamesmontemagno/tiny-clips/pull/691) started with the shared v2 model at `899e5cf`. The macOS editor now uses that model for assembly, playback, export, portable saves and Agent Control. **Windows still has the model only.** Keep [#591](https://github.com/jamesmontemagno/tiny-clips/issues/591) open for the Windows implementation and cross-platform proof.

## Decisions to carry to Windows

- Keep at least one playable clip. Removing the last clip, trimming away all playable time, or splitting inside a removed range is refused. A split keeps the left ID and all source-local layers; it does not copy media.
- Append by default. **Insert at Playhead** splits the active clip only if the position is inside it; an exact cut inserts at the boundary. A timeline drop indicates and uses the nearest clip edge. A multi-file picker/drop preserves the supplied order and is **one transaction and one undo step**; a bad or cancelled item leaves the previous edit intact.
- The primary strip and transport are in project time. Existing detail lanes and inspectors are in the active clip's source time. Selection and edits pause playback; during playback the inspector follows the active clip across cuts. Keep project canvas size and frame rate stable, and do not offer native camera or click layers on ordinary videos.
- The store stages copies and probes media before changing the manifest. Snapshot a linked single recording, not its export. Adopt a v1 destination's external original before conversion. Undo/removal never deletes a source. The capture flow retains the destination editor and uses ordinary capture permission and cancellation handling.
- The shared [`studio.timeline.*` contract](../docs/agent-integration.md#assemble-a-studio-timeline-macos) and CLI/MCP support are in this PR. Implement the Windows handlers against these commands, not new names. Discovery is read-only even on v1; recheck access and the expected revision at commit, including *after* a recording finishes. Use the open editor's history.

## macOS code and test map

| Responsibility | Implementation | Regression coverage |
|---|---|---|
| Validation, split/insert and source/project time | `StudioTimelineEditing`, `StudioProject` | `StudioProjectTests`, `StudioFixtureTests` |
| Owned imports, transactional saves, portable folders | `StudioProjectStore` | `StudioProjectFolderTests` (real media, stale edits, rollback, originals removed) |
| Selection, history and input | `StudioEditorModel`, `StudioViewModel`, `StudioViews`, `StudioWindow`, `ClipsManagerWindow`, `CaptureManager` | `StudioEditorModelTests`, `StudioProjectFolderTests`, `StudioCaptureEventsTests` |
| Preview and export | `StudioCompositionBuilder`, `StudioCompositor`, `StudioPlayback`, `StudioExporter` | `StudioTimelineRenderingTests`, with native and rotated flat test cards under `mac/TinyClipsTests/Fixtures/StudioRendering/` |
| Agent access | `AutomationCommandRouter`, shared command/policy catalog and Studio skills | Mac `AutomationPolicyTests` and `agent/tests/TinyClips.Cli.Tests` |

**Verified on macOS (Apple Silicon, Xcode 27.2):** the direct and Mac App Store Debug builds succeeded. The full Mac suite had 856 passes, one skip and no failures. It exercised atomic batch insertion, undo/redo, reopening with originals removed, and decoded video and audio across cuts. The CLI/MCP suite had 108 passes and two Windows-only skips; all three packaged-tool smoke tests passed.

**Not exercised manually:** live permissioned recording back into the editor, keyboard and VoiceOver operation of the complete flow, or moving a saved project between Mac and Windows. These remain release checks below, not claims about what the automated suite covers.

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

**Known Windows build blocker at `899e5cf`:** [x64, ARM64 and Store CI](https://github.com/jamesmontemagno/tiny-clips/actions/runs/45870277698) reported `CS0104` at `StudioWindow.xaml.cs:46`: `StudioTimeline` refers to both the WinUI control and the new Core model. Qualify or alias the control where the view uses it, then rebuild all three targets. The Core test job was skipped, not passed.

## Windows implementation order

| Step | Where to start | Done when |
|---|---|---|
| 1. Match edit validation | `StudioTimelineEditing.cs` and `StudioJson.cs`; apply the refusal rules above on read and before save. | Duplicate IDs, unknown sources, empty clips and invalid splits are rejected. Opening v1 does not convert it. |
| 2. Own imported media | Windows `StudioProjectStore`: stage, probe and copy the source before changing the manifest. Include a flat destination's external original. Include native layers, referenced assets and the source's current edits in a linked import. | Save As, move the originals, reopen. A bad second file, disk error or cancellation leaves the original project and all user files intact. |
| 3. Connect history | `StudioEditorModel` and `Studio/Editing/StudioEditorSession`: put the lane and registry, not export bookkeeping or selection, in history. | Each edit undoes/redoes in one step, survives reopening and cannot overwrite a stale revision. |
| 4. Render the lane | `Studio/Preview` and `Studio/Rendering`: share active clip, source time and style resolution; use one project canvas/frame rate and scope decoder use to active/adjacent clips. | Compare frame, audio and caption timing before, at and after cuts with camera offset, rotation and speed changes. |
| 5. Wire editor inputs | Studio views, Library and capture service: file picker/drop, Library snapshot and **Record another segment**. | Success returns to the same editor; cancellation preserves selection/edits. Keyboard and Narrator can perform every lane operation. |
| 6. Enable Windows Agent Control | Implement and advertise the existing shared commands only when the Windows handlers support them. Recheck scopes, OS consent, approval and revision after staging/recording. | A paired client assembles, edits and exports without driving the UI. Update capability metadata, policy tests and docs when enabling Windows. |

**Suggested first Windows slice:** fix the type-name collision above, then run the equivalent of the Mac batch-import/reopen and preview/export boundary tests through the native Windows store, editor and renderer before wiring the other entry points. Use the Mac implementations above as behavior references, not UI or AVFoundation ports.

## Release checks (on the respective hosts)

- **Mac:** repeat both builds and the full tests above. Use a disposable project to assemble at least three clips via recording, file import and Library; reorder, split, trim, undo/redo, reopen and export. Repeat the editing flow with keyboard and VoiceOver; exercise a cancelled take and a failed import.
- **Windows:** use a Windows host or [Windows CI](../.github/workflows/windows-build.yml), not Xcode. Restore `windows/TinyClips.Windows.slnx`, build the direct x64 and ARM64 targets and the Store flavor (`TinyClipsStoreBuild=true`), and run `TinyClips.Core.Tests` and `TinyClips.App.Tests`. Repeat the three-clip flow with keyboard and Narrator.
- **Portability and Agent:** transfer the saved folder from Mac to Windows and back with the originals unavailable. Compare output dimensions, duration, images and sound at the same boundaries, not encoder bytes. Run the shared policy/contract and CLI/MCP tests, then repeat assembly and export through Agent Control. Keep #591 open until both platforms pass.

One sequential lane and plain cuts are the scope. Whole-project merging, overlapping video tracks, new inter-clip transitions, AI editing and independent audio editing are not blockers.
