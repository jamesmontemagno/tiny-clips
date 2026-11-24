#!/usr/bin/env python3
"""Require a fresh app build before the agent stops, but only when app source
changed and only for the platform the agent is running on (macOS builds the
Xcode schemes, Windows builds the WinUI app, other hosts are skipped).

preToolUse snapshots the content of source files an edit tool is about to
touch; postToolUse counts the edit only if a file's content actually changed,
so failed or no-op edits never require a build."""

import hashlib
import json
import re
import sys
from pathlib import Path
from urllib.parse import unquote, urlparse


STATE_DIR_NAME = ".state"
STATE_FILE_SUFFIX = "-build-validation.json"
EDIT_TOOL_NAMES = {
    "apply_patch",
    "create",
    "create_file",
    "edit",
    "multi_replace_string_in_file",
    "replace_string_in_file",
    "str_replace_editor",
    "vscode_renameSymbol",
}
BUILD_TOOL_NAMES = {
    "bash",
    "create_and_run_task",
    "execution_subagent",
    "powershell",
    "run_in_terminal",
    "runTask",
    "task",
}
PATCH_FILE_PATTERN = re.compile(
    r"^\*\*\*\s+(?:Add|Update|Delete)\s+File:\s+(.+?)(?:\s+->\s+.+)?$",
    re.MULTILINE,
)
SUCCESS_PATTERNS = (
    re.compile(r"\bBUILD SUCCEEDED\b", re.IGNORECASE),
    re.compile(r"\bTEST SUCCEEDED\b", re.IGNORECASE),
    re.compile(r"\bexit code:?\s*0\b", re.IGNORECASE),
)
FAILURE_PATTERNS = (
    re.compile(r"\bBUILD FAILED\b", re.IGNORECASE),
    re.compile(r"\bTEST FAILED\b", re.IGNORECASE),
    re.compile(r"\bexit code:?\s*-?[1-9]\d*\b", re.IGNORECASE),
    re.compile(r"\b[1-9]\d*\s+Error\(s\)", re.IGNORECASE),
)


def xcode_scheme_check(scheme):
    pattern = re.compile(
        rf"\bxcodebuild\b.*?(?:^|\s)-?scheme\s+[\"']?{re.escape(scheme)}[\"']?(?=[\s;&|()<>]|$)",
        re.IGNORECASE | re.DOTALL,
    )
    return lambda text: pattern.search(text) is not None


DOTNET_BUILD_PATTERN = re.compile(r"\b(?:dotnet\s+(?:build|msbuild)|msbuild)\b", re.IGNORECASE)
WINDOWS_APP_TARGET_PATTERN = re.compile(r"TinyClips\.App\b|TinyClips\.Windows\.slnx?\b", re.IGNORECASE)


def windows_app_check(text):
    return DOTNET_BUILD_PATTERN.search(text) is not None and WINDOWS_APP_TARGET_PATTERN.search(text) is not None


MAC_BUILD_FLAGS = 'CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO'
PLATFORMS = {
    "mac": {
        "sourcePrefixes": (
            "mac/TinyClips/",
            "mac/TinyClips.xcodeproj/",
        ),
        "checks": {
            "TinyClips": xcode_scheme_check("TinyClips"),
            "TinyClipsMAS": xcode_scheme_check("TinyClipsMAS"),
        },
        "commands": (
            f"xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClips -configuration Debug {MAC_BUILD_FLAGS}",
            f"xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClipsMAS -configuration Debug {MAC_BUILD_FLAGS}",
        ),
    },
    "windows": {
        "sourcePrefixes": (
            "windows/src/",
            "windows/Directory.Build.props",
            "windows/TinyClips.Windows.slnx",
        ),
        "checks": {
            "TinyClips.App": windows_app_check,
        },
        "commands": (
            "dotnet build windows/src/TinyClips.App/TinyClips.App.csproj -c Debug -p:Platform=x64",
        ),
    },
}


def host_platform():
    if sys.platform == "darwin":
        return "mac"
    if sys.platform == "win32":
        return "windows"
    return None


