---
name: tinyclips-capture
description: Capture screens, windows, regions, or scrolling screenshots with Tiny Clips; run OCR or annotate screenshots. Use for screenshot and screen-text tasks.
---

# Screenshots with Tiny Clips

Needs scopes `capture` (and `edit` to annotate). If a command returns `unauthorized` or `automation_disabled`, follow the tinyclips-setup skill.

- List targets: `tinyclips capture.targets kind=all`; IDs are valid for this app session only.
- Whole screen: `tinyclips capture.screenshot mode=screen`.
- Region: choose a display and provide `displayId` plus `region: {x,y,width,height}` in display-local physical pixels from its top-left, using `--params` JSON. Window: choose a `windowId` from `capture.targets`. These commands do not show pickers.
- OCR: call `capture.ocr` with `mode=screen|region|window` and the matching target parameters. It returns recognized text without changing the clipboard.
- Scrolling screenshot: call `capture.scrolling.start` with the target parameters, scroll the target using your own browser or UI tools, then call `capture.scrolling.stop`. Poll `capture.scrolling.status` if needed. Tiny Clips does not inject scrolling input. `capture.scrolling.cancel` discards the capture and requires in-app approval.
- Annotate or redact: call `editor.annotate` with the screenshot `path` and a list of typed annotations (rectangle, ellipse, arrow, line, pen, text, number, emoji, redact). `bounds` and `points` use source-image pixels from the top left. Use `redactionStyle: "solid"` for passwords, keys, and other secrets; `blur` and `pixelate` come in `light`, `medium`, and `heavy` levels and `light` can stay readable. It saves a new capture; the source is unchanged and no editor window opens.
- Match a saved look: call `editor.themes.list`, then pass `themeId` to `editor.annotate`. Values on an annotation win over the theme, and redactions and emoji are never styled.
- To involve the user in a visual selection or manual editing, open the interactive app UI separately with `tinyclips app.open window=library` or `tinyclips editor.open path="<file>"`.

Errors: `permission_required` means Screen Recording permission is missing. Ask the user to grant it in System Settings or Windows privacy settings; never try to bypass it.
