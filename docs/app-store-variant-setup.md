# App Store Variant Setup

This repo now includes code paths for an `APPSTORE` build variant and Mac App Store-specific files:

- `mac/TinyClips/Info-MAS.plist`
- `mac/TinyClips/TinyClipsMAS.entitlements`

## What is already implemented in code

When `APPSTORE` is defined at compile time:

- Update UI is hidden in menu + settings.
- Save behavior is sandbox-safe:
  - Default save folders:
    - Screenshots + GIFs: `~/Pictures/TinyClips`
    - Videos: `~/Movies/TinyClips`
  - Optional custom save folder is selected via `NSOpenPanel` and persisted using a security-scoped bookmark.

Direct/non-App-Store behavior is unchanged.

## Xcode wiring steps

1. Duplicate the `TinyClips` target and name it `TinyClipsMAS`.
2. Set a distinct bundle identifier for `TinyClipsMAS`.
  - Current MAS bundle identifier in this repo: `com.refractored.tinyclips`
3. In `TinyClipsMAS` build settings:
   - `INFOPLIST_FILE = TinyClips/Info-MAS.plist`
   - `CODE_SIGN_ENTITLEMENTS = TinyClips/TinyClipsMAS.entitlements`
   - Add `APPSTORE` to **Swift Active Compilation Conditions**.
   - Enable App Sandbox (`ENABLE_APP_SANDBOX = YES`).
4. Remove Sparkle from the `TinyClipsMAS` target:
   - No Sparkle product in the MAS target's Frameworks/Link phase.
   - Keep Sparkle only in the direct distribution target.
5. Set signing/provisioning for Mac App Store distribution on `TinyClipsMAS`.

## Validation checklist

- `TinyClips` (direct):
  - Still shows “Check for Updates…”.
  - Save directory field remains editable path behavior.
- `TinyClipsMAS`:
  - No update UI.
  - Without custom folder, screenshots/GIFs save to Pictures and videos save to Movies.
  - Custom folder selection persists across relaunch and new captures save there.

## Xcode Cloud

Use Xcode Cloud for the Mac App Store (`TinyClipsMAS`) CI/CD path.

### Notes

- Ensure the cloud workflow builds the `TinyClipsMAS` scheme for App Store validation/distribution.
- If automatic Swift package resolution is disabled, commit `mac/TinyClips.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved` to the branch being built.
- In App Store Connect, make sure the app record and signing assets align with the MAS bundle identifier `com.refractored.tinyclips`.