def emit(payload):
    sys.stdout.write(json.dumps(payload))


def warn(message):
    sys.stderr.write(f"{message}\n")


def field(payload, *names):
    for name in names:
        if name in payload:
            return payload[name]
    return None


def normalize_path(value, repo_root):
    if not isinstance(value, str) or not value:
        return None

    path_value = value
    if path_value.startswith("file://"):
        path_value = unquote(urlparse(path_value).path)
        if re.match(r"^/[A-Za-z]:", path_value):
            path_value = path_value[1:]

    try:
        path = Path(path_value)
        if not path.is_absolute():
            path = repo_root / path
        path = path.resolve()
        return path.relative_to(repo_root).as_posix()
    except (OSError, ValueError):
        return None


def extract_paths_from_patch(patch_text, repo_root):
    paths = set()
    if not isinstance(patch_text, str):
        return paths

    for match in PATCH_FILE_PATTERN.finditer(patch_text):
        normalized = normalize_path(match.group(1).strip(), repo_root)
        if normalized:
            paths.add(normalized)

    return paths


def extract_paths_from_payload(payload, repo_root):
    paths = set()
    if isinstance(payload, str):
        return extract_paths_from_patch(payload, repo_root)

    def visit(value, parent_key=None):
        if isinstance(value, dict):
            for key, nested_value in value.items():
                visit(nested_value, key)
            return

        if isinstance(value, list):
            for item in value:
                visit(item, parent_key)
            return

        if not isinstance(value, str):
            return

        if parent_key in {"input", "patch"}:
            paths.update(extract_paths_from_patch(value, repo_root))

        if parent_key in {"filePath", "filePaths", "path", "uri", "oldPath", "newPath", "old_path", "new_path"}:
            normalized = normalize_path(value, repo_root)
            if normalized:
                paths.add(normalized)

    visit(payload)
    return paths


def collect_strings(value):
    strings = []

    def visit(node):
        if isinstance(node, dict):
            for child in node.values():
                visit(child)
        elif isinstance(node, list):
            for child in node:
                visit(child)
        elif isinstance(node, str):
            strings.append(node)

    visit(value)
    return "\n".join(strings)


def state_file(repo_root, session_id):
    return repo_root / ".github" / "hooks" / STATE_DIR_NAME / f"{session_id}{STATE_FILE_SUFFIX}"


def load_state(path, platform):
    checks = PLATFORMS[platform]["checks"]
    data = {}
    if path.exists():
        try:
            data = json.loads(path.read_text())
        except (json.JSONDecodeError, OSError):
            data = {}

    if data.get("platform") != platform:
        data = {}

    validated = data.get("validated", {})
    content_hashes = data.get("contentHashes", {})
    return {
        "platform": platform,
        "sourceRevision": int(data.get("sourceRevision", 0)),
        "validated": {name: int(validated.get(name, 0)) for name in checks},
        "contentHashes": dict(content_hashes) if isinstance(content_hashes, dict) else {},
    }


def save_state(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, sort_keys=True))


def is_source_path(path, platform):
    return any(path.startswith(prefix) for prefix in PLATFORMS[platform]["sourcePrefixes"])


def content_hash(repo_root, relative_path):
    try:
        return hashlib.sha256((repo_root / relative_path).read_bytes()).hexdigest()
    except (FileNotFoundError, IsADirectoryError, NotADirectoryError):
        return None


def source_paths(tool_args, repo_root, platform):
    return sorted(path for path in extract_paths_from_payload(tool_args, repo_root) if is_source_path(path, platform))


def snapshot_sources(tool_args, repo_root, state, platform):
    hashes = state["contentHashes"]
    missing = [path for path in source_paths(tool_args, repo_root, platform) if path not in hashes]
    if not missing:
        return state
    snapshot = {**hashes, **{path: content_hash(repo_root, path) for path in missing}}
    return {**state, "contentHashes": snapshot}


