#!/usr/bin/env python3

import json
import re
import sys
from pathlib import Path


STATE_DIR_NAME = ".state"
STATE_FILE_SUFFIX = "-build-validation.json"
SOURCE_PREFIXES = (
    "mac/TinyClips/",
    "mac/TinyClips.xcodeproj/",
)
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
TARGET_SCHEMES = ("TinyClips", "TinyClipsMAS")
PATCH_FILE_PATTERN = re.compile(
    r"^\*\*\*\s+(?:Add|Update|Delete)\s+File:\s+(.+?)(?:\s+->\s+.+)?$",
    re.MULTILINE,
)
SUCCESS_PATTERNS = (
    re.compile(r"\bBUILD SUCCEEDED\b", re.IGNORECASE),
    re.compile(r"\bexit code:?\s*0\b", re.IGNORECASE),
    re.compile(r"\bExit Code:\s*0\b", re.IGNORECASE),
)


def emit(payload):
    sys.stdout.write(json.dumps(payload))


def warn(message):
    sys.stderr.write(f"{message}\n")


def normalize_path(value, repo_root):
    if not isinstance(value, str) or not value:
        return None

    path_value = value
    if path_value.startswith("file://"):
        path_value = path_value[7:]

    try:
        path = Path(path_value)
    except OSError:
        return None

    if not path.is_absolute():
        path = (repo_root / path).resolve()
    else:
        path = path.resolve()

    try:
        return path.relative_to(repo_root).as_posix()
    except ValueError:
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
            return

        if isinstance(node, list):
            for child in node:
                visit(child)
            return

        if isinstance(node, str):
            strings.append(node)

    visit(value)
    return strings


def state_file(repo_root, session_id):
    return repo_root / ".github" / "hooks" / STATE_DIR_NAME / f"{session_id}{STATE_FILE_SUFFIX}"


def load_state(path):
    if not path.exists():
        return {
            "editedPaths": [],
            "sourceRevision": 0,
            "validatedSchemes": {scheme: 0 for scheme in TARGET_SCHEMES},
        }

    try:
        data = json.loads(path.read_text())
    except (json.JSONDecodeError, OSError):
        data = {}

    validated = data.get("validatedSchemes", {})
    return {
        "editedPaths": data.get("editedPaths", []),
        "sourceRevision": data.get("sourceRevision", 0),
        "validatedSchemes": {
            scheme: int(validated.get(scheme, 0)) for scheme in TARGET_SCHEMES
        },
    }


def save_state(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, sort_keys=True))


def is_source_path(path):
    return any(path.startswith(prefix) for prefix in SOURCE_PREFIXES)


def mark_edits(tool_args, repo_root, state):
    edited_paths = sorted(extract_paths_from_payload(tool_args, repo_root))
    if not edited_paths:
        return state

    merged_paths = sorted(set(state.get("editedPaths", [])) | set(edited_paths))
    source_revision = state.get("sourceRevision", 0)
    if any(is_source_path(path) for path in edited_paths):
        source_revision += 1

    return {
        **state,
        "editedPaths": merged_paths,
        "sourceRevision": source_revision,
    }


def payload_mentions_scheme(text, scheme):
    return (
        re.search(
            rf"(?:^|\s)-?scheme\s+{re.escape(scheme)}(?=\s|$)",
            text,
            re.IGNORECASE,
        )
        is not None
    )


def payload_has_success(text):
    return any(pattern.search(text) for pattern in SUCCESS_PATTERNS)


def mark_builds(payload, state):
    combined_text = "\n".join(collect_strings(payload))
    if "xcodebuild" not in combined_text:
        return state

    if not payload_has_success(combined_text):
        return state

    validated_schemes = dict(state.get("validatedSchemes", {}))
    current_revision = state.get("sourceRevision", 0)
    for scheme in TARGET_SCHEMES:
        if payload_mentions_scheme(combined_text, scheme):
            validated_schemes[scheme] = current_revision

    return {
        **state,
        "validatedSchemes": validated_schemes,
    }


def handle_post_tool_use(payload, repo_root):
    tool_name = payload.get("toolName")
    if tool_name not in EDIT_TOOL_NAMES and tool_name not in BUILD_TOOL_NAMES:
        emit({})
        return

    state_path = state_file(repo_root, payload["sessionId"])
    state = load_state(state_path)

    if tool_name in EDIT_TOOL_NAMES:
        state = mark_edits(payload.get("toolArgs", {}), repo_root, state)

    if tool_name in BUILD_TOOL_NAMES:
        state = mark_builds(payload, state)

    save_state(state_path, state)
    emit({})


def handle_stop(payload, repo_root):
    if payload.get("stop_hook_active"):
        emit({})
        return

    state = load_state(state_file(repo_root, payload["sessionId"]))
    source_revision = state.get("sourceRevision", 0)
    if source_revision == 0:
        emit({})
        return

    missing_schemes = [
        scheme
        for scheme, validated_revision in state.get("validatedSchemes", {}).items()
        if validated_revision < source_revision
    ]
    if not missing_schemes:
        emit({})
        return

    reason = (
        "Product source files changed in this session, but required scheme builds are missing or stale. "
        f"Run successful builds for: {', '.join(missing_schemes)}. "
        "Preferred commands: xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClips -configuration Debug "
        "CODE_SIGN_IDENTITY=\"\" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO and xcodebuild build -project "
        "mac/TinyClips.xcodeproj -scheme TinyClipsMAS -configuration Debug CODE_SIGN_IDENTITY=\"\" CODE_SIGNING_REQUIRED=NO "
        "CODE_SIGNING_ALLOWED=NO."
    )
    emit({"decision": "block", "reason": reason})


def main():
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        warn("require_build_validation hook received invalid JSON input.")
        emit({})
        return

    cwd = payload.get("cwd")
    session_id = payload.get("sessionId")
    event_name = sys.argv[1] if len(sys.argv) > 1 else None
    if not cwd or not session_id or not event_name:
        emit({})
        return

    repo_root = Path(cwd).resolve()

    try:
        if event_name == "postToolUse":
            handle_post_tool_use(payload, repo_root)
            return

        if event_name == "agentStop":
            handle_stop(payload, repo_root)
            return
    except OSError as error:
        warn(f"require_build_validation hook warning: {error}")
        emit({})
        return

    emit({})


if __name__ == "__main__":
    main()