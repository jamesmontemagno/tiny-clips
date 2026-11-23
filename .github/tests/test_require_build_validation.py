import importlib.util
from pathlib import Path
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


class RequireBuildValidationTests(unittest.TestCase):
    def test_scheme_match_accepts_shell_token_delimiters(self):
        for delimiter in (";", "&&", "||", "|", ")", ">"):
            with self.subTest(delimiter=delimiter):
                self.assertTrue(
                    MODULE.payload_mentions_scheme(
                        f"xcodebuild -scheme TinyClips{delimiter} echo done",
                        "TinyClips",
                    )
                )

    def test_scheme_match_does_not_accept_longer_scheme_names(self):
        for name in ("TinyClipsMAS", "TinyClipsDebug", "TinyClips-Preview"):
            with self.subTest(name=name):
                self.assertFalse(
                    MODULE.payload_mentions_scheme(
                        f"xcodebuild -scheme {name}; echo done",
                        "TinyClips",
                    )
                )

    def test_chained_scheme_commands_are_recorded_from_arguments(self):
        state = {
            "editedPaths": ["mac/TinyClips/TinyClipsApp.swift"],
            "sourceRevision": 1,
            "validatedSchemes": {"TinyClips": 0, "TinyClipsMAS": 0},
        }

        for scheme, delimiter in (("TinyClips", ";"), ("TinyClipsMAS", "&&")):
            state = MODULE.mark_builds(
                {
                    "toolArgs": {
                        "command": (
                            "xcodebuild -project mac/TinyClips.xcodeproj "
                            f"-scheme {scheme}{delimiter} echo done"
                        )
                    },
                    "toolResult": {
                        "resultType": "success",
                        "textResultForLlm": "BUILD SUCCEEDED",
                    },
                },
                state,
            )

        self.assertEqual(
            state["validatedSchemes"],
            {"TinyClips": 1, "TinyClipsMAS": 1},
        )


if __name__ == "__main__":
    unittest.main()
