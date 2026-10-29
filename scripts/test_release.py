from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

SCRIPT = Path(__file__).with_name("release.py")
SPEC = importlib.util.spec_from_file_location("release_tool", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class ReleaseManifestTests(unittest.TestCase):
    def test_changelog_is_deterministic(self) -> None:
        manifest = {
            "version": "1.0.0-alpha1",
            "releaseDate": "Unreleased",
            "summary": "Summary.",
            "changes": {
                "Added": ["Capability."],
                "Changed": [],
                "Fixed": [],
                "Security": ["Boundary."],
            },
            "roadmapCompleted": ["FND-01"],
            "releaseNotes": "docs/releases/1.0.0-alpha1.md",
        }
        rendered = MODULE.render_changelog(manifest)
        self.assertIn("## [1.0.0-alpha1] - Unreleased", rendered)
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

    def test_current_pointer_resolves_prerelease_manifest(self) -> None:
        root = Path(tempfile.mkdtemp())
        release = root / "release"
        release.mkdir()
        (release / "current.json").write_text(
            json.dumps(
                {"schemaVersion": 1, "manifest": "1.0.0-alpha1.json"}
            ),
            encoding="utf-8",
        )
        expected = release / "1.0.0-alpha1.json"
        expected.write_text("{}", encoding="utf-8")
        self.assertEqual(expected, MODULE.resolve_manifest(root, None))

    def test_semver_rejects_leading_zero_prerelease_numbers(self) -> None:
        self.assertIsNotNone(MODULE.VERSION.fullmatch("1.0.0-alpha1"))
        self.assertIsNone(MODULE.VERSION.fullmatch("1.0.0-alpha.01"))


class PublicationPreparationTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        (self.root / "release").mkdir()
        (self.root / "docs" / "releases").mkdir(parents=True)
        self.manifest_path = self.root / "release" / "1.0.0-alpha1.json"
        self.notes_path = self.root / "docs" / "releases" / "1.0.0-alpha1.md"
        self.changelog_path = self.root / "CHANGELOG.md"
        self.manifest = {
            "schemaVersion": 1,
            "version": "1.0.0-alpha1",
            "releaseDate": "Unreleased",
            "channel": "alpha",
            "sourceBranch": "main",
            "tag": "v1.0.0-alpha1",
            "longTermSupport": False,
            "compatibilityBaseline": None,
            "releaseNotes": "docs/releases/1.0.0-alpha1.md",
            "summary": "Summary.",
            "changes": {section: [] for section in MODULE.REQUIRED_CHANGE_SECTIONS},
            "roadmapCompleted": [],
            "gates": sorted(MODULE.REQUIRED_GATES),
            "artifacts": sorted(MODULE.REQUIRED_ARTIFACTS),
        }
        (self.root / "Directory.Build.props").write_text(
            "<Project><Version>1.0.0-alpha1</Version></Project>", encoding="utf-8"
        )
        (self.root / "supply-chain-policy.json").write_text(
            json.dumps({"packageVersion": "1.0.0-alpha1"}), encoding="utf-8"
        )
        self.write_release("Unreleased")

    def write_release(self, release_date: str) -> None:
        self.manifest["releaseDate"] = release_date
        self.manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        self.notes_path.write_text(
            f"# Nao 1.0.0-alpha1 release notes\n\n- Release date: {release_date}\n",
            encoding="utf-8",
        )
        self.changelog_path.write_text(
            MODULE.render_changelog(self.manifest), encoding="utf-8"
        )

    def snapshot(self) -> list[bytes]:
        return [
            path.read_bytes()
            for path in (self.manifest_path, self.notes_path, self.changelog_path)
        ]

    def test_unreleased_date_is_resolved_consistently_in_utc(self) -> None:
        with patch.object(MODULE, "datetime") as clock:
            clock.now.return_value = datetime(2026, 10, 30, tzinfo=timezone.utc)
            self.assertEqual([], MODULE.prepare_publication(self.root, self.manifest_path))
            clock.now.assert_called_once_with(timezone.utc)
        manifest = MODULE.load_manifest(self.manifest_path)
        self.assertEqual("2026-10-30", manifest["releaseDate"])
        self.assertEqual(
            "2026-10-30", MODULE.release_date(self.notes_path.read_text(encoding="utf-8"))
        )
        self.assertEqual(
            MODULE.render_changelog(manifest),
            self.changelog_path.read_text(encoding="utf-8"),
        )
        self.assertEqual([], MODULE.validate_manifest(self.root, self.manifest_path))

    def test_explicit_date_is_preserved_without_rewriting_files(self) -> None:
        self.write_release("2026-10-28")
        before = self.snapshot()
        self.assertEqual([], MODULE.prepare_publication(self.root, self.manifest_path))
        self.assertEqual(before, self.snapshot())

    def test_preparation_is_idempotent(self) -> None:
        self.assertEqual([], MODULE.prepare_publication(self.root, self.manifest_path))
        before = self.snapshot()
        with patch.object(MODULE, "datetime") as clock:
            self.assertEqual([], MODULE.prepare_publication(self.root, self.manifest_path))
            clock.now.assert_not_called()
        self.assertEqual(before, self.snapshot())

    def test_mismatched_notes_block_preparation_without_changes(self) -> None:
        self.notes_path.write_text(
            "# Nao 1.0.0-alpha1 release notes\n\n- Release date: 2026-10-28\n",
            encoding="utf-8",
        )
        before = self.snapshot()
        errors = MODULE.prepare_publication(self.root, self.manifest_path)
        self.assertIn("release date differs between manifest and release notes", errors)
        self.assertEqual(before, self.snapshot())

    def test_stale_changelog_blocks_preparation_without_changes(self) -> None:
        self.changelog_path.write_text("Stale changelog\n", encoding="utf-8")
        before = self.snapshot()
        errors = MODULE.prepare_publication(self.root, self.manifest_path)
        self.assertIn("CHANGELOG.md is not the deterministic manifest rendering", errors)
        self.assertEqual(before, self.snapshot())

    def test_invalid_date_blocks_preparation_without_changes(self) -> None:
        self.write_release("2026-02-30")
        before = self.snapshot()
        errors = MODULE.prepare_publication(self.root, self.manifest_path)
        self.assertIn("releaseDate must be Unreleased or an ISO release date", errors)
        self.assertEqual(before, self.snapshot())


if __name__ == "__main__":
    unittest.main()
