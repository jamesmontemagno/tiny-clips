---
description: "Use when changing what happens after a screenshot is captured on macOS or Windows: save/clipboard/editor outputs, temporary screenshot files, screenshot recovery, screenshot analytics, review prompts, onboarding or settings toggles, or agent screenshot settings."
applyTo: "mac/TinyClips/CaptureManager.swift, mac/TinyClips/Capture/ScreenshotCapture.swift, mac/TinyClips/Services/SaveService.swift, mac/TinyClips/Services/StoreReviewPrompt.swift, mac/TinyClips/Models/CaptureSettings.swift, mac/TinyClips/Automation/AutomationSettings.swift, mac/TinyClips/Views/ScreenshotEditor*.swift, mac/TinyClips/Views/OnboardingWizardWindow.swift, mac/TinyClips/Views/Settings/ScreenshotSettingsSection.swift, windows/src/TinyClips.Core/Capture/Screenshot*.cs, windows/src/TinyClips.Core/Services/Screenshot*.cs, windows/src/TinyClips.Core/Services/*CaptureSettings.cs, windows/src/TinyClips.Core/Automation/AutomationSettingsCatalog.cs, windows/src/TinyClips.App/App.xaml.cs, windows/src/TinyClips.App/App.Automation.cs, windows/src/TinyClips.App/Services/ClipboardService.cs, windows/src/TinyClips.App/Services/Screenshot*.cs, windows/src/TinyClips.App/Views/Screenshot*, windows/src/TinyClips.App/Controls/Settings/Screenshot*, windows/src/TinyClips.App/ViewModels/OnboardingViewModel.cs, windows/src/TinyClips.App/Views/OnboardingWindow.xaml*"
---

# Screenshot Output Flows (macOS + Windows)

Interactive screenshots (normal and scrolling) have three **independent** outputs: **Save file to disk**, **Copy to clipboard**, and **Open editor**. Any combination is valid except all three off. Video and GIF saving are separate and must not be routed through this flow. The design history is in issue #668.

Keep both platforms behaviorally aligned. A change to one platform's flow usually needs the matching change on the other.

## 1. Never break the "at least one output" invariant

- Change outputs only through the policy helpers. Never write the three preference keys directly from UI, onboarding, migrations, or automation:
  - mac: `CaptureSettings.setScreenshotOutput(_:enabled:)`, `canSetScreenshotOutput`, and `screenshotOutputBinding(...)` (`Models/CaptureSettings.swift`).
  - Windows: `ScreenshotOutputPolicy.TrySet` (`TinyClips.Core/Services/ScreenshotOutputPolicy.cs`).
- If a toggle is rejected, the UI must snap back and explain why. On Windows, use `ScreenshotOutputToggleFeedback.Reconcile`. On mac, use the binding from `screenshotOutputBinding`. Reuse these helpers for any new place that edits outputs, such as a new onboarding page or a tray/menu shortcut.
- Agent `settings.set` for `copyScreenshotToClipboard` or `showScreenshotEditor` returns `conflict` without changing anything if it would turn off the last output. The **Save file to disk** preference is intentionally **not** exposed to agents. Do not add it without asking the user.
- macOS migration sets the new key to `legacySaveImmediately || !showScreenshotEditor`, and an already-migrated value wins. Resetting settings removes the legacy key. Windows ignores its old, unused save-immediately preference. Do not reintroduce legacy keys as sources of truth.

## 2. Do not assume a screenshot was saved

When saving is off, the captured image is a **temporary** file in app-private storage, not a clip.

