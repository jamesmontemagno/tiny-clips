# Studio: a Settings page of its own (Windows handoff)

**Status:** built on macOS on 7 October 2026. Not started on Windows. Nothing under
`windows/src`, `windows/tests`, or `windows/tools` was changed for it.

## Why

The owner wants Studio's settings on a page of their own. On the Mac they were a section at the
end of the Video page; on Windows they are a card on the General page
(`Controls/Settings/GeneralSettingsSection.xaml`). Either way they were under something else:
the switch that turns Studio on, the storage rules, and the list of drafts with its buttons.

## What the Mac has now

`mac/TinyClips/Views/SettingsView.swift` has a **Studio** page in the sidebar, right after
Video. `mac/TinyClips/Views/Settings/StudioSettingsSection.swift` is the page:

| Section | Holds |
|---|---|
| **Tiny Clips Studio** | The **Tiny Clips Studio (Preview)** switch and what Studio is. Always there. Everything below shows only while it is on |
| **Recording** | A line saying that a Studio recording is started with **Studio Recording…** in the menu bar menu (see `studio-recording-command.md`). No control: for a few hours on 7 October there was a switch here, the After recording choice over again, and it went when that choice did |
| **Projects** | **Open Project…**, which opens a project saved as a folder (see `studio-project-folders.md`) |
| **Storage** | Keep exported projects for, Storage limit, Storage used, Clean Up Now, and the note about what is kept |
| **Drafts** | The drafts list, as it was |

The Video page has nothing of Studio left on it. Text that sent people to "Video settings" for
Studio now says "Studio settings".

## What to do on Windows

1. A `StudioSettingsSection` page beside the others in `Controls/Settings`, in the Settings
   navigation after Video, with what the General page's Studio card holds today, moved and not
   copied. Keep every `AutomationId`.
2. What the page says under Recording depends on `studio-recording-command.md`: do that first,
   or with it. Until then After recording stays on the Video page as it is.
3. The view model's Studio members are tested in `SettingsViewModelStudioTests`; they should not
   need to change, only where they are shown.
4. Rows A11Y-27 and A11Y-28 of `windows/docs/accessibility-release-gate.md` name Settings ›
   General and the tab order after the switch. The hands-on checklist in
   `plans/video-studio-plan.md` does too (its Windows steps 1 and 2).
5. `windows/CHANGELOG.md`, the README, and any notice that says where to switch Studio on.

## How the Mac was checked

- The page was drawn off screen with Studio switched on, and the picture read.
- 354 tests pass and both builds succeed.
- Not done by anyone yet: opening Settings and using the page.
