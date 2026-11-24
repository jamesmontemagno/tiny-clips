import importlib.util
from pathlib import Path
import tempfile
import unittest


HOOK = (
    Path(__file__).resolve().parents[2]
    / ".github"
    / "hooks"
    / "require_build_validation.py"
)
SPEC = importlib.util.spec_from_file_location("require_build_validation", HOOK)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)

SESSION = "test-session"
MAC_SOURCE = "mac/TinyClips/TinyClipsApp.swift"
MAC_BUILDS = (
    "xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClips",
    "xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClipsMAS",
)


class RequireBuildValidationTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name).resolve()
        self.write(MAC_SOURCE, "original")

    def write(self, relative_path, text):
        path = self.root / relative_path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)

    def edit(self, relative_path, new_text, platform="mac"):
        payload = {"toolName": "edit", "toolArgs": {"path": relative_path}}
        MODULE.handle_pre_tool_use(payload, self.root, SESSION, platform)
        if new_text is not None:
            self.write(relative_path, new_text)
        MODULE.handle_post_tool_use(payload, self.root, SESSION, platform)

    def build(self, command, output, platform="mac"):
        MODULE.handle_post_tool_use(
            {"toolName": "bash", "toolArgs": {"command": command}, "toolResult": {"textResultForLlm": output}},
            self.root,
            SESSION,
            platform,
        )

    def stop(self, platform="mac"):
        return MODULE.handle_stop({}, self.root, SESSION, platform)

    def test_scheme_match_accepts_shell_token_delimiters(self):
        check = MODULE.xcode_scheme_check("TinyClips")
        for delimiter in (";", "&&", "||", "|", ")", ">"):
            with self.subTest(delimiter=delimiter):
                self.assertTrue(check(f"xcodebuild -scheme TinyClips{delimiter} echo done"))

    def test_scheme_match_does_not_accept_longer_scheme_names(self):
        check = MODULE.xcode_scheme_check("TinyClips")
        for name in ("TinyClipsMAS", "TinyClipsDebug", "TinyClips-Preview"):
            with self.subTest(name=name):
                self.assertFalse(check(f"xcodebuild -scheme {name}; echo done"))

    def test_no_op_and_failed_edits_do_not_require_build(self):
        self.edit(MAC_SOURCE, "original")
        self.edit(MAC_SOURCE, None)
        self.assertIsNone(self.stop())

    def test_changed_source_requires_successful_builds_after_final_edit(self):
        self.edit(MAC_SOURCE, "changed")
        self.assertEqual(self.stop()["decision"], "block")

        self.build(MAC_BUILDS[0], "** BUILD FAILED **")
        self.build(MAC_BUILDS[1], "** BUILD SUCCEEDED **")
        self.assertIn("build for: TinyClips.", self.stop()["reason"])

        self.build(MAC_BUILDS[0], "** BUILD SUCCEEDED **")
        self.assertIsNone(self.stop())

        self.edit(MAC_SOURCE, "changed again")
        self.assertEqual(self.stop()["decision"], "block")

    def test_other_platform_sources_and_early_builds_are_ignored(self):
        self.write("windows/src/TinyClips.App/App.xaml.cs", "original")
        self.build(MAC_BUILDS[0], "** BUILD SUCCEEDED **")
        self.edit("windows/src/TinyClips.App/App.xaml.cs", "changed", platform="mac")
        self.assertFalse(MODULE.state_file(self.root, SESSION).exists())
        self.assertIsNone(self.stop())


if __name__ == "__main__":
    unittest.main()