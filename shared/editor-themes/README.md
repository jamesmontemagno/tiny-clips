# Editor Theme Format

macOS, Windows, and agents exchange screenshot editor themes as this JSON. See [`plans/editor-themes.md`](../../plans/editor-themes.md) for product behavior.

## Rules

- `version` is `1`. A newer version is rejected, so an old app never loses fields it can't read.
- Colors are `#RRGGBB` or `#AARRGGBB` in sRGB. Both apps store and compare uppercase `#AARRGGBB`.
- A missing field takes its value from [`default-theme.json`](fixtures/default-theme.json). Unknown fields are ignored.
- A value outside its range is rejected, never clamped.
- `id` is 1 to 64 letters, digits, or hyphens. `name` is 1 to 60 characters after trimming.
- `shape.fillColor` with alpha 0 means no fill, and `shape.gradientEndColor` needs a visible fill. `shape.shadowBlur` of 0 means no shadow.
- `text.fontFamily` of `null` is the platform's default font. A family the system doesn't have falls back the same way.

## Units

Sizes use macOS editor units, so a theme looks the same on any image:

| Field | One unit, in image pixels |
| --- | --- |
| `shape.strokeWidth`, `shape.shadowBlur`, `line.width` | `max(1, imageWidth / 900)` |
| `text.fontSize`, `text.box.borderWidth`, `text.box.padding`, `text.box.cornerRadius` | `imageWidth / 800` |
| `number.size` | A multiplier of each platform's default badge size |
| `canvas.padding`, `canvas.cornerRadius`, `canvas.shadow` | 1 |

## Ranges

| Field | Range |
| --- | --- |
| `shape.strokeWidth`, `line.width`, `shape.shadowBlur` | 0 to 100 |
| `shape.gradientDirection`, `canvas.gradientDirection` | 0 to 360 |
| `text.fontSize` | 4 to 200 |
| `text.box.borderWidth` | 0 to 12 |
| `text.box.padding` | 0 to 48 |
| `text.box.cornerRadius` | 0 to 32 |
| `number.size` | 0.25 to 4 |
| `canvas.padding` | 0 to 500 |
| `canvas.cornerRadius`, `canvas.shadow` | 0 to 200 |

## Named Values

| Field | Values |
| --- | --- |
| `line.arrowStyle` | `straight`, `curvedLeft`, `curvedRight` |
| `canvas.background` | `transparent`, `solid`, `gradient` |
| `canvas.frame` | `original`, `square`, `landscape4x3`, `landscape16x9`, `portrait3x4`, `portrait9x16` |
| `canvas.horizontalAlignment` | `left`, `center`, `right` |
| `canvas.verticalAlignment` | `top`, `center`, `bottom` |

## Storage

Each app keeps `editor-themes.json` in its data folder, written atomically:

```json
{ "version": 1, "activeThemeId": null, "themes": [] }
```

A file that can't be read is copied to `editor-themes.json.bak` before the app starts over, and a single invalid theme is skipped the same way. Each app holds at most 200 themes.

## Fixtures

Both apps test against these files:

- [`default-theme.json`](fixtures/default-theme.json): what `{ "id": "default", "name": "Default" }` expands to.
- [`brand-theme.json`](fixtures/brand-theme.json): every field set to a non-default value.
- [`invalid-themes.json`](fixtures/invalid-themes.json): themes both apps must reject, and the field each error names.
