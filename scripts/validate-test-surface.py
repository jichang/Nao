#!/usr/bin/env python3
"""Validate that Nao.slnx declares every supported source and test project."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path
from xml.etree import ElementTree

TEST_CATEGORIES = {
    Path("tests/Nao.Agents.Tests/Nao.Agents.Tests.fsproj"): "unit",
    Path("tests/Nao.E2E.Tests/Nao.E2E.Tests.fsproj"): "end-to-end",
    Path("tests/Nao.Eval.Tests/Nao.Eval.Tests.fsproj"): "evaluation",
    Path("tests/Nao.Persistence.Tests/Nao.Persistence.Tests.fsproj"): "integration",
    Path("tests/Nao.Protocols.Tests/Nao.Protocols.Tests.fsproj"): "unit",
    Path("tests/Nao.Providers.Tests/Nao.Providers.Tests.fsproj"): "integration",
    Path("tests/Nao.Runtime.Orleans.Tests/Nao.Runtime.Orleans.Tests.fsproj"): "integration",
}


def project_paths(root: Path, directory: str) -> set[Path]:
    return {
        path.relative_to(root)
        for extension in ("*.fsproj", "*.csproj")
        for path in (root / directory).rglob(extension)
    }


def solution_projects(root: Path) -> set[Path]:
    solution = ElementTree.parse(root / "Nao.slnx")
    return {
        Path(element.attrib["Path"])
        for element in solution.iter("Project")
        if "Path" in element.attrib
    }


def validate(root: Path) -> list[str]:
    discovered = project_paths(root, "src") | project_paths(root, "tests")
    declared = solution_projects(root)
    errors: list[str] = []

    for project in sorted(discovered - declared):
        errors.append(f"project is not declared in Nao.slnx: {project}")

    for project in sorted(declared - discovered):
        errors.append(f"Nao.slnx references a missing project: {project}")

    supported_tests = project_paths(root, "tests") & declared
    for project in sorted(supported_tests - TEST_CATEGORIES.keys()):
        errors.append(f"supported test project has no category: {project}")

    for project in sorted(TEST_CATEGORIES.keys() - supported_tests):
        errors.append(f"test category names an unsupported project: {project}")

    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--list-tests",
        action="store_true",
        help="print each supported test category and project path",
    )
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    errors = validate(root)

    if errors:
        for error in errors:
            print(f"error: {error}", file=sys.stderr)
        return 1

    if args.list_tests:
        for project, category in sorted(TEST_CATEGORIES.items()):
            print(f"{category}\t{project}")
        return 0

    declared_count = len(solution_projects(root))
    print(f"Validated {declared_count} solution projects with no exclusions.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
