from __future__ import annotations

import importlib.util
import json
import shutil
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

    def test_release_versions_come_from_arbitrary_valid_tags(self) -> None:
        for version in ("1.0.0-alpha2", "2.4.0-beta.3", "7.0.1-rc2", "9.4.2"):
            with self.subTest(version=version):
                self.assertEqual(version, MODULE.release_version(f"v{version}"))

    def test_release_tags_reject_development_and_malformed_versions(self) -> None:
        for tag in ("main", "1.0.0", "v1.0.0-dev", "v1.0.0-alpha.01", "v1.0.0+build", "v../escape"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                MODULE.release_version(tag)


class PublicationPreparationTests(unittest.TestCase):
    def setUp(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        (self.root / "release").mkdir()
        (self.root / "docs" / "releases").mkdir(parents=True)
        self.tag = "v1.0.0-alpha7"
        self.version = "1.0.0-alpha7"
        self.manifest_path = self.root / "release" / "template.json"
        self.notes_path = self.root / "docs" / "releases" / "template.md"
        self.changelog_path = self.root / "CHANGELOG.md"
        self.manifest = {
            "schemaVersion": 1,
            "version": "1.0.0-dev",
            "releaseDate": "Unreleased",
            "channel": "development",
            "sourceBranch": "main",
            "tag": None,
            "longTermSupport": False,
            "compatibilityBaseline": None,
            "releaseNotes": "docs/releases/template.md",
            "summary": "Summary.",
            "changes": {section: [] for section in MODULE.REQUIRED_CHANGE_SECTIONS},
            "roadmapCompleted": [],
            "gates": sorted(MODULE.REQUIRED_GATES),
            "artifacts": sorted(MODULE.REQUIRED_ARTIFACTS),
        }
        (self.root / "Directory.Build.props").write_text(
            "<Project><Version>1.0.0-dev</Version></Project>", encoding="utf-8"
        )
        (self.root / "supply-chain-policy.json").write_text(
            json.dumps({"packageVersion": "1.0.0-dev", "supportedVersions": {"releaseTargets": []}}), encoding="utf-8"
        )
        (self.root / "release" / "current.json").write_text(
            json.dumps({"schemaVersion": 1, "manifest": "template.json"}), encoding="utf-8"
        )
        self.write_release("Unreleased")

    def write_release(self, release_date: str) -> None:
        self.manifest["releaseDate"] = release_date
        self.manifest_path.write_text(json.dumps(self.manifest), encoding="utf-8")
        self.notes_path.write_text(
            f"# Nao development release notes\n\n- Release date: {release_date}\n",
            encoding="utf-8",
        )
        self.changelog_path.write_text(
            MODULE.render_changelog(self.manifest), encoding="utf-8"
        )

    def snapshot(self) -> dict[str, bytes]:
        return {
            str(path.relative_to(self.root)): path.read_bytes()
            for path in self.root.rglob("*") if path.is_file()
        }

    def prepare(self) -> list[str]:
        return MODULE.prepare_publication(
            self.root, MODULE.resolve_manifest(self.root, None), self.tag
        )

    def test_unreleased_date_is_resolved_consistently_in_utc(self) -> None:
        with patch.object(MODULE, "datetime") as clock:
            clock.now.return_value = datetime(2026, 10, 30, tzinfo=timezone.utc)
            self.assertEqual([], self.prepare())
            clock.now.assert_called_once_with(timezone.utc)
        path = MODULE.resolve_manifest(self.root, None)
        manifest = MODULE.load_manifest(path)
        self.assertEqual("2026-10-30", manifest["releaseDate"])
        self.assertEqual(self.version, manifest["version"])
        self.assertEqual(self.tag, manifest["tag"])
        self.assertEqual(1, manifest["schemaVersion"])
        notes_path = self.root / manifest["releaseNotes"]
        self.assertEqual(
            "2026-10-30", MODULE.release_date(notes_path.read_text(encoding="utf-8"))
        )
        self.assertEqual(
            MODULE.render_changelog(manifest),
            self.changelog_path.read_text(encoding="utf-8"),
        )
        self.assertEqual([], MODULE.validate_manifest(self.root, path))
        self.assertEqual("1.0.0-dev", MODULE.load_manifest(self.manifest_path)["version"])
        policy = json.loads((self.root / "supply-chain-policy.json").read_text())
        self.assertEqual(self.version, policy["packageVersion"])
        self.assertEqual([self.version], policy["supportedVersions"]["releaseTargets"])

    def test_explicit_date_and_reviewed_templates_are_preserved(self) -> None:
        self.write_release("2026-10-28")
        before = self.manifest_path.read_bytes(), self.notes_path.read_bytes()
        self.assertEqual([], self.prepare())
        self.assertEqual(before, (self.manifest_path.read_bytes(), self.notes_path.read_bytes()))
        prepared = MODULE.load_manifest(MODULE.resolve_manifest(self.root, None))
        self.assertEqual("2026-10-28", prepared["releaseDate"])

    def test_preparation_is_idempotent(self) -> None:
        self.assertEqual([], self.prepare())
        before = self.snapshot()
        with patch.object(MODULE, "datetime") as clock:
            self.assertEqual([], self.prepare())
            clock.now.assert_not_called()
        self.assertEqual(before, self.snapshot())

    def test_mismatched_notes_block_preparation_without_changes(self) -> None:
        self.notes_path.write_text(
            "# Nao development release notes\n\n- Release date: 2026-10-28\n",
            encoding="utf-8",
        )
        before = self.snapshot()
        errors = self.prepare()
        self.assertIn("release date differs between manifest and release notes", errors)
        self.assertEqual(before, self.snapshot())

    def test_stale_changelog_blocks_preparation_without_changes(self) -> None:
        self.changelog_path.write_text("Stale changelog\n", encoding="utf-8")
        before = self.snapshot()
        errors = self.prepare()
        self.assertIn("CHANGELOG.md is not the deterministic manifest rendering", errors)
        self.assertEqual(before, self.snapshot())

    def test_invalid_date_blocks_preparation_without_changes(self) -> None:
        self.write_release("2026-02-30")
        before = self.snapshot()
        errors = self.prepare()
        self.assertIn("releaseDate must be Unreleased or an ISO release date", errors)
        self.assertEqual(before, self.snapshot())

    def test_invalid_tag_cannot_change_release_files(self) -> None:
        self.tag = "v1.0.0-dev"
        before = self.snapshot()
        self.assertTrue(self.prepare())
        self.assertEqual(before, self.snapshot())

    def test_existing_historical_records_are_never_overwritten(self) -> None:
        for path in (
            self.root / "release" / f"{self.version}.json",
            self.root / "docs" / "releases" / f"{self.version}.md",
        ):
            with self.subTest(path=path):
                path.write_text("Historical record\n", encoding="utf-8")
                before = self.snapshot()
                self.assertTrue(any("already exist" in error for error in self.prepare()))
                self.assertEqual(before, self.snapshot())
                path.unlink()

    def write_draft(self) -> tuple[Path, Path]:
        manifest_path = self.root / "release" / f"{self.version}.json"
        notes_path = self.root / "docs" / "releases" / f"{self.version}.md"
        draft = dict(self.manifest)
        draft.update(
            version=self.version,
            tag=self.tag,
            channel="alpha",
            releaseNotes=f"docs/releases/{self.version}.md",
            summary="Reviewed version-specific draft.",
        )
        manifest_path.write_text(json.dumps(draft), encoding="utf-8")
        notes_path.write_text(
            f"# Nao {self.version} release notes\n\n- Release date: Unreleased\n\n"
            "Reviewed version-specific content.\n",
            encoding="utf-8",
        )
        return manifest_path, notes_path

    def test_existing_unreleased_draft_is_finalized_without_using_template_content(self) -> None:
        manifest_path, notes_path = self.write_draft()
        templates = self.manifest_path.read_bytes(), self.notes_path.read_bytes()
        with patch.object(MODULE, "datetime") as clock:
            clock.now.return_value = datetime(2026, 11, 5, tzinfo=timezone.utc)
            self.assertEqual([], self.prepare())
        prepared = MODULE.load_manifest(manifest_path)
        self.assertEqual("Reviewed version-specific draft.", prepared["summary"])
        self.assertEqual("2026-11-05", prepared["releaseDate"])
        self.assertEqual(1, prepared["schemaVersion"])
        self.assertEqual(
            f"# Nao {self.version} release notes\n\n- Release date: 2026-11-05\n\n"
            "Reviewed version-specific content.\n",
            notes_path.read_text(encoding="utf-8"),
        )
        self.assertEqual(templates, (self.manifest_path.read_bytes(), self.notes_path.read_bytes()))
        self.assertEqual([], MODULE.validate_manifest(self.root, manifest_path))
        before = self.snapshot()
        self.assertEqual([], self.prepare())
        self.assertEqual(before, self.snapshot())

    def test_dated_or_invalid_drafts_are_never_changed(self) -> None:
        for field, value in (
            ("schemaVersion", 2),
            ("releaseDate", "2026-10-28"),
            ("version", "1.0.0-alpha6"),
            ("tag", "v1.0.0-alpha6"),
            ("channel", "stable"),
            ("releaseNotes", "docs/releases/template.md"),
            ("gates", []),
            ("artifacts", []),
            ("roadmapCompleted", ["FND-01"]),
        ):
            with self.subTest(field=field):
                manifest_path, notes_path = self.write_draft()
                draft = MODULE.load_manifest(manifest_path)
                draft[field] = value
                manifest_path.write_text(json.dumps(draft), encoding="utf-8")
                if field == "releaseDate":
                    notes_path.write_text(
                        f"# Nao {self.version} release notes\n\n- Release date: {value}\n",
                        encoding="utf-8",
                    )
                before = self.snapshot()
                self.assertTrue(self.prepare())
                self.assertEqual(before, self.snapshot())

    def test_mismatched_draft_notes_are_never_changed(self) -> None:
        for notes in (
            "# Nao other release notes\n\n- Release date: Unreleased\n",
            f"# Nao {self.version} release notes\n\n- Release date: 2026-10-28\n",
        ):
            with self.subTest(notes=notes):
                _, notes_path = self.write_draft()
                notes_path.write_text(notes, encoding="utf-8")
                before = self.snapshot()
                self.assertTrue(self.prepare())
                self.assertEqual(before, self.snapshot())

    def test_prepared_release_cannot_be_reused_for_another_tag(self) -> None:
        self.assertEqual([], self.prepare())
        self.tag = "v9.0.0"
        before = self.snapshot()
        self.assertTrue(any("differs" in error for error in self.prepare()))
        self.assertEqual(before, self.snapshot())


class RepositoryTemplateTests(unittest.TestCase):
    def copy_development_checkout(self, root: Path) -> None:
        repository = SCRIPT.parents[1]
        for name in ("release", "docs/releases"):
            (root / name).mkdir(parents=True)
        for name in ("release/template.json", "docs/releases/template.md"):
            shutil.copy2(repository / name, root / name)
        shutil.copytree(repository / "docs" / "roadmap", root / "docs" / "roadmap")
        for name in ("Directory.Build.props", "supply-chain-policy.json", "CHANGELOG.md"):
            shutil.copy2(repository / name, root / name)
        # CI runs these tests after preparing publication metadata in its checkout.
        (root / "release" / "current.json").write_text(
            json.dumps({"schemaVersion": 1, "manifest": "template.json"}), encoding="utf-8"
        )
        supply_path = root / "supply-chain-policy.json"
        supply = json.loads(supply_path.read_text(encoding="utf-8"))
        supply["packageVersion"] = MODULE.DEVELOPMENT_VERSION
        supply["supportedVersions"]["releaseTargets"] = []
        supply_path.write_text(json.dumps(supply), encoding="utf-8")
        template = MODULE.load_manifest(root / "release" / "template.json")
        (root / "CHANGELOG.md").write_text(MODULE.render_changelog(template), encoding="utf-8")

    def test_next_tag_uses_reviewed_templates_without_source_version_edits(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.copy_development_checkout(root)
            props = (root / "Directory.Build.props").read_bytes()
            template = (root / "release" / "template.json").read_bytes()
            errors = MODULE.prepare_publication(
                root, MODULE.resolve_manifest(root, None), "v9.8.7-alpha99"
            )
            self.assertEqual([], errors)
            prepared = MODULE.resolve_manifest(root, None)
            self.assertEqual([], MODULE.validate_manifest(root, prepared))
            self.assertEqual("9.8.7-alpha99", MODULE.load_manifest(prepared)["version"])
            self.assertEqual(props, (root / "Directory.Build.props").read_bytes())
            self.assertEqual(template, (root / "release" / "template.json").read_bytes())

    def test_existing_alpha2_draft_can_be_published(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.copy_development_checkout(root)
            draft_path = root / "release" / "1.0.0-alpha2.json"
            repository = SCRIPT.parents[1]
            draft = MODULE.load_manifest(repository / "release" / draft_path.name)
            notes_path = root / str(draft["releaseNotes"])
            notes = (repository / str(draft["releaseNotes"])).read_text(encoding="utf-8")
            notes_path.write_text(
                notes.replace(f"- Release date: {draft['releaseDate']}", "- Release date: Unreleased", 1),
                encoding="utf-8",
            )
            draft["releaseDate"] = "Unreleased"
            draft_path.write_text(json.dumps(draft), encoding="utf-8")
            props = (root / "Directory.Build.props").read_bytes()
            template = (root / "release" / "template.json").read_bytes()
            self.assertEqual(
                [],
                MODULE.prepare_publication(root, MODULE.resolve_manifest(root, None), "v1.0.0-alpha2"),
            )
            prepared = MODULE.resolve_manifest(root, None)
            self.assertEqual(draft_path, prepared)
            self.assertEqual([], MODULE.validate_manifest(root, prepared))
            finalized = MODULE.load_manifest(prepared)
            self.assertNotEqual("Unreleased", finalized["releaseDate"])
            draft["releaseDate"] = finalized["releaseDate"]
            self.assertEqual(draft, finalized)
            self.assertEqual(props, (root / "Directory.Build.props").read_bytes())
            self.assertEqual(template, (root / "release" / "template.json").read_bytes())


if __name__ == "__main__":
    unittest.main()
