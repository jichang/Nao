#!/usr/bin/env python3
"""Validate versioned release notes and required migration-guide links."""

from __future__ import annotations

import argparse
import re
import sys
from datetime import date
from pathlib import Path
from urllib.parse import unquote, urlsplit

SECTION = re.compile(r"^## (.+)$", re.MULTILINE)
LINK = re.compile(r"\[[^]]+\]\(([^)]+)\)")
VERSION_FILE = re.compile(r"^\d+\.\d+\.\d+\.md$")
REQUIRED_SECTIONS = {
    "Summary",
    "Added",
    "Changed",
    "Fixed",
    "Breaking changes",
    "Security",
    "Upgrade and rollback",
    "Validation",
    "Supply-chain evidence",
    "Known limitations",
}


def section_body(text: str, section: str) -> str:
    match = re.search(
        rf"^## {re.escape(section)}\s*$\n(.*?)(?=^## |\Z)",
        text,
        re.MULTILINE | re.DOTALL,
    )
    return match.group(1).strip() if match else ""


def validate_release(root: Path, path: Path, publishing: bool) -> list[str]:
    errors: list[str] = []
    version = path.stem
    text = path.read_text(encoding="utf-8")
    location = path.relative_to(root)

    if not text.startswith(f"# Nao {version} release notes\n"):
        errors.append(f"{location}: title must identify version {version}")

    release_date = re.search(r"^- Release date: (.+)$", text, re.MULTILINE)
    if not release_date:
        errors.append(f"{location}: missing release date metadata")
    elif publishing:
        try:
            date.fromisoformat(release_date.group(1))
        except ValueError:
            errors.append(f"{location}: publishing requires an ISO release date")

    for field in ("Milestone", "Owners"):
        if not re.search(rf"^- {field}: \S.+$", text, re.MULTILINE):
            errors.append(f"{location}: missing non-empty {field} metadata")

    sections = set(SECTION.findall(text))
    for section in sorted(REQUIRED_SECTIONS - sections):
        errors.append(f"{location}: missing required section {section}")

    for section in REQUIRED_SECTIONS:
        body = section_body(text, section)
        if not body:
            errors.append(f"{location}: section {section} cannot be empty")
        if re.search(r"\b(Describe|List)\b", body):
            errors.append(f"{location}: section {section} still contains template text")

    breaking = section_body(text, "Breaking changes")
    if breaking != "None":
        migration_links = LINK.findall(breaking)
        if not migration_links:
            errors.append(
                f"{location}: breaking changes require links to migration guides"
            )
        for raw_target in migration_links:
            parsed = urlsplit(raw_target)
            target = (path.parent / unquote(parsed.path)).resolve()
            migrations = (root / "docs" / "migrations").resolve()
            if migrations not in target.parents or target.suffix != ".md":
                errors.append(
                    f"{location}: breaking-change link is not a migration guide: {raw_target}"
                )
            elif not target.exists():
                errors.append(
                    f"{location}: missing migration guide: {raw_target}"
                )

    return errors


def validate(root: Path, version: str | None, publishing: bool) -> list[str]:
    releases = root / "docs" / "releases"
    paths = sorted(
        path for path in releases.glob("*.md") if VERSION_FILE.match(path.name)
    )
    errors: list[str] = []

    if version:
        expected = releases / f"{version}.md"
        if expected not in paths:
            return [f"missing release notes for version {version}: {expected.relative_to(root)}"]
        paths = [expected]

    for path in paths:
        errors.extend(validate_release(root, path, publishing))

    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", help="validate one release version")
    parser.add_argument(
        "--publishing",
        action="store_true",
        help="require final publication metadata such as an ISO release date",
    )
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    errors = validate(root, args.version, args.publishing)

    if errors:
        for error in errors:
            print(f"error: {error}", file=sys.stderr)
        return 1

    scope = args.version or "all versioned releases"
    print(f"Validated release documentation for {scope}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
