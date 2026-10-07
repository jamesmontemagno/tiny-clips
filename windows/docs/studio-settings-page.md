# Studio: a Settings page of its own (Windows handoff)

**Status:** built on macOS on 7 October 2026, and on Windows the same day from this brief
(#430), without the Projects section: **Open project** needs saved project folders, which are
#429. On Windows it is compiled and unit tested and has never been run: nobody has opened
Settings and seen the page. What to try by hand is step 21 of the Windows hands-on checklist in
[`plans/video-studio-plan.md`](../../plans/video-studio-plan.md). "What Windows has now" below
says where each part is.

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
   or with it. (It was done first, as #431: After recording is gone from the Video page.)
3. The view model's Studio members are tested in `SettingsViewModelStudioTests`; they should not
   need to change, only where they are shown.
4. Rows A11Y-27 and A11Y-28 of `windows/docs/accessibility-release-gate.md` name Settings ›
   General and the tab order after the switch. The hands-on checklist in
   `plans/video-studio-plan.md` does too (its Windows steps 1 and 2).
5. `windows/CHANGELOG.md`, the README, and any notice that says where to switch Studio on.

## What Windows has now

| What | Where |
|---|---|
| A **Studio** item in the Settings navigation, right after Video, and its section kind | `Views/SettingsWindow.xaml`, `Settings/SettingsSectionKind.cs`, `Views/SettingsWindow.xaml.cs`: `GetOrCreateSection` |
| The page: **Tiny Clips Studio** (the switch, what Studio is, and the line that says what is kept while it is off), and, only while Studio is on, **Recording** (one card, no control), **Storage**, and **Drafts**. The markup and the handlers were moved from the General page, whose markup is the file `main` has again. Every `AutomationId` is kept; `StudioRecordingHowToCard` and `StudioNavigationItem` are new. The drafts heading says "Drafts" where it said "Studio drafts" | `Controls/Settings/StudioSettingsSection.xaml`, `.xaml.cs` |
| The view model's Studio members are the same. What changed is the page they belong to: the switch and the two storage rules are restored, and kept from being saved while the page is first shown, with the Studio page and no longer with General | `ViewModels/SettingsViewModel.Studio.cs`: `RestoreStudioSettings`, `OnIsStudioPreviewEnabledChanged`, `PersistStudio` |
| The projects are read when the Studio page is first chosen. They were read whenever Settings opened, because General is the page it opens on | `StudioSettingsSection` constructor: `EnsureStudioStorageInitializedAsync` |
| The Clips Library's notice for Open in Studio with Studio off says Settings › Studio | `ViewModels/ClipsLibrary/ClipsLibraryViewModel.cs` |

**Projects is not there.** It goes between Recording and Storage, where the page's markup has
a comment for it: a heading like the others and one card with **Open project…**. The test
that holds the page's headings to the Mac's order lets a Projects heading in without changing.

The headings of this page are marked as headings for a screen reader (`HeadingLevel`). The
group titles of the other Settings pages are styled the same and not marked.

## How the Mac was checked

- The page was drawn off screen with Studio switched on, and the picture read.
- 354 tests pass and both builds succeed.
- Not done by anyone yet: opening Settings and using the page.
