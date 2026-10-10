// What the video says about the latest releases. To hype new features, edit this file and
// re-render; the intro, core-feature scenes, and outro stay the same.
//
// tiles: 3 to 6 entries, shown in order, one landing every few beats.
//   title   one line, about 22 characters or fewer
//   blurb   one sentence, two lines at most (about 44 characters)
//   accent  blue | violet | pink | orange | green | cyan | red
//   visual  one of:
//     { type: 'compare', from: 'PNG', to: 'WebP', ratio: 0.42 }    two bars; the second shrinks to ratio (0–1) of the first
//     { type: 'toggle', from: 'H.264', to: 'H.265', caption: 'HEVC' }   a switch that flips from -> to
//     { type: 'emoji', emoji: ['😎', '🎉', '👀'] }                  three emoji; the middle one is selected and rotates
//     { type: 'slider', from: 100, to: 50, unit: '%', size: [1920, 1080] }   a slider; size (optional) shows scaled dimensions
//     { type: 'keys', keys: ['⌃', '⌥', '⌘', '1'] }                  key caps pressed in order (up to 5)
//     { type: 'stat', value: '60 fps', caption: 'GPU pipeline' }    one big number or word
//     { type: 'list' }                                              a menu of rows with thumbnails
//     { type: 'panel', tags: ['demo', 'bug', 'ui'] }                a grid with a details pane
//
// The renderer refuses to run if a tile's text does not fit, so check with `npm run review`.
window.HYPE_CONTENT = {
  breakdown: {
    lead: 'And it keeps getting better.',
    title: 'What’s new',
  },
  whatsNew: {
    pill: 'What’s new',
    heading: 'Fresh from the latest releases',
    platforms: 'macOS + Windows',
    tiles: [
      {
        title: 'WebP screenshots',
        blurb: 'Smaller files with adjustable quality.',
        accent: 'cyan',
        visual: { type: 'compare', from: 'PNG', to: 'WebP', ratio: 0.42 },
      },
      {
        title: 'H.265 video',
        blurb: 'Pick the codec that fits your recording.',
        accent: 'red',
        visual: { type: 'toggle', from: 'H.264', to: 'H.265', caption: 'HEVC' },
      },
      {
        title: 'Emoji stickers',
        blurb: 'Drop, resize, and rotate anything.',
        accent: 'orange',
        visual: { type: 'emoji', emoji: ['😎', '🎉', '👀'] },
      },
      {
        title: 'Export at any scale',
        blurb: 'Click the resolution, pick 10–100%.',
        accent: 'green',
        visual: { type: 'slider', from: 100, to: 50, unit: '%', size: [1920, 1080] },
      },
      {
        title: 'Thumbnail previews',
        blurb: 'Recent captures show up right in the menu.',
        accent: 'pink',
        visual: { type: 'list' },
      },
      {
        title: 'Clip details pane',
        blurb: 'Preview, tag, and share in one place.',
        accent: 'blue',
        visual: { type: 'panel', tags: ['demo', 'bug', 'ui'] },
      },
    ],
  },
};
