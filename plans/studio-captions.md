# Tiny Clips Studio: captions

**Status:** in progress, started 5 November 2026. Stage 2 below is in place; everything after it
is not built yet. The format is specified in section 15 of
[`docs/studio-project-format.md`](../docs/studio-project-format.md), which decides if this
document and the code disagree.

Captions can be generated on the device, edited in Studio, exported as SRT or WebVTT, and burned
into the picture of an exported video. They work on macOS and Windows from one shared format.

## Decisions

- **Both platforms, one format.** Cues live in `project.json`, so a project saved as a folder
  carries them to the other platform.
- **"Embed" means burning into the picture**, plus SRT and VTT files. A subtitle track inside the
  MP4 that a player can turn on and off is left out.
- **Speech engines.** Windows uses Whisper.net. macOS uses SpeechAnalyzer on macOS 26 and later,
  and WhisperKit on macOS 15 or where SpeechAnalyzer does not support the machine or language.
- **Audio never leaves the device.**
- **No new Pro gate.** The Studio views have none today.
- **Burn-in is off by default.**

## What comes from OutroKit

[OutroKit](https://github.com/jamesmontemagno/outrokit) (MIT) has most of this built, but its
burn-in and its audio extraction run ffmpeg, which neither app ships and the sandboxed Mac App
Store build cannot launch. Its Core project also brings in the Copilot SDK. So the logic is ported
and the pipeline is not.

| OutroKit | In Studio |
|---|---|
| `CaptionStyle`, `BuildForceStyle` values | The sizes, positions, and looks in section 15.4 of the format |
| `CaptionQualityService` | Ported to C#, rewritten in Swift, tested against shared fixtures |
| `WhisperModelCatalog`, `WhisperModelService` | Windows only |
| `MediaTranscriptService` | Windows keeps the Whisper.net loop, takes its audio from `StudioAudioMixSource` in place of ffmpeg, and adds `WithTokenTimestamps` |
| `SrtConverter` | Replaced by the shared reader and writer in section 15.3 |
| `BurnAsync`, `FfmpegRunner` | Not used. Both renderers draw captions themselves |

## Stages

Each stage is a pull request that updates the changelogs and validates the platforms it touches.

1. **Mac spike.** Needs a Mac; see Open questions.
2. **Foundation (done).** Format spec, models, `studio.project.edit` access, the cue resolver, the
   SRT and VTT reader and writer, and shared fixtures. No UI.
3. **Files.** The Captions inspector panel (cue list where a click seeks and text is editable,
   Import, Auto Fix), **Export Captions…**, and `studio.captions.export`. Useful by itself with an
   SRT from OutroKit. Imports assume recording time, with an option for files timed to the edited
   video.
4. **Burn-in.** Both renderers, the preview, style controls, and a `StudioRenderCheck` group.
   - Windows draws in `StudioSceneRenderer` with DirectWrite, which needs the `Vortice.DirectWrite`
     package. `StudioPreviewStillness.SameLayout` must return false across a cue change.
   - macOS draws cached Core Text images in `StudioCompositor`. `containsTweening` is already true.
5. **Windows transcription.** Whisper.net, model management in Studio settings, and
   `studio.captions.generate`.
6. **macOS transcription.** Both engines, `com.apple.security.network.client` in the Mac App Store
   entitlements for WhisperKit downloads, and updated privacy text in the store listings.

Transcription is a cancellable background job behind one interface, applied as a single undoable
edit. Models download only after the user agrees to the size. `studio.captions.generate` downloads
one only when `allowModelDownload` is set.

## Open questions

Stage 1 answers the first two, on a Mac:

- **SpeechAnalyzer:** long-file reliability, word timings, whether it needs Speech permission, and
  a language picker, since it seems to need a chosen language rather than detecting one.
- **WhisperKit:** word timings, and how the Mac App Store network entitlement fares in App Review.
- **Project size:** test an hour-long project, because Windows compares undo state as serialized
  text (`StudioEditableState.ContentEquals`).
- **Package size:** `Whisper.net.Runtime` also pulls in a Metal package. Check the Windows MSIX.
- **System sound in captions:** the Windows recorder writes one mixed track, so what the computer
  played can end up in the captions. macOS prefers the microphone track.
