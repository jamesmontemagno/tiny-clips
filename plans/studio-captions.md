# Tiny Clips Studio: captions

**Status:** in progress, started 5 November 2026. Stages 1 to 6 below are in place, except for
SpeechAnalyzer on the Mac, which needs a real Mac to test. The format is specified in section 15 of
[`docs/studio-project-format.md`](../docs/studio-project-format.md), which decides if this
document and the code disagree.

Captions can be generated on the device, edited in Studio, exported as SRT or WebVTT, and burned
into the picture of an exported video. They work on macOS and Windows from one shared format.

## Decisions

- **Both platforms, one format.** Cues live in `project.json`, so a project saved as a folder
  carries them to the other platform.
- **"Embed" means burning into the picture**, plus SRT and VTT files. A subtitle track inside the
  MP4 that a player can turn on and off is left out.
- **Speech engines.** Windows uses Whisper.net. macOS uses WhisperKit. SpeechAnalyzer on macOS 26
  and later was the plan, and can still sit behind the same protocol once it can be tested.
- **Audio never leaves the device.**
- **No new Pro gate.** The Studio views have none today.
- **Burn-in is off by default.**

## What comes from OutroKit

[OutroKit](https://github.com/jamesmontemagno/outrokit) (MIT) has most of this built, but its
burn-in and its audio extraction run ffmpeg, which neither app ships and the sandboxed Mac App
Store build cannot launch. Its Core project also brings in the Copilot SDK, which it uses for AI
titles and descriptions and not for captions. So Tiny Clips does not depend on OutroKit: the logic
is ported and the pipeline is not.

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

1. **Mac spike (done, on a GitHub macOS 26 runner).** WhisperKit does what Studio needs; see
   What the spike found. SpeechAnalyzer could not be tried: it is not available in a VM.
2. **Foundation (done).** Format spec, models, `studio.project.edit` access, the cue resolver, the
   SRT and VTT reader and writer, and shared fixtures. No UI.
3. **Files (done).** The Captions inspector panel (cue list where a click seeks and text is
   editable, Import, Auto Fix, Export), and `studio.captions.import`, `check`, `fix`, and `export`.
   Useful by itself with an SRT from OutroKit. Imports assume recording time, with an option for
   files timed to the edited video. Changing a cue's times by hand, and adding a cue, are not in
   this stage.
4. **Burn-in (done).** Both renderers, the preview, style controls, and a `StudioRenderCheck` group.
   - Windows draws in `StudioSceneRenderer` with DirectWrite, which comes with `Vortice.Direct2D1`,
     so no package was added. `StudioPreviewStillness.SameLayout` returns false across a cue change.
   - macOS draws cached Core Text images in `StudioCompositor`. `containsTweening` is already true.
5. **Windows transcription (done).** Whisper.net, model management in Studio settings, and
   `studio.captions.models` and `studio.captions.generate`. Built ahead of stage 4, because making
   captions is more use than drawing them. It listens on the CPU, in the language it detects.
6. **macOS transcription (done, with WhisperKit).** `com.apple.security.network.client` in the Mac
   App Store entitlements for its downloads. SpeechAnalyzer is left until a real Mac can test it.
   The privacy answers in App Store Connect still need checking by whoever submits the build.

Transcription is a cancellable background job behind one interface, applied as a single undoable
edit. Models download only after the user agrees to the size. `studio.captions.generate` downloads
one only when `allowModelDownload` is set.

## What the spike found

On a GitHub macOS 26 runner (a virtual M1 with 3 cores) with the Base model: 21 seconds of speech
took 0.9 s, and 20 minutes took about a minute through the app's own code, with all 3,078 words
there. It has word timings, detected Spanish from real speakers, and downloads 146 MB from Hugging
Face. Version 1 of the package brings in only `swift-argument-parser`. Long segments need breaking
into cues, which Whisper.net does itself. A synthetic Spanish voice from `say` came out as Latin
and garbled, so detection is only as good as the speech.

## Open questions

The first needs a real Mac, and the second needs a submission:

- **SpeechAnalyzer:** long-file reliability, word timings, whether it needs Speech permission, and
  a language picker, since it seems to need a chosen language rather than detecting one. On a
  GitHub macOS 26 VM, `SpeechTranscriber.isAvailable` is false with no supported locales.
- **App Review:** how the Mac App Store network entitlement fares.
- **Project size:** test an hour-long project, because Windows compares undo state as serialized
  text (`StudioEditableState.ContentEquals`).
- **Package size:** captions add about 2.8 MiB to the MSIX and 7 MiB once installed.
  `Whisper.net.Runtime` copies the libraries of all three Windows architectures, so about 3.4 MiB
  of the installed size is libraries a package cannot use. Trim it if it matters.
- **Language:** Windows detects the language, and a recording can be heard as the wrong one. A
  language picker can use the same setting SpeechAnalyzer needs on macOS.
- **GPU:** Whisper.net has Vulkan and CUDA runtimes. Both are large, and neither is in the package.
- **Long recordings:** the sound is held in memory while it is heard, about 230 MB an hour.
- **System sound in captions:** the Windows recorder writes one mixed track, so what the computer
  played can end up in the captions. macOS prefers the microphone track.
