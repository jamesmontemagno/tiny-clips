# macOS GIF timing

Opening an existing GIF retains each frame's delay. Preview, elapsed/selected
duration, trimmed export and click/branding overlays use the same ordered timing
data instead of the first frame's delay or an average. Start and end frames are
inclusive, including a single-frame selection.

Speed scales each delay independently, with the existing 10 ms minimum applied
before summing durations. The preview waits for the displayed frame's delay,
then advances; stopping, seeking or changing speed invalidates queued old ticks.
Closing the trimmer stops playback. Actual presentation can still be delayed by
the main run loop; this is not a real-time playback guarantee.

Import prefers ImageIO's unclamped delay, then its clamped delay. Missing metadata
retains the existing entrypoint defaults: 100 ms for Recent Captures/overlay
imports, 80 ms for Clips Manager. Nonfinite metadata now uses that same default;
zero, negative and sub-10 ms delays use 10 ms. Finite valid delays are not averaged.
Captured GIFs still use the configured constant frame rate. GIF encoding has
centisecond precision, so arbitrary speed settings can be rounded by ImageIO.
Windows' existing 20 ms minimum is unchanged.

Frame count and delay count must match. Undecodable frames and failed image
transforms are errors, not omitted frames. Export processes one downscaled frame
at a time and finalizes a sibling temporary file before replacing the destination.
Failures are surfaced through the existing error UI; a failed encode must not
overwrite the original. Timing storage adds only numeric arrays, not copies of
decoded images. Infinite looping and existing native controls are unchanged.

## Regression coverage and qualification

`mac/TinyClipsTests/GifTimingTests.swift` exercises the production timing value:
unequal delays, inclusive/single-frame ranges, all existing speed choices,
per-frame minimums, source offsets, constant capture timing, metadata defaults
and invalid frame alignment.

`GifTrimmerTimingTests.swift` exercises actual ImageIO import/export, selected
frames, downscaling, branding, errors and frame identity. A native-display test
checks click overlays after a long frame (explicitly skipped without a display).
Its injected scheduler
also checks the production trimmer's delay choices, loop boundaries, speed/seek
changes and suppression of stale callbacks. These deterministic callback tests
do not establish actual display cadence.

Both test files are registered in the existing Xcode XCTest target; the timing
source is included in both the direct and Mac App Store app targets. On macOS:

```sh
xcodebuild test -project mac/TinyClips.xcodeproj -scheme TinyClips \
  -configuration Debug -only-testing:TinyClipsTests/GifTimingTests \
  -only-testing:TinyClipsTests/GifTrimmerTimingTests \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
```

The existing build workflow additionally builds the app and MAS variant.
Linux can compile/run the Foundation-only timing tests, but cannot qualify
ImageIO, SwiftUI/AppKit, native builds, clipboard, VoiceOver or rendered playback.

Before native qualification, use a synthetic numbered/color-coded GIF with
100/500/200 ms delays. Open through both library and recent-item paths; preview
full/middle/single-frame ranges at 0.5x/1x/2x, seek while playing, stop/restart and
close during a queued tick. Reopen exports and inspect individual frame delays
and dimensions. Exercise a normal capture with click and branding overlays,
including a click after an intentionally long source frame. Test export failure
without losing the prior output. Check keyboard/VoiceOver controls and duration
labels. Measure memory and open/preview responsiveness separately on identical
fixtures before claiming performance gains. Windows playback of exports is a
separate interchange check, not implied by macOS component results.
