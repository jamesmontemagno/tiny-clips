---
name: hype-video
description: Produce the Tiny Clips hype video for a new release — pick the newest features from the changelogs, update the "What's new" tiles, render the 45-second 1080p MP4, and draft a social post to go with it. Use when asked to hype, promote, announce, or make a video or tweet about new Tiny Clips features or a release.
argument-hint: "[what to hype] (e.g., 'the 1.9 release', 'scrolling capture and OCR', 'latest Windows features')"
user-invocable: true
---

# Hype Video

Re-cut the Tiny Clips hype video around a new set of features and draft the post that ships with it.

The video is code, not footage. It lives in `marketing/hype-video/`: an HTML/CSS composition
rendered frame by frame in headless Edge or Chrome, with a soundtrack synthesized in Node. The
intro, the core-feature scenes, and the outro stay the same from release to release. The part that
changes is the "What's new" section (0:30–0:39), and all of its text and visuals come from one
file: `marketing/hype-video/src/content.js`.

## When to use

- A release has shipped, or is about to, and the user wants a video for it.
- The user asks to hype, promote, or announce specific features.
- The user wants a tweet or post about what is new in Tiny Clips.

## Procedure

### 1. Choose what to hype

Read the newest released sections of both changelogs:

- `CHANGELOG.md` (macOS)
- `windows/CHANGELOG.md` (Windows)

Pick 3 to 6 features. Six fills the grid best.

- Take them from the **Added** and **Changed** sections. Bug fixes are not hype.
- Prefer features that shipped on both platforms. When every tile is cross-platform, set
  `platforms` to `macOS + Windows`. If one platform only, say so in `platforms`, or name the
  platform in that tile's blurb.
- Leave out anything under **Unreleased** unless the user asks to tease it.
- Lead with the most visual feature; the first tile is on screen longest.

If the user named the features, use those and skip the selection. If it is unclear which platform
or release they mean, ask.

### 2. Edit `src/content.js`

Replace the tiles. The file's header comment lists every field; the short version:

| Field | Rule |
| --- | --- |
| `title` | Must fit one line, about 22 characters. Name the feature the way the app does. |
| `blurb` | One sentence, two lines at most, about 44 characters. Say what the user gets. |
| `accent` | `blue`, `violet`, `pink`, `orange`, `green`, `cyan`, or `red`. Vary them across tiles. |
| `visual` | One of the types below. |

| Visual | Use it for | Example |
| --- | --- | --- |
| `compare` | Something got smaller, faster, or shorter | `{ type: 'compare', from: 'PNG', to: 'WebP', ratio: 0.42 }` |
| `toggle` | A new option or mode next to the old one | `{ type: 'toggle', from: 'H.264', to: 'H.265', caption: 'HEVC' }` |
| `emoji` | Stickers, reactions, anything playful | `{ type: 'emoji', emoji: ['😎', '🎉', '👀'] }` |
| `slider` | An adjustable amount | `{ type: 'slider', from: 100, to: 50, unit: '%', size: [1920, 1080] }` |
| `keys` | A new keyboard shortcut (up to 5 keys) | `{ type: 'keys', keys: ['⌃', '⌥', '⌘', '1'] }` |
| `stat` | One number or word that sells it | `{ type: 'stat', value: '60 fps', caption: 'GPU pipeline' }` |
| `list` | Menus, recents, history | `{ type: 'list' }` |
| `panel` | Library, inspector, details views | `{ type: 'panel', tags: ['demo', 'bug', 'ui'] }` |

Also update `whatsNew.heading` and `whatsNew.pill` if the video is for a named release (for
example pill `New in 1.9`), and `breakdown.title`, the big line that introduces the section.

Copy rules:

- Only claim what the changelog says. Use a number (`ratio`, a `stat` value, a percentage) only
  when the changelog or a measurement backs it; `compare` bars are read as a real size difference.
- Shortcuts must match the current defaults in `README.md` and `windows/README.md`.
- Plain words, no exclamation marks; the motion carries the energy.

### 3. Check it

```bash
cd marketing/hype-video
npm install        # first time only
npm run review
```

`review` writes contact sheets to `out/review/sheet-*.png`: one frame per scene and one as each
tile lands. Open them and look at every tile. The command fails with a list of problems when a
title wraps, a blurb runs past two lines, a visual is clipped by its tile, the heading collides
with the platform label, or a visual type or accent is unknown. Fix `content.js` and run it again
until it passes and the sheets look right.

`npm run preview` opens the composition in a browser with a scrubber and the soundtrack, for
checking motion and timing.

### 4. Render

```bash
npm run render     # out/tiny-clips-hype.mp4, about three minutes
```

Confirm the result: 45.0 s, 1920×1080, 60 fps, H.264 video with AAC audio.

```bash
ffprobe -v error -show_entries stream=codec_name,width,height,r_frame_rate:format=duration -of default=nw=1 out/tiny-clips-hype.mp4
```

Tell the user the full path of the MP4. `out/` is not tracked by git, so the file stays local
until they upload it.

### 5. Draft the post

Write the post in the reply; do not publish it anywhere.

- At most 280 characters, counting a link as 23. Leave the video to attach itself.
- First line says what Tiny Clips is or what just shipped. Then two to four of the hyped
  features, each a few words, in the same order as the video. End with `tinyclips.app`.
- Mention that it runs on macOS and Windows, and that it is free and open source if there is room.
- One or two emoji at most, and no more than two hashtags.
- Offer one alternative with a different angle (for example, feature list versus one-line pitch).

### 6. Commit

Commit the source changes only, normally just `marketing/hype-video/src/content.js`. Never commit
`marketing/hype-video/out/` or `node_modules/`; both are ignored. Do not push or open a pull
request unless asked.

## Changing more than the tiles

- **Core feature scenes, intro, outro**: `marketing/hype-video/src/index.html`. Each scene is a
  `<section class="scene" data-bar="…" data-bars="…">` on a 128 BPM grid (one bar is 1.875 s).
  Keep scene boundaries on bars so the cuts stay on the beat.
- **A new tile visual**: add a builder to `VISUALS` in `src/player.js`, its styles next to the
  other `.v-*` rules in `src/styles.css`, and a line in the `content.js` header and in the table
  above.
- **App screenshots**: the video reads `docs/windows-art/editor.png`, `docs/windows-art/tray.png`,
  `docs/windows-art/video-editor.png`, and `docs/tinyclips.png`. Replace those files to refresh
  them. Do not use screenshots that show personal data.
- **Music**: `scripts/audio.mjs`. If the tempo or length changes, change `BPM` and `BARS` in both
  `audio.mjs` and `player.js`.
- **Other cuts**: `npm run draft` for a fast 30 fps pass, `node scripts/render.mjs --no-audio` for
  picture only, `--from` and `--to` (seconds) for a section.

`marketing/hype-video/README.md` has the storyboard and the full command list.

## Requirements

Node 20 or later, `ffmpeg` on the `PATH`, and Microsoft Edge or Google Chrome. Set `BROWSER_PATH`
to use a different Chromium-based browser.
