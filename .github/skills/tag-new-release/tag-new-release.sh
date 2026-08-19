#!/bin/bash

set -euo pipefail

usage() {
    cat <<'EOF'
Usage:
  ./tag-new-release.sh --platform <mac|windows> [--version <tag>] [--dry-run]

Examples:
  ./tag-new-release.sh --platform mac --version v1.5.0-mac
  ./tag-new-release.sh --platform windows --version v1.0.9-windows
  ./tag-new-release.sh --platform mac

Notes:
  - Requires a clean working tree.
  - Updates changelog by inserting a dated release heading after "Unreleased".
  - Commits changelog update, then creates an annotated tag with release notes.
  - Does not push commits/tags.
EOF
}

PLATFORM=""
INPUT_VERSION=""
DRY_RUN="false"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --platform)
            PLATFORM="${2:-}"
            shift 2
            ;;
        --version)
            INPUT_VERSION="${2:-}"
            shift 2
            ;;
        --dry-run)
            DRY_RUN="true"
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "❌ Unknown argument: $1" >&2
            usage
            exit 1
            ;;
    esac
done

if [[ -z "$PLATFORM" ]]; then
    echo "❌ --platform is required." >&2
    usage
    exit 1
fi

if [[ "$PLATFORM" != "mac" && "$PLATFORM" != "windows" ]]; then
    echo "❌ --platform must be one of: mac, windows." >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
cd "$REPO_ROOT"

if [[ -n "$(git status --porcelain)" ]]; then
    echo "❌ Working tree must be clean before tagging." >&2
    exit 1
fi

TODAY="$(date +%Y-%m-%d)"

if [[ "$PLATFORM" == "mac" ]]; then
    CHANGELOG_FILE="$REPO_ROOT/CHANGELOG.md"
    UNRELEASED_PATTERN='^## Unreleased$'
    APP_VERSION="$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$REPO_ROOT/mac/TinyClips/Info.plist")"
else
    CHANGELOG_FILE="$REPO_ROOT/windows/CHANGELOG.md"
    UNRELEASED_PATTERN='^## \[Unreleased\]$'
    APP_VERSION=""
fi

if [[ ! -f "$CHANGELOG_FILE" ]]; then
    echo "❌ Changelog not found: $CHANGELOG_FILE" >&2
    exit 1
fi

if ! grep -Eq "$UNRELEASED_PATTERN" "$CHANGELOG_FILE"; then
    echo "❌ Could not find expected Unreleased heading in $CHANGELOG_FILE" >&2
    exit 1
fi

if [[ -n "$INPUT_VERSION" ]]; then
    VERSION="$INPUT_VERSION"
