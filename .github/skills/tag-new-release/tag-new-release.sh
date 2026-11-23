#!/bin/bash

set -euo pipefail

usage() {
    cat <<'EOF'
Usage:
  ./tag-new-release.sh --platform <mac|windows|agent> [--version <tag>] [--allow-empty] [--dry-run]

Examples:
  ./tag-new-release.sh --platform mac --version v1.5.0-mac
  ./tag-new-release.sh --platform windows --version v1.0.9-windows
  ./tag-new-release.sh --platform agent --version agent-v0.4.0
  ./tag-new-release.sh --platform mac

Notes:
  - Requires a clean working tree.
  - Updates changelog by inserting a dated release heading after "Unreleased".
  - Commits changelog update, then creates an annotated tag with release notes.
  - --allow-empty permits a maintenance release with no Unreleased bullets; it leaves the changelog unchanged.
  - Does not push commits/tags.
EOF
}

PLATFORM=""
INPUT_VERSION=""
DRY_RUN="false"
ALLOW_EMPTY="false"

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
        --allow-empty)
            ALLOW_EMPTY="true"
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

if [[ "$PLATFORM" != "mac" && "$PLATFORM" != "windows" && "$PLATFORM" != "agent" ]]; then
    echo "❌ --platform must be one of: mac, windows, agent." >&2
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
elif [[ "$PLATFORM" == "windows" ]]; then
    CHANGELOG_FILE="$REPO_ROOT/windows/CHANGELOG.md"
    UNRELEASED_PATTERN='^## \[Unreleased\]$'
    APP_VERSION=""
else
    CHANGELOG_FILE="$REPO_ROOT/agent/CHANGELOG.md"
    UNRELEASED_PATTERN='^## Unreleased$'
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
        if [[ "$PLATFORM" == "agent" ]]; then
            LATEST_AGENT_TAG="$(git tag --list 'agent-v*' --sort=-creatordate | head -n 1)"
            if [[ -z "$LATEST_AGENT_TAG" ]]; then
                VERSION="agent-v0.1.0"
            elif [[ "$LATEST_AGENT_TAG" =~ ^agent-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$ ]]; then
                PATCH=$((BASH_REMATCH[3] + 1))
                VERSION="agent-v${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${PATCH}"
            else
                echo "❌ Latest Agent tag does not match expected pattern: $LATEST_AGENT_TAG" >&2
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
elif [[ "$PLATFORM" == "windows" ]]; then
    if [[ ! "$VERSION" =~ ^v[0-9]+\.[0-9]+\.[0-9]+-windows$ ]]; then
        echo "❌ Invalid windows version format: $VERSION" >&2
        echo "Expected: vX.Y.Z-windows" >&2
        exit 1
    fi
    RELEASE_HEADING="## [${VERSION}] - ${TODAY}"
    SECTION_HEADER_PREFIX="### "
else
    if [[ ! "$VERSION" =~ ^agent-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$ ]]; then
        echo "❌ Invalid Agent version format: $VERSION" >&2
        echo "Expected: agent-vX.Y.Z or agent-vX.Y.Z-preview.1" >&2
        exit 1
    fi
    AGENT_VERSION="${VERSION#agent-v}"
    if ! python3 - "$AGENT_VERSION" "$REPO_ROOT" <<'PY'
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

version = sys.argv[1]
root = Path(sys.argv[2])
version_pattern = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?"
if not re.fullmatch(version_pattern, version):
    raise SystemExit(f"Invalid Agent version format: agent-v{version}")

project = ET.parse(root / "agent/src/TinyClips.Cli/TinyClips.Cli.csproj")
package_version = project.findtext(".//PackageVersion")
plugin = json.loads((root / "plugin.json").read_text(encoding="utf-8"))
registry = json.loads((root / "agent/src/TinyClips.Cli/.mcp/server.json").read_text(encoding="utf-8"))
versions = [
    ("CLI", package_version),
    ("plugin", plugin.get("version")),
    ("MCP", registry.get("version")),
]
packages = registry.get("packages")
if isinstance(packages, list) and len(packages) == 1:
    versions.append(("MCP package", packages[0].get("version")))
else:
    versions.append(("MCP package", None))

mismatches = [f"{name}={value or '(missing)'}" for name, value in versions if value != version]
if mismatches:
    details = ", ".join(mismatches)
    raise SystemExit(
        f"Tag version {version} must match committed Agent metadata ({details}). "
        "Run `node agent/scripts/bump-version.mjs <version>` and bump the Agent metadata first."
    )
PY
    then
        exit 1
    fi
    RELEASE_HEADING="## ${AGENT_VERSION} - ${TODAY}"
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

HAS_RELEASE_NOTES="true"
if ! awk -v heading="$RELEASE_HEADING" '
BEGIN { cap=0; found=0; hasNotes=0 }
$0 == heading { cap=1; found=1; next }
/^## / && cap { exit }
cap {
    if ($0 ~ /^- /) {
        hasNotes=1
    }
}
END { if (!found || !hasNotes) exit 1 }
' "$TMP_CHANGELOG"; then
    if [[ "$ALLOW_EMPTY" != "true" ]]; then
        echo "❌ Unreleased in ${CHANGELOG_FILE} contains no release notes. Add bullet points before preparing ${VERSION}." >&2
        exit 1
    fi
    HAS_RELEASE_NOTES="false"
fi

if [[ "$HAS_RELEASE_NOTES" == "true" ]]; then
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
        ' "$TMP_CHANGELOG"
    } > "$TMP_TAG_MSG"
else
    printf 'Release %s\n\nMaintenance release.\n' "$VERSION" > "$TMP_TAG_MSG"
fi

if [[ "$DRY_RUN" == "true" ]]; then
    echo "ℹ️ Dry run only; no commit or tag created."
    echo "Platform: $PLATFORM"
    echo "Version: $VERSION"
    if [[ "$HAS_RELEASE_NOTES" == "true" ]]; then
        echo "Changelog: $CHANGELOG_FILE"
    else
        echo "Changelog: unchanged (no Unreleased notes)"
    fi
    echo ""
    echo "Tag message preview:"
    cat "$TMP_TAG_MSG"
    exit 0
fi

if [[ "$HAS_RELEASE_NOTES" == "true" ]]; then
    cp "$TMP_CHANGELOG" "$CHANGELOG_FILE"
    git add "$CHANGELOG_FILE"
    git commit -m "Mark ${VERSION} release

Co-authored-by: copilot-swe-agent[bot] <198982749+Copilot@users.noreply.github.com>"
else
    git commit --allow-empty -m "Mark ${VERSION} release

Co-authored-by: copilot-swe-agent[bot] <198982749+Copilot@users.noreply.github.com>"
fi
git tag -a "$VERSION" -F "$TMP_TAG_MSG"

echo "✅ Created commit and tag: $VERSION"
echo ""
git --no-pager show --no-patch --format=fuller "$VERSION"
echo ""
echo "Next steps:"
echo "  git push origin main"
echo "  git push origin ${VERSION}"