def mark_edits(tool_args, repo_root, state, platform):
    paths = source_paths(tool_args, repo_root, platform)
    if not paths:
        return state

    hashes = dict(state["contentHashes"])
    changed = False
    for path in paths:
        current = content_hash(repo_root, path)
        # Without a preToolUse snapshot the previous content is unknown, so assume it changed.
        if path not in hashes or hashes[path] != current:
            changed = True
        hashes[path] = current

    revision = state["sourceRevision"] + (1 if changed else 0)
    return {**state, "sourceRevision": revision, "contentHashes": hashes}


def build_succeeded(result_text):
    if any(pattern.search(result_text) for pattern in FAILURE_PATTERNS):
        return False
    return any(pattern.search(result_text) for pattern in SUCCESS_PATTERNS)


def mark_builds(tool_args, tool_result, state, platform):
    command_text = collect_strings(tool_args)
    matched = [name for name, check in PLATFORMS[platform]["checks"].items() if check(command_text)]
    if not matched or not build_succeeded(collect_strings(tool_result)):
        return state

    validated = dict(state["validated"])
    for name in matched:
        validated[name] = state["sourceRevision"]
    return {**state, "validated": validated}


def handle_pre_tool_use(payload, repo_root, session_id, platform):
    if field(payload, "toolName", "tool_name") not in EDIT_TOOL_NAMES:
        return

    tool_args = field(payload, "toolArgs", "tool_input") or {}
    if not source_paths(tool_args, repo_root, platform):
        return

    state_path = state_file(repo_root, session_id)
    state = load_state(state_path, platform)
    updated = snapshot_sources(tool_args, repo_root, state, platform)
    if updated != state:
        save_state(state_path, updated)


def handle_post_tool_use(payload, repo_root, session_id, platform):
    tool_name = field(payload, "toolName", "tool_name")
    is_edit = tool_name in EDIT_TOOL_NAMES
    is_build = tool_name in BUILD_TOOL_NAMES
    if not is_edit and not is_build:
        return

    tool_args = field(payload, "toolArgs", "tool_input") or {}
    state_path = state_file(repo_root, session_id)
    state = load_state(state_path, platform)

    # Builds only matter after source edits; skip the state write for unrelated commands.
    if is_build and state["sourceRevision"] == 0:
        return

    updated = state
    if is_edit:
        updated = mark_edits(tool_args, repo_root, updated, platform)
    if is_build:
        updated = mark_builds(tool_args, field(payload, "toolResult", "tool_result") or {}, updated, platform)

    if updated != state:
        save_state(state_path, updated)


def handle_stop(payload, repo_root, session_id, platform):
    if payload.get("stop_hook_active"):
        return None

    state_path = state_file(repo_root, session_id)
    if not state_path.exists():
        return None

    state = load_state(state_path, platform)
    source_revision = state["sourceRevision"]
    if source_revision == 0:
        return None

    missing = [name for name, revision in state["validated"].items() if revision < source_revision]
    if not missing:
        return None

    commands = " and ".join(PLATFORMS[platform]["commands"])
    return {
        "decision": "block",
        "reason": (
            "App source files changed since the last successful build on this platform. "
            f"Run a successful build for: {', '.join(missing)}. "
            f"Use: {commands}. Run it once after your final edits rather than after each change."
        ),
    }


def main():
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        warn("require_build_validation hook received invalid JSON input.")
        emit({})
        return

    cwd = payload.get("cwd")
    session_id = field(payload, "sessionId", "session_id")
    event_name = sys.argv[1] if len(sys.argv) > 1 else None
    platform = host_platform()
    if not cwd or not session_id or not event_name or platform is None:
        emit({})
        return

    repo_root = Path(cwd).resolve()
    result = None

    try:
        if event_name == "preToolUse":
            handle_pre_tool_use(payload, repo_root, session_id, platform)
        elif event_name == "postToolUse":
            handle_post_tool_use(payload, repo_root, session_id, platform)
        elif event_name == "agentStop":
            result = handle_stop(payload, repo_root, session_id, platform)
    except OSError as error:
        warn(f"require_build_validation hook warning: {error}")

    emit(result or {})


if __name__ == "__main__":
    main()