else
    if [[ "$PLATFORM" == "mac" ]]; then
        if [[ "$APP_VERSION" =~ ^[0-9]+\.[0-9]+$ ]]; then
            VERSION="v${APP_VERSION}.0-mac"
        elif [[ "$APP_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
            VERSION="v${APP_VERSION}-mac"
        else
            echo "❌ mac app version must be X.Y or X.Y.Z in Info.plist, got: $APP_VERSION" >&2
            exit 1
        fi
    else
        LATEST_WINDOWS_TAG="$(git tag --list 'v*-windows' --sort=-creatordate | head -n 1)"
        if [[ -z "$LATEST_WINDOWS_TAG" ]]; then
            echo "❌ Could not infer a windows version. Provide --version." >&2
            exit 1
        fi
        if [[ "$LATEST_WINDOWS_TAG" =~ ^v([0-9]+)\.([0-9]+)\.([0-9]+)-windows$ ]]; then
            PATCH=$((BASH_REMATCH[3] + 1))
            VERSION="v${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${PATCH}-windows"
        else
            echo "❌ Latest windows tag does not match expected pattern: $LATEST_WINDOWS_TAG" >&2
            exit 1
        fi
    fi
fi

if [[ "$PLATFORM" == "mac" ]]; then
    if [[ ! "$VERSION" =~ ^v[0-9]+(\.[0-9]+){2,3}(-mac)?$ ]]; then
        echo "❌ Invalid mac version format: $VERSION" >&2
        echo "Expected: vX.Y.Z[-mac], vX.Y.Z.W[-mac]" >&2
        exit 1
    fi
    if [[ ! "$VERSION" =~ -mac$ ]]; then
        VERSION="${VERSION}-mac"
    fi
    if [[ -z "$INPUT_VERSION" && -n "$APP_VERSION" ]]; then
        if [[ "$APP_VERSION" =~ ^[0-9]+\.[0-9]+$ ]]; then
            EXPECTED_VERSION="v${APP_VERSION}.0-mac"
        elif [[ "$APP_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
            EXPECTED_VERSION="v${APP_VERSION}-mac"
        else
            echo "❌ mac app version must be X.Y or X.Y.Z in Info.plist, got: $APP_VERSION" >&2
            exit 1
        fi
        if [[ "$VERSION" != "$EXPECTED_VERSION" ]]; then
            echo "❌ mac tag version ($VERSION) does not align with Info.plist version ($APP_VERSION)." >&2
            exit 1
        fi
    fi
    RELEASE_HEADING="## ${VERSION} - ${TODAY}"
    SECTION_HEADER_PREFIX="### "
else
    if [[ ! "$VERSION" =~ ^v[0-9]+\.[0-9]+\.[0-9]+-windows$ ]]; then
        echo "❌ Invalid windows version format: $VERSION" >&2
        echo "Expected: vX.Y.Z-windows" >&2
        exit 1
    fi
    RELEASE_HEADING="## [${VERSION}] - ${TODAY}"
    SECTION_HEADER_PREFIX="### "
fi

if git rev-parse -q --verify "refs/tags/$VERSION" >/dev/null; then
    echo "❌ Tag already exists: $VERSION" >&2
    exit 1
fi

TMP_CHANGELOG="$(mktemp)"
TMP_TAG_MSG="$(mktemp)"
cleanup() {
    rm -f "$TMP_CHANGELOG" "$TMP_TAG_MSG"
}
trap cleanup EXIT

awk -v heading="$RELEASE_HEADING" '
BEGIN { inserted=0 }
{
    print $0
    if (!inserted && ($0 == "## Unreleased" || $0 == "## [Unreleased]")) {
        print ""
        print heading
        inserted=1
    }
}
END {
    if (!inserted) {
        exit 2
    }
}
' "$CHANGELOG_FILE" > "$TMP_CHANGELOG" || {
    echo "❌ Failed to update changelog heading." >&2
    exit 1
}

cp "$TMP_CHANGELOG" "$CHANGELOG_FILE"

if ! awk -v heading="$RELEASE_HEADING" '
BEGIN { cap=0; found=0; hasNotes=0 }
$0 == heading { cap=1; found=1; next }
/^## / && cap { exit }
cap {
    if ($0 ~ /^### / || $0 ~ /^- /) {
        hasNotes=1
    }
}
END { if (!found || !hasNotes) exit 1 }
' "$CHANGELOG_FILE"; then
    echo "❌ Release notes for ${VERSION} are missing after changelog update." >&2
    exit 1
fi

{
    printf 'Release %s\n\n' "$VERSION"
    awk -v heading="$RELEASE_HEADING" '
    BEGIN { cap=0 }
    $0 == heading { cap=1; next }
    /^## / && cap { exit }
    cap {
        if ($0 ~ /^### /) {
            sub(/^### /, "", $0)
            print $0 ":"
        } else {
            print $0
        }
    }
    ' "$CHANGELOG_FILE"
} > "$TMP_TAG_MSG"

if [[ "$DRY_RUN" == "true" ]]; then
    echo "ℹ️ Dry run only; no commit or tag created."
    echo "Platform: $PLATFORM"
    echo "Version: $VERSION"
    echo "Changelog: $CHANGELOG_FILE"
    echo ""
    echo "Tag message preview:"
    cat "$TMP_TAG_MSG"
    git checkout -- "$CHANGELOG_FILE"
    exit 0
fi

git add "$CHANGELOG_FILE"
git commit -m "Mark ${VERSION} release

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git tag -a "$VERSION" -F "$TMP_TAG_MSG"

echo "✅ Created commit and tag: $VERSION"
echo ""
git --no-pager show --no-patch --format=fuller "$VERSION"
echo ""
echo "Next steps:"
echo "  git push origin main"
echo "  git push origin ${VERSION}"
