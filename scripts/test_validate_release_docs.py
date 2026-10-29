from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("validate-release-docs.py")
SPEC = importlib.util.spec_from_file_location("validate_release_docs", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


def release(
    breaking: str = "None",
    release_date: str = "Unreleased",
    version: str = "1.0.0",
) -> str:
    sections = {
        "Summary": "First supported package set.",
        "Added": "Initial capabilities.",
        "Changed": "None",
        "Fixed": "None",
        "Breaking changes": breaking,
        "Security": "Initial trust boundaries.",
        "Upgrade and rollback": "Fresh installation only.",
        "Validation": "Build and tests passed.",
        "Supply-chain evidence": "Recorded by the protected workflow.",
        "Known limitations": "See the roadmap.",
    }
    bodies = "\n\n".join(f"## {name}\n\n{body}" for name, body in sections.items())
    return (
        f"# Nao {version} release notes\n\n"
        f"- Release date: {release_date}\n"
        "- Milestone: R4\n"
        "- Owners: @owner\n\n"
        f"{bodies}\n"
    )


class ReleaseDocumentationValidationTests(unittest.TestCase):
    def root(self) -> Path:
        temporary = Path(tempfile.mkdtemp())
        (temporary / "docs" / "releases").mkdir(parents=True)
        (temporary / "docs" / "migrations").mkdir(parents=True)
        return temporary

    def test_breaking_release_requires_migration_link(self) -> None:
        root = self.root()
        path = root / "docs" / "releases" / "1.0.0.md"
        path.write_text(release("Changed the durable state."), encoding="utf-8")
        errors = MODULE.validate(root, "1.0.0", False)
        self.assertTrue(any("require links to migration guides" in error for error in errors))

    def test_migration_link_and_final_date_pass_publication(self) -> None:
        root = self.root()
        migration = root / "docs" / "migrations" / "state.md"
        migration.write_text("# State migration\n", encoding="utf-8")
        path = root / "docs" / "releases" / "1.0.0.md"
        path.write_text(
            release("[State migration](../migrations/state.md)", "2026-10-29"),
            encoding="utf-8",
        )
        self.assertEqual([], MODULE.validate(root, "1.0.0", True))

    def test_unreleased_date_blocks_publication(self) -> None:
        root = self.root()
        path = root / "docs" / "releases" / "1.0.0.md"
        path.write_text(release(), encoding="utf-8")
        errors = MODULE.validate(root, "1.0.0", True)
        self.assertTrue(any("ISO release date" in error for error in errors))

    def test_prerelease_version_document_is_discovered(self) -> None:
        root = self.root()
        version = "1.0.0-alpha1"
        path = root / "docs" / "releases" / f"{version}.md"
        path.write_text(release(version=version), encoding="utf-8")
        self.assertEqual([], MODULE.validate(root, version, False))


if __name__ == "__main__":
    unittest.main()
