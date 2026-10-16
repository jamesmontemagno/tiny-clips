# Tiny Clips Studio: editing the sound apart from the picture

**Status:** a proposal, written on 7 October 2026. Nothing here is built, and nothing here is
decided. It answers the owner's question: can the sound be edited by itself, so that parts of it
are taken out while the picture goes on, and should there be a separate sound track?

The short answer: yes. Taking sound out of a range is a small step from where the code is. A
separate sound file (a voice-over, music) is a larger one and waits on other work. This document
proposes the first, shapes the format so the second fits later, and lists what the owner has to
decide.

## What a project's sound is today

From `docs/studio-project-format.md` (sections 3 and 7) and the code:

- The sound is in the screen file. The Mac recorder writes the computer's sound and the
  microphone as two tracks and says which is which (`sources.screen.audioTracks`). The Windows
  recorder mixes both into one track.
- `audio.muted` takes all sound away. `audio.systemVolume` and `audio.microphoneVolume` set one
  level for the whole video, on the Mac only, because only there are the two apart.
- A cut (`edits.cuts`) removes picture and sound together. A speed change is silent for as long
  as it lasts.
- Nothing changes the sound over a part of the video and leaves the picture alone.
- The timeline shows no sound: no waveform, no lane.

How each platform plays it, which decides what is cheap:

| | macOS | Windows |
|---|---|---|
| Preview and export | One `AVAudioMix` with a level for each sound track (`StudioCompositionBuilder.audioMix`). A level that changes over time is a volume ramp on the same object | Export decodes the sound to PCM and writes it range by range (`StudioAudioPump`), filling silence where a speed change is. The preview plays through a media player that is muted and unmuted |
| Two kinds of sound apart | Yes | No. Needs the recorder to write two tracks, the follow-up already agreed on 5 October |

## Three things "editing the sound" can mean

1. **Silence a range.** From 12.0 to 14.5 seconds there is no sound, or no microphone, and the
   picture plays on. For a cough, a notification sound, a name said aloud, a keyboard clatter.
2. **Change the level over a range.** The same, with a level between silent and as recorded: the
   computer's sound turned down while you speak.
3. **Add a sound file.** A voice-over recorded afterwards, or music, with its own place in time,
   its own level, and its own silences.

What is deliberately not on the list: **taking sound out and closing the gap.** That slides
everything after it against the picture, so lips and clicks no longer match. To remove time, cut
the video; the Cut lane does that and takes the sound with it.

## Proposal

Build 1 first, in a way that makes 2 one more slider. Leave 3 as a stage of its own.

### Stage 1: silences

**What the project says.** A new list, in source time like zooms and cuts:

```
audio.ranges: [ { "start": 12.0, "end": 14.5, "target": "all", "level": 0 } ]
```

| Property | Type | Default | Notes |
|---|---|---|---|
| `start`, `end` | number | | Source time. Clamped to the recording; dropped when `end <= start` |
| `target` | `all`, `system`, `microphone` | `all` | Which sound it applies to. `system` and `microphone` apply only to a track of that kind; a `mixed` track hears only `all` |
| `level` | number | 0 | 0 is silent, 1 is as recorded. Clamped to 0 to 1. Stage 1 writes 0 only |

Rules to settle in the format document before any code:

- The level of a track at a source time is its volume (`systemVolume` or `microphoneVolume`)
  times the lowest `level` of the ranges that cover that time and apply to it. Overlap needs no
  rule of its own that way.
- Each end of a range is a ramp of 10 ms, inside the range, so that silence does not start with
  a click. Not a setting.
- Ranges stay in source time. A cut or the trim hides the part of a range it covers. A speed
  change is silent already.
- `audio.muted` still silences everything.

**Fixtures.** A handful in `shared/studio/fixtures`, from the Python reference: for a project and
a list of source times, the level of each track. Both test suites load them, as they do for the
time map.

**The editor.**

- A **Sound lane** on the timeline, under Speed: the waveform of the recording, with each range
  as a block drawn over it. Blocks are dragged and resized the way cuts are.
