# Tiny Clips hype video

A 45-second, 1920×1080, 60 fps promo for Tiny Clips, built entirely from code: the picture is an
HTML/CSS composition rendered frame by frame in a headless browser, and the soundtrack is
synthesized in Node, so there is no stock footage or licensed music to track.

## Hype a new release

Edit `src/content.js` — it holds the “What’s new” tiles and the line that introduces them — then:

```bash
npm run review     # contact sheets in out/review/, and a check that every tile's text fits
npm run render     # out/tiny-clips-hype.mp4
```

The `hype-video` skill (`.github/skills/hype-video/SKILL.md`) walks through the whole flow,
including a short creative brief, choosing features from the changelogs, and drafting the post.

## Render it

Requires Node 20+, `ffmpeg` on your `PATH`, and Microsoft Edge or Google Chrome installed (set
`BROWSER_PATH` to point at a different Chromium-based browser).

```bash
cd marketing/hype-video
npm install
npm run render     # out/tiny-clips-hype.mp4  (60 fps, about three minutes)
npm run draft      # out/tiny-clips-hype-draft.mp4  (30 fps, under a minute)
npm run review     # one frame per scene as contact sheets -> out/review/
npm run preview    # live preview with a scrubber and the selected soundtrack
```

`out/` is ignored by git. Useful extras:

```bash
node scripts/render.mjs --still 5,12.5,43     # PNG stills at those seconds -> out/stills/
node scripts/render.mjs --from 15 --to 21     # render part of the timeline
node scripts/render.mjs --no-audio            # picture only, for adding your own music
```

## Storyboard

Everything is cut to a 128 BPM grid: one bar is 1.875 s and the piece is 24 bars long.

| Bars | Time | Scene |
| --- | --- | --- |
| 0–2 | 0:00 | A region selection turns into the app icon, then the wordmark |
| 2–5 | 0:03.75 | Screenshot, Video, GIF — one bar each |
| 5–6 | 0:09.4 | Capture picker: Region, Screen, Window |
| 6–8 | 0:11.25 | Menu bar and system tray, global hotkeys |
| 8–11 | 0:15 | Screenshot editor, then one tool per beat |
| 11–13 | 0:20.6 | Video and GIF trimmers |
| 13–16 | 0:24.4 | Six features, two beats each: webcam overlay, mic + system audio, teleprompter, scrolling capture, OCR, clips library |
| 16–17 | 0:30 | Breakdown line from `content.js` |
| 17–21 | 0:31.9 | What’s new tiles from `content.js` |
| 21–24 | 0:39.4 | Tiny Clips · Available on macOS and Windows · tinyclips.app |

## Editing it

- `src/content.js` holds the release-specific copy: the “What’s new” tiles (3 to 6), their
  visuals, and the breakdown line. This is the only file a new release normally touches.
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
