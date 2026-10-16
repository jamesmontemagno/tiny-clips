# Tiny Clips Studio launch video

An independent snapshot of the Tiny Clips 2.0 Studio launch cut. It lives in
`marketing/studio-launch/` and includes its own rendering scripts and footage.
The reusable launch-video tooling and Hype Video skill now live in the standalone
[tiny-clips-marketing repository](https://github.com/jamesmontemagno/tiny-clips-marketing).

A 45-second, 1920×1080, 60 fps promo for Tiny Clips. The Studio 2.0 cut combines an animated
HTML/CSS composition with user-supplied Studio footage, rendered frame by frame in a headless
browser. The polished lo-fi soundtrack is synthesized locally in Node.
The selected creative profile uses subtle motion and an ice (cyan/blue/violet) palette.

## Hype a new release

Edit `src/content.js` — it holds the “What’s new” tiles and the line that introduces them — then:

```bash
npm run review     # contact sheets in out/review/, and a check that every tile's text fits
npm run render     # out/tiny-clips-studio-launch.mp4
```

The `hype-video` skill in the standalone marketing repository walks through the whole flow,
including a short creative brief, choosing features from the changelogs, and drafting the post.

## Render it

Requires Node 20+, `ffmpeg` on your `PATH`, and Microsoft Edge or Google Chrome installed (set
`BROWSER_PATH` to point at a different Chromium-based browser).

```bash
cd marketing/studio-launch
npm install
npm run render     # out/tiny-clips-studio-launch.mp4  (60 fps, about three minutes)
npm run draft      # out/tiny-clips-studio-launch-draft.mp4  (30 fps, under a minute)
npm run review     # one frame per scene as contact sheets -> out/review/
npm run preview    # live preview with a scrubber and the selected soundtrack
```

`out/` and `node_modules/` are ignored by this project. Useful extras:

```bash
node scripts/render.mjs --still 5,12.5,43     # PNG stills at those seconds -> out/stills/
node scripts/render.mjs --from 15 --to 21     # render part of the timeline
node scripts/render.mjs --no-audio            # picture only, for adding your own music
```

## Storyboard

Everything is cut to a 128 BPM grid: one bar is 1.875 s and the piece is 24 bars long.

| Bars | Time | Scene |
| --- | --- | --- |
| 0–1 | 0:00 | Tiny Clips 2.0: Hello, Studio |
| 1–4 | 0:01.875 | Animated separate screen/camera layers, bubble to side-by-side layouts, backgrounds and padding |
| 4–7 | 0:07.5 | Actual Windows Studio controls/timeline with a privacy-safe, illustrative animated canvas |
| 7–9 | 0:13.125 | Zoom, cut, speed, MP4 export, editable projects, and enabling Studio Preview |
| 9–12 | 0:16.875 | A rendered Studio video recap and Preview call to action |
| 12–15 | 0:22.5 | Screenshot, video, GIF; microphone and system audio; menu bar and tray |
| 15–17 | 0:28.125 | Screenshot editor: arrows, steps, redaction, stickers, text, backgrounds |
| 17–21 | 0:31.875 | Six recent cross-platform features from `src/content.js` |
| 21–24 | 0:39.4 | Tiny Clips · Available on macOS and Windows · tinyclips.app |

Studio owns exactly the first 22.5 seconds, including the release title and rendered-video
recap. Its early-preview status and opt-in setting are explicit; ordinary video recording is
not presented as Studio. Scene transitions and background movement have been softened for
this cut, and a melodic lo-fi motif gives the soundtrack a clearer music lead. The second half
highlights shipped cross-platform additions, rather than implying every tile first shipped in
2.0. The established download outro is unchanged.
The result preview puts its export controls along the top of the video and the Scene 01 layout
label below them.

### Recorded Studio media

`assets/studio-demo.mp4` is a silent 7.5-second excerpt (8.0–15.5 seconds) of the supplied
2026-10-15 Studio recording. The original preview area is **removed from the encoded asset**,
not just hidden in CSS, so nested conversation and project text cannot leak into the video.
The real inspector, controls, cursor, and timeline remain. The composition replaces the
removed preview with a clean animated illustration, labeled as such.

`scripts/studio-media.mjs` extracts 225 JPEG frames into ignored `out/studio-frames/` before
review, rendering, or preview. Rendering waits for each selected frame to decode, so seeking
backward, parallel workers, and still exports all produce the correct frame. Source footage
is 30 fps, held for two frames in the 60 fps final; the surrounding animation is native 60 fps.
Missing or short media fails explicitly.

To replace the footage, use an approved 7.5-second, 30 fps clip, remove private content before
putting it in `assets/`, and adjust the illustrative canvas bounds in `src/styles.css` if the
editor's geometry changes. Do not commit the unredacted original.

## Editing it

- `src/content.js` holds the release-specific copy: the “What’s new” tiles (3 to 6), their
  visuals,   and an optional breakdown line. The Studio-first storyboard also lives in `src/index.html`.
- `src/creative.js` holds the selected campaign angle, hype level, motion intensity, music style,
  and visual palette. The `hype-video` skill asks about any choices not specified in the request.
  Supported values are:

  | Setting | Values |
  | --- | --- |
  | `campaign` | `all-in-one`, `new-release`, `creator`, `developer` |
  | `hypeLevel` | `polished`, `upbeat`, `full-send` |
  | `motionIntensity` | `subtle`, `punchy`, `high-impact` |
  | `musicStyle` | `electronic`, `cinematic`, `funk`, `lofi`, `silent` |
  | `visualTheme` | `neon`, `sunset`, `ice` |

  Defaults preserve the original video. All music is synthesized locally; `silent` removes any
  previously generated soundtrack and renders without muxing audio. `npm run preview` regenerates
  the selected soundtrack first, so it cannot accidentally use one from an earlier profile. The
  timeline stays at 128 BPM, 24 bars, and 45 seconds for every profile.
- `src/index.html` holds the scenes. A scene is a `<section class="scene" data-bar="8" data-bars="1">`;
  elements inside are timed with `data-at` in beats from the start of their scene (`data-at="1.5"`)
  or in seconds (`data-at="0.3s"`).
- `src/styles.css` holds the look and every animation. Entrances are the `a-*` classes (`a-slam`,
  `a-pop`, `a-up`, …); tile visuals are the `.v-*` rules.
- `src/player.js` builds the tiles from `content.js`, puts every CSS animation on one timeline,
  and exposes `window.__seek(seconds)`. Nothing plays on its own, so a frame depends only on its
  timestamp.
- `scripts/audio.mjs` is the soundtrack; the arrangement section maps instruments to bars. If you
  change the tempo or length, change `BPM` and `BARS` in both `audio.mjs` and `player.js`.

The real app screenshots come from `docs/` (`docs/windows-art/editor.png`, `tray.png`,
`video-editor.png`, and `docs/tinyclips.png`); replace those files to refresh them here too.
