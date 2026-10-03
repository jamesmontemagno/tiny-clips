---
name: bump-swift-version
description: Bump the version for the TinyClips Swift app across Info.plist, Info-MAS.plist, and project.pbxproj. Use this skill when you need to update the app version (e.g., from 1.8.0 to 1.9.0).
argument-hint: "[version] (e.g., 1.9.0)"
user-invocable: true
---

# Bump Swift App Version

This skill updates the TinyClips macOS app version across all version configuration files in a single command.

## What it does

The skill automatically updates the following files with the new version number:

- `mac/TinyClips/Info.plist` - `CFBundleShortVersionString`
- `mac/TinyClips/Info-MAS.plist` - `CFBundleShortVersionString` (Mac App Store variant)
- `mac/TinyClips.xcodeproj/project.pbxproj` - All 4 `MARKETING_VERSION` entries (Debug/Release for Direct and MAS builds)

## When to use

- When preparing a new release of the TinyClips macOS app
- When you need to update the version before building or submitting to the Mac App Store
- To keep all version references in sync across the project

## How to use

1. Request the version bump in chat:
   ```
   /bump-swift-version 1.9.0
   ```

2. Provide the new version in format `X.Y.Z` (e.g., `1.9.0`, `2.0.0`). `X.Y` (e.g., `2.0`) is also accepted.

## Files included

- `bump-swift-version.sh` - Shell script that performs the version updates

## Step-by-step procedure

When asked to bump the Swift version:

1. **Run the script** from the repository root. It validates the version format, updates all three files, and fails if any of them did not end up on the new version:
   ```bash
   .github/skills/bump-swift-version/bump-swift-version.sh 1.9.0
   ```
2. **Verify changes** - `git diff --stat` should show exactly the three files above with 6 changed lines (one per plist, four in `project.pbxproj`)
3. **Open a pull request, if one was requested** - Validate both mac schemes as described in `.github/copilot-instructions.md`, commit the three files as `Bump macOS version to X.Y.Z`, and create the PR

Always use the script instead of editing the plists with `PlistBuddy -c "Set ..."` or `plutil -replace`. Those rewrite the whole file and reorder its keys; the script changes only the version line.

## Example

**Request:**
```
Bump the Swift app version to 1.9.0
```

**Result:**
- Info.plist: CFBundleShortVersionString = 1.9.0
- Info-MAS.plist: CFBundleShortVersionString = 1.9.0
- project.pbxproj: All MARKETING_VERSION = 1.9.0

## Notes

- The version format must be `X.Y.Z` or `X.Y` (e.g., 1.9.0, 1.10.2, 2.0)
- This updates both the direct distribution and Mac App Store variant
- All 4 build configurations (Debug Direct, Release Direct, Debug MAS, Release MAS) are updated
- Changes are made directly to the files; no commit is created automatically
