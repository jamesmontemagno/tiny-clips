#!/bin/bash

# Bump the version for the Swift app across all version files
# Usage: ./bump-swift-version.sh <new-version>
# Example: ./bump-swift-version.sh 1.9.0

set -e

if [ "$(uname -s)" != "Darwin" ]; then
    echo "❌ This script must be run on macOS because it uses PlistBuddy and BSD sed."
    exit 1
fi

if [ -z "$1" ]; then
    echo "Usage: $0 <new-version>"
    echo "Example: $0 1.9.0"
    exit 1
fi

NEW_VERSION="$1"

# Validate version format (X.Y.Z or X.Y)
if ! [[ "$NEW_VERSION" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
    echo "❌ Invalid version format: $NEW_VERSION"
    echo "Version must be in format: X.Y.Z or X.Y (e.g., 1.9.0, 2.0)"
    exit 1
fi

# Find the repository root
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
MAC_DIR="$REPO_ROOT/mac"

# Files to update
INFO_PLIST="$MAC_DIR/TinyClips/Info.plist"
INFO_MAS_PLIST="$MAC_DIR/TinyClips/Info-MAS.plist"
PROJECT_PBXPROJ="$MAC_DIR/TinyClips.xcodeproj/project.pbxproj"

echo "Bumping Swift app version to $NEW_VERSION..."
echo ""

# Rewrite only the version line. PlistBuddy "Set" and plutil re-serialize the
# whole plist and reorder its keys, which buries the bump in an unrelated diff.
update_plist() {
    local plist="$1"
    local actual

    if [ ! -f "$plist" ]; then
        echo "❌ File not found: $plist"
        exit 1
    fi

    sed -i '' "/<key>CFBundleShortVersionString<\/key>/{n;s|<string>[^<]*</string>|<string>$NEW_VERSION</string>|;}" "$plist"

    actual="$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$plist")"
    if [ "$actual" != "$NEW_VERSION" ]; then
        echo "❌ Could not update $plist (CFBundleShortVersionString is $actual)"
        exit 1
    fi

    echo "✓ Updated $plist"
}

update_plist "$INFO_PLIST"
update_plist "$INFO_MAS_PLIST"

# Update project.pbxproj
if [ -f "$PROJECT_PBXPROJ" ]; then
    sed -E -i '' "s/(MARKETING_VERSION = )[0-9]+(\.[0-9]+)*;/\1$NEW_VERSION;/g" "$PROJECT_PBXPROJ"

    TOTAL_COUNT="$(grep -c "MARKETING_VERSION = " "$PROJECT_PBXPROJ" || true)"
    UPDATED_COUNT="$(grep -cF "MARKETING_VERSION = $NEW_VERSION;" "$PROJECT_PBXPROJ" || true)"
    if [ "$TOTAL_COUNT" -eq 0 ] || [ "$UPDATED_COUNT" -ne "$TOTAL_COUNT" ]; then
        echo "❌ Could not update $PROJECT_PBXPROJ ($UPDATED_COUNT of $TOTAL_COUNT MARKETING_VERSION entries are $NEW_VERSION)"
        exit 1
    fi

    echo "✓ Updated $PROJECT_PBXPROJ ($UPDATED_COUNT MARKETING_VERSION entries)"
else
    echo "❌ File not found: $PROJECT_PBXPROJ"
    exit 1
fi

echo ""
echo "✅ Version bump complete! Updated to $NEW_VERSION"
echo ""
echo "Verification:"
echo "Info.plist:"
grep -A1 "CFBundleShortVersionString" "$INFO_PLIST" | tail -2
echo ""
echo "Info-MAS.plist:"
grep -A1 "CFBundleShortVersionString" "$INFO_MAS_PLIST" | tail -2
echo ""
echo "project.pbxproj MARKETING_VERSION entries:"
grep "MARKETING_VERSION = " "$PROJECT_PBXPROJ" | sort -u
