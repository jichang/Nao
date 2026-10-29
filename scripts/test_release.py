from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("release.py")
SPEC = importlib.util.spec_from_file_location("release_tool", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class ReleaseManifestTests(unittest.TestCase):
    def test_changelog_is_deterministic(self) -> None:
        manifest = {
            "version": "1.0.0",
            "releaseDate": "Unreleased",
            "summary": "Summary.",
            "changes": {
                "Added": ["Capability."],
                "Changed": [],
                "Fixed": [],
                "Security": ["Boundary."],
            },
            "roadmapCompleted": ["FND-01"],
            "releaseNotes": "docs/releases/1.0.0.md",
        }
        rendered = MODULE.render_changelog(manifest)
        self.assertIn("## [1.0.0] - Unreleased", rendered)
        self.assertIn("- None", rendered)
        self.assertEqual(rendered, MODULE.render_changelog(manifest))

    def test_completed_roadmap_requires_every_checkbox(self) -> None:
        root = Path(tempfile.mkdtemp())
        roadmap = root / "docs" / "roadmap"
        roadmap.mkdir(parents=True)
        (roadmap / "00-test.md").write_text(
            "## FND-01 — Done\n\n- [x] One\n- [x] Two\n"
            "## FND-02 — Pending\n\n- [x] One\n- [ ] Two\n",
            encoding="utf-8",
        )
        self.assertEqual({"FND-01"}, MODULE.completed_roadmap(root))

    def test_manifest_loader_rejects_non_object(self) -> None:
        path = Path(tempfile.mkdtemp()) / "release.json"
        path.write_text(json.dumps([]), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "JSON object"):
            MODULE.load_manifest(path)


if __name__ == "__main__":
    unittest.main()
