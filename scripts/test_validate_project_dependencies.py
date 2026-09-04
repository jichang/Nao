#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("validate-project-dependencies.py")
SPEC = importlib.util.spec_from_file_location("validate_project_dependencies", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


class DependencyPolicyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary_directory.name)

        for project in VALIDATOR.ALLOWED_PROJECT_REFERENCES:
            project_path = self.root / project
            project_path.parent.mkdir(parents=True, exist_ok=True)
            project_path.write_text("<Project />", encoding="utf-8")

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def test_accepts_allowed_windows_style_reference(self) -> None:
        project = Path("src/Nao.Agents/Nao.Agents.fsproj")
        (self.root / project).write_text(
            '<Project><ItemGroup><ProjectReference Include="..\\Nao.Protocols\\Nao.Protocols.fsproj" />'
            "</ItemGroup></Project>",
            encoding="utf-8",
        )

        self.assertEqual([], VALIDATOR.validate(self.root))

    def test_rejects_forbidden_project_direction(self) -> None:
        project = Path("src/Nao.Agents/Nao.Agents.fsproj")
        (self.root / project).write_text(
            '<Project><ItemGroup><ProjectReference Include="..\\Nao.Persistence\\Nao.Persistence.fsproj" />'
            "</ItemGroup></Project>",
            encoding="utf-8",
        )

        self.assertIn(
            "forbidden project dependency: "
            "src/Nao.Agents/Nao.Agents.fsproj -> src/Nao.Persistence/Nao.Persistence.fsproj",
            VALIDATOR.validate(self.root),
        )

    def test_rejects_vendor_package_in_core(self) -> None:
        references = self.root / "src/Nao.Protocols/paket.references"
        references.write_text("OpenAI\n", encoding="utf-8")

        self.assertIn(
            "forbidden core package dependency: src/Nao.Protocols/Nao.Protocols.fsproj -> OpenAI",
            VALIDATOR.validate(self.root),
        )


if __name__ == "__main__":
    unittest.main()