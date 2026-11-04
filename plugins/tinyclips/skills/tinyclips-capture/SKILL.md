---
name: tinyclips-capture
description: Capture screens, windows, regions, or scrolling screenshots with Tiny Clips; run OCR or annotate screenshots. Use for screenshot and screen-text tasks.
---

# Screenshots with Tiny Clips

Needs scopes `capture` (and `edit` to annotate). If a command returns `unauthorized` or `automation_disabled`, follow the tinyclips-setup skill.

- List targets: `tinyclips capture.targets kind=all`; IDs are valid for this app session only. On GNOME, the person must first choose **Main menu → Choose shared source…** in Tiny Clips; a local screenshot or recording is not an agent grant. Do not attempt to open, automate or bypass a consent picker from an unattended command. Check the running app's supported commands before requesting OCR or scrolling capture.
- Whole screen: `tinyclips capture.screenshot mode=screen`.
- Region: choose a display and provide `displayId` plus `region: {x,y,width,height}` in display-local physical pixels from its top-left, using `--params` JSON. Window: choose a `windowId` from `capture.targets`. These commands do not show pickers.
- OCR: call `capture.ocr` with `mode=screen|region|window` and the matching target parameters. It returns recognized text without changing the clipboard.
- Scrolling screenshot: call `capture.scrolling.start` with the target parameters, scroll the target using your own browser or UI tools, then call `capture.scrolling.stop`. Poll `capture.scrolling.status` if needed. Tiny Clips does not inject scrolling input. `capture.scrolling.cancel` discards the capture and requires in-app approval.
- Annotate or redact: call `editor.annotate` with the screenshot `path` and a list of typed annotations (rectangle, ellipse, arrow, line, pen, text, number, emoji, redact). `bounds` and `points` use source-image pixels from the top left. It saves a new capture; the source is unchanged and no editor window opens.
- Crop a saved library screenshot: call `editor.crop` with its `path` and an in-bounds `region` in source-image pixels. Read the returned PNG path; the original remains intact.
- To involve the user in a visual selection or manual editing, open the interactive app UI separately with `tinyclips app.open window=library` or `tinyclips editor.open path="<file>"`.

Errors: `permission_required` means the required permission or live source is missing. Ask the user to authorize it in the relevant OS settings or, on GNOME, share a source from the capture bar. Never try to bypass it.