- Read outputs once per capture with a snapshot: mac `ScreenshotOutputPolicy(settings:)`, Windows `ScreenshotOutputPolicy.Read`. Do not re-read settings partway through routing.
- Gate any post-capture behavior that relates to a saved file on the permanent result. On mac, check `ScreenshotOutputPolicy.destination == .saved`. On Windows, check `ScreenshotRoutingResult.IsPermanent`/`ShouldPresentSavedResult`. This covers features such as adding to the Clips Library or Recent, showing saved notifications, revealing in Finder/Explorer, uploading, sharing, filing into capture folders, or naming. With saving off, do nothing; the user can save explicitly from the editor.
- Never persist a temporary path anywhere that outlives the capture, including Recent, Library, settings, analytics, Studio, or agent results.
- Explicit editor **Save**/**Save As** creates a persistent file and must not target the temporary directory. On mac, check `TinyClipsTemporaryFiles.canSaveScreenshotPermanently`.

## 3. Keep the default save path simple

With saving on, both platforms write the captured image **directly** to the generated capture URL, as before #668. Do not add temp-then-move, hard links, file identity checks, or reparse-point handling to this path; that machinery was removed on purpose. For a name collision, keep the existing behavior: overwrite or use the next numbered name.

## 4. Temporary file lifetime

- Create temporary screenshots only through the lease APIs:
  - mac: `TinyClipsTemporaryFiles.makeScreenshotLease` → `ScreenshotTemporaryFileLease`. Editors keep the file alive with `TinyClipsTemporaryFiles.protectEditorFile`.
  - Windows: `ScreenshotTemporaryFileStore.Reserve` → `ScreenshotTemporaryFileLease`. Use `Retain()` to hand the file to another owner.
- Any asynchronous consumer must hold its own retained lease and release it when done. This includes an editor, the clipboard, recovery, or any future share or OCR step. The file is deleted when the last lease is released, usually when the editor closes.
- At startup, both platforms delete leftover screenshot temporaries older than one day. On mac, `removeStaleFiles(olderThan:avoiding:)` runs in `TinyClipsApp.swift`. On Windows, `ScreenshotTemporaryCleanupService.PurgeStaleAsync` runs in `App.xaml.cs`. Do not shorten this window or run the sweep while captures may be active. The manual **Clear temporary files** action is the only immediate purge.
- On Windows, a temporary screenshot's clipboard copy publishes **bitmap bytes** through `ClipboardService.CopyTemporaryScreenshotAsync`, not a file reference, so it still pastes after the file is deleted. Persistent screenshots keep their existing clipboard formats.
- On mac, build Retina clipboard images from an `NSBitmapImageRep` whose `size` is set to the logical point size. `NSImage(cgImage:size:)` loses 2× density.

## 5. Recovery, analytics, and prompts

- If no output succeeds, the user must be offered recovery, not a silently dropped capture. Examples include a failed write, a failed clipboard copy with no editor, or an editor that could not open or did not fit. Recovery retries only the outputs that have not succeeded yet. For example, it never republishes a clipboard copy that already succeeded (Windows `ScreenshotRecoveryProgress`). Closing an editor-only capture is an intentional discard, not a failure.
- Count a screenshot in analytics **once**, and only after at least one output succeeds, across all retries and recovery. Do not record it at capture time. On mac, use the `outputSucceeded` callback in `ScreenshotOutputRouter.deliver` and `ScreenshotOutputSuccess`. On Windows, use the `recordCapture` parameter on `ScreenshotCaptureRouter.RouteAsync` and `ScreenshotRoutingResult.AnalyticsRecorded`.
- On mac, store review prompts wait while `CaptureManager.isScreenshotRecoveryInProgress` is true (`StoreReviewPrompt.swift`). Any new prompt, What's New, or modal triggered by capture counts must also defer while a recovery alert is open.

## 6. Agent and automation commands

`capture.screenshot` and `capture.scrolling.stop` always produce a **persistent** file, regardless of the interactive save preference. Do not route agent captures through the temporary path. Commands that return a path, such as `editor.annotate` or OCR follow-ups, must return persistent paths. Update `shared/automation/README.md`, `docs/agent-integration.md`, and `agent/CHANGELOG.md` if this contract changes.

## 7. Tests and validation

- Add regression coverage next to the existing tests:
  - mac: `SaveServiceTests.swift`, `CaptureSettingsTests.swift`, `CaptureAnalyticsStoreTests.swift`, and `AutomationPolicyTests.swift`.
  - Windows: `ScreenshotCaptureRouterTests.cs`, `ScreenshotTemporaryFileStoreTests.cs`, `CaptureSettingsTests.cs`, `AutomationSettingsCatalogTests.cs`, `ScreenshotClipboardFormatTests.cs`, and `ScreenshotOutputUiMarkupTests.cs`.
- Test routing and policy logic with fakes and an isolated temporary directory. Do not capture real screens or read or write the real clipboard in tests.
- When adding a new post-capture feature, test at least these combinations: save only, clipboard only, editor only, and all three. Also test a failed output followed by recovery.
- Validate both platforms when the flow changes. The build and test commands are in `.github/copilot-instructions.md`.
