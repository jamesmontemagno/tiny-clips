# Studio multi-clip timeline: Mac pickup and remaining work

The [draft PR](https://github.com/jamesmontemagno/tiny-clips/pull/691) implements the **project model**, not the editor workflow in [#591](https://github.com/jamesmontemagno/tiny-clips/issues/591). At `899e5cf`, Swift and C# both have explicit v1-to-v2 conversion, stable source/clip IDs, source-to-output time mapping, and append/insert/move/split/trim/remove primitives. Opening a v1 project does not opt it into v2. Neither editor uses those primitives to assemble or render a movie yet.

## First on your Mac: establish a baseline

From a local clone of `jamesmontemagno/tiny-clips`, run the following in zsh or bash. Use full Xcode; the [macOS CI job](../.github/workflows/build.yml) uses Xcode 26.2. The Linux session cross-compiled Windows Core and its test assembly but could not run the native tests or build either Mac scheme. Check the [PR checks](https://github.com/jamesmontemagno/tiny-clips/pull/691/checks) for native results.

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

## Implementation order

| Step | Where to start | Done when |
|---|---|---|
| 1. Make edit results safe to persist | `StudioTimelineEditing.swift`, `StudioTimelineEditing.cs`, the two project readers, and [format section 16](../docs/studio-project-format.md#16-multi-clip-projects-v2). Exercise removal of the last clip, a split inside a cut, trim leaving no playable time, duplicate IDs, and missing source references. Define refusal or empty-lane semantics consistently; currently the readers require a nonempty lane but removal can return an empty one. | Both readers and editors accept or reject the same shared cases. Every accepted edit survives save/reload. Opening a v1 project still leaves its version, editability and output intact. |
| 2. Make projects own imported media | Extend the Mac and Windows `StudioProjectStore` folder save/import paths. Stage and probe source files before committing the manifest. Copy an ordinary video as a flat source; for a Library export linked to a single-recording project, snapshot the current editable recording, camera, events, edits and referenced assets instead of importing the export. Include an external v1 original when converting a flat destination. | Move the originals away: the destination still resolves every source, including after portable Save As. Missing, malformed or unsupported media, insufficient disk space and cancellation leave the old manifest and timeline untouched. Removal/cleanup never deletes originals or media needed by undo, drafts or an open project. |
| 3. Connect the model to editor history | Mac: `StudioEditorModel` and `Views/Studio/StudioViewModel.swift`. Windows: `StudioEditorModel` and `Studio/Editing/StudioEditorSession`. Put the lane and source registry in the undoable state, separate from export bookkeeping; resolve selection by clip ID and the playhead in project time. Route a successful import or timeline edit through one transaction. | Each edit undoes/redoes in one step, autosaves and reopens in order. Reorder/split do not retime source-local captions, scenes, zooms, annotations, audio or capture events. A stale revision cannot overwrite a newer edit. |
| 4. Share timing between preview and export | Mac: `Studio/Rendering/StudioCompositionBuilder`, `StudioPlayback`, `StudioExporter`. Windows: `Studio/Preview` and `Studio/Rendering`. Resolve the active clip, its source time and its style from the same map; keep one project canvas and output frame rate. Fit rotated/mixed-size sources without stretching. Keep scene transitions inside their clip and load decoders only for active/adjacent clips. | Seek/play and export just before, at and after a cut. Compare frames, audio and caption timing for a native clip with camera offset, a rotated flat clip and a different-rate clip. No previous clip's camera, events, or sound leaks across the cut. |
| 5. Add the Studio entry points | Start on Mac in `Views/Studio`, `CaptureManager` and the Clips Library; mirror in the Windows Studio views, view models and capture service. Add file picker, file drop, Library and **Record another segment** to the existing project. Append by default, offer **Insert at playhead**, and show the legal boundary a timeline drop will use. Reuse capture setup and permissions. | Successful capture returns to the same editor at the requested position. Cancellation/failure preserves unsaved work and selection; errors say how to recover. Clip actions work without dragging, have accessible names, and expose only the layers the source has. |
| 6. Expose the same operations through Agent Control | Extend `shared/automation/commands.json`, policy fixtures, both app handlers, CLI/MCP and the Studio/recording Skills. Provide source/clip discovery, import, targeted recording and lane edits using stable IDs. Recheck the expected revision at commit, including after recording; use the open editor's undo path. | A paired agent assembles, edits and exports without driving the UI. Test stale revisions, invalid paths and denied scopes. Keep record/edit/export permissions, platform consent, Store restrictions and auditing; removing a clip is not permission to delete a source. Update the agent integration guide and changelogs with the shipped behavior. |

**Suggested first vertical slice:** after the baseline, get one file-picker import into an existing Mac project through steps 1-4: copy it, append it, undo/redo, close/reopen, and export across the cut. This exercises the store, history and renderer together before adding the other entry points.

## Release checks (on the respective hosts)

- **Mac:** repeat both builds and the full tests above. Use a disposable project to assemble at least three clips via recording, file import and Library; reorder, split, trim, undo/redo, reopen and export. Repeat the editing flow with keyboard and VoiceOver; exercise a cancelled take and a failed import.
- **Windows:** use a Windows host or [Windows CI](../.github/workflows/windows-build.yml), not Xcode. Restore `windows/TinyClips.Windows.slnx`, build the direct x64 and ARM64 targets and the Store flavor (`TinyClipsStoreBuild=true`), and run `TinyClips.Core.Tests` and `TinyClips.App.Tests`. Repeat the three-clip flow with keyboard and Narrator.
- **Portability and Agent:** transfer the saved folder from Mac to Windows and back with the originals unavailable. Compare output dimensions, duration, images and sound at the same boundaries, not encoder bytes. Run the shared policy/contract and CLI/MCP tests, then repeat assembly and export through Agent Control. Keep #591 open until both platforms pass.

One sequential lane and plain cuts are the scope. Whole-project merging, overlapping video tracks, new inter-clip transitions, AI editing and independent audio editing are not blockers.
