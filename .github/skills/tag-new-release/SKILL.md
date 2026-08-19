---
name: tag-new-release
description: Create a new annotated git tag with release notes extracted from changelog files. Supports both macOS and Windows TinyClips release flows.
argument-hint: "--platform <mac|windows> [--version <tag>] (e.g., --platform mac --version v1.5.0-mac)"
user-invocable: true
---

# Tag New Release

Create a new annotated git tag for a release based on the project's platform changelog.

## When to use

- When preparing a new TinyClips release tag for macOS or Windows.
- When the user asks to tag a release.
- When the user provides a version to tag, such as `v1.5.0-mac` or `v1.0.9-windows`.
- When the user wants release notes extracted from the relevant changelog and included in the tag message.

## How to use

Request the release tag in chat (platform is required):

```text
/tag-new-release --platform mac --version v1.5.0-mac
```

Or run the script directly from the repository root:

```bash
bash .github/skills/tag-new-release/tag-new-release.sh --platform mac --version v1.5.0-mac
```

On Windows, use PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .github/skills/tag-new-release/tag-new-release.ps1 -Platform windows -Version v1.0.9-windows
```

If `--version` is provided, that exact tag value is used for the release. If `--version` is omitted:
- macOS defaults to `v<Info.plist CFBundleShortVersionString>.0-mac` (for example, `v1.5.0-mac`)
- Windows defaults to incrementing patch from the latest `v*-windows` tag.

## Step-by-step procedure

When asked to tag a new release:

1. **Check existing tags** - Inspect recent git tags for the selected platform.
2. **Determine release version**:
   - macOS: If the user provides a tag version, use it as the source of truth; otherwise validate it against `mac/TinyClips/Info.plist` `CFBundleShortVersionString`
   - Windows: Use provided tag or infer by incrementing the latest `-windows` patch tag
3. **Read platform changelog**:
   - macOS: root `CHANGELOG.md`
   - Windows: `windows/CHANGELOG.md`
4. **Update changelog** - Insert the release heading with today's date below Unreleased.
5. **Verify the working directory** - Ensure there are no uncommitted changes before creating commit/tag.
6. **Create commit** - Commit the changelog release marker.
7. **Create an annotated git tag** with:
   - A tag name matching the platform version (for example `v1.5.0-mac`, `v1.0.9-windows`)
   - A tag message containing the version and formatted release notes from the platform changelog
8. **Confirm the tag** - Show the created tag details to verify the tag name and message.
9. **Suggest pushing the tag** - Tell the user they can push with:

   ```bash
   git push origin <tag-name>
   ```

   Do not push the tag unless the user explicitly asks.

## Release notes format

The tag message should include cleanly formatted release notes from the selected changelog entry for the release. Include all relevant sections, such as:

- Added
- Improved
- Fixed
- Changed
- Deprecated
- Removed
- Security

## Safety checks

- Do not create a release tag from a dirty working directory.
- Do not overwrite an existing tag.
- Do not push tags without explicit user approval.
- If the requested version conflicts with existing tags, stop and ask the user how to proceed.
- When the version is provided explicitly by the user, honor that value even if it differs from the app's current `Info.plist` version.

## Files included

- `tag-new-release.sh` - Script that automates changelog release marking, commit creation, and annotated tagging for macOS or Windows.
- `tag-new-release.ps1` - PowerShell equivalent for running the same release-tag workflow natively on Windows.