- **M** adds a one-second silence at the playhead, as X adds a cut. M is free on both platforms.
- The **Audio** panel of the inspector, which today holds Mute and the volumes, gains what the
  Cut panel has: Previous and Next, **Add Silence**, and for the selected one: **Applies to**
  (All sound, System audio, Microphone; only what the recording has), Start, End, Delete. Adding
  or selecting a silence shows the Audio panel, by the rule the rail already follows.
- The name is **Silence**, not "audio cut": Cut already means taking time out of the video.

**The waveform** is worked out once from the screen file and kept beside the project as a small
file of peaks. It is derived, not part of the format: a missing or stale one is made again, and
cleanup removes it with the sources. Where the two kinds of sound are apart, the lane draws both.

**macOS.** `StudioSound` (Foundation only, unit tested) gains the level of each track at a
source time. `StudioCompositionBuilder.audioMix` turns that into volume ramps, at output times
from the time map. Preview and export share that one object, so they cannot disagree.

**Windows.** `StudioAudioPump` already writes PCM a range at a time; it multiplies by the level
as it writes. The preview mutes its player for the length of a silence, as it does for a speed
change. Until the recorder writes two tracks, every Windows silence is `all`, and the Applies to
choice is not shown.

### Stage 2: levels

The Audio panel's selected range gets a **Level** slider, and `level` is written as set. The
format, the fixtures, and both mixers are ready from stage 1. The one new piece of work is the
Windows preview, which has to set a volume and not only mute.

### Stage 3: an added sound file

Sketched so that stage 1 does not close a door, and no further:

- `sources.audio: [ { "file", "duration", "startOffset", "volume" } ]`, a sound file copied
  into the project folder, with where it starts in source time.
- `target` in `audio.ranges` grows a way to name such a file.
- It needs a way to pick or record the file, a second waveform lane, a mixer with more inputs
  on both platforms, and a rule for sound that runs past the end of the picture.
- On Windows it depends on the same mixing work as two recorded tracks. Do that first.

## What this does not cover

- Sound in a speed change. Keeping the pitch needs time stretching and is its own project.
- Making sound louder than recorded. That needs a limiter, as the volumes section of the main
  plan says.
- Noise removal, levelling, captions.
- **Finding silences for you.** "Silence removal" in the dream backlog is the opposite edit: it
  finds quiet stretches and suggests *cuts*. The waveform peaks from stage 1 are what it would
  read, so stage 1 brings it closer without being it.

## Risks

- **A silence someone relied on is not honoured by an older build.** Unknown properties survive
  a save, so an older build would keep `audio.ranges` and play the sound anyway. No build with
  Studio has shipped, so adding the property before release costs nothing. After release it is
  a reason to raise `schemaVersion`.
- **Preview and export disagreeing by a few milliseconds on Windows**, where the preview mutes a
  player and the export multiplies samples. For a silence that is not heard; stage 2 has to check
  it with levels.
- **The lane count.** The timeline has Scene, Zoom, Cut, Speed, and the trim bar. A fifth lane
  with a waveform is taller than the others. The window's smallest height has to be looked at.
- **The Windows recorder's sound path** is the one that ships today. Nothing in stages 1 or 2
  touches it. Stage 3 and per-kind silences on Windows do.

## For the owner to decide

1. **Is stage 1 the right first step,** or is the added sound file (stage 3) what was meant by
   "a separate audio"? They are different amounts of work.
2. **Silence only, or levels from the start?** The format carries `level` either way.
3. **Per kind on the Mac from the start** (silence the microphone and keep the computer's
   sound), with Windows on `all` until it records two tracks?
4. **Does the Sound lane show the waveform in the first build,** or blocks on a plain lane first
   and the waveform after?
5. **In this pull request or after it?** #384 is large and not yet used by a person on Windows.

## Order of work, if the answers are yes

1. Format: section 7 gains the level rules; fixtures from the Python reference.
2. Both editor models: add, move, resize, delete, step, and the level at a time. Unit tests.
3. macOS: the audio mix, the Sound lane, the Audio panel, M.
4. Windows: the pump, the preview, the lane, the panel, M, `StudioWindowCheck`, the
   accessibility gate.
5. The waveform, if it was not in the first build.
6. Stage 2.
