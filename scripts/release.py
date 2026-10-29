#!/usr/bin/env python3
"""Validate and render Nao's reviewed release manifest."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path
from xml.etree import ElementTree

VERSION = re.compile(
    r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?:-((?:0|[1-9]\d*|[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9]\d*|[A-Za-z-][0-9A-Za-z-]*))*))?$"
)
ROADMAP_HEADING = re.compile(r"^## ([A-Z]+-\d+)\b", re.MULTILINE)
CHECKBOX = re.compile(r"^- \[([ x])\]", re.MULTILINE)
REQUIRED_CHANGE_SECTIONS = {"Added", "Changed", "Fixed", "Security"}
REQUIRED_GATES = {
    "compatibility",
    "migration",
    "evaluation",
    "security",
    "performance",
    "clean-consumer",
}
REQUIRED_ARTIFACTS = {
    "nuget-packages",
    "symbol-packages",
    "source-links",
    "sha256-checksums",
    "spdx-sbom",
    "build-provenance",
    "sbom-attestation",
}


def load_manifest(path: Path) -> dict[str, object]:
    with path.open(encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise ValueError("release manifest must be a JSON object")
    return value


def resolve_manifest(root: Path, requested: Path | None) -> Path:
    if requested is not None:
        return requested if requested.is_absolute() else root / requested

    pointer_path = root / "release" / "current.json"
    pointer = load_manifest(pointer_path)
    if set(pointer) != {"schemaVersion", "manifest"} or pointer["schemaVersion"] != 1:
        raise ValueError("release/current.json must contain schemaVersion and manifest")
    target = pointer["manifest"]
    if not isinstance(target, str):
        raise ValueError("release/current.json manifest must be a filename")
    target_path = Path(target)
    if (
        target_path.name != target
        or target_path.suffix != ".json"
        or not VERSION.fullmatch(target_path.stem)
    ):
        raise ValueError("release/current.json manifest must be a versioned JSON filename")
    manifest_path = pointer_path.parent / target_path
    if not manifest_path.is_file():
        raise ValueError(f"current release manifest does not exist: {target}")
    return manifest_path


def release_date(notes: str) -> str | None:
    match = re.search(r"^- Release date: (.+)$", notes, re.MULTILINE)
    return match.group(1) if match else None


def completed_roadmap(root: Path) -> set[str]:
    completed: set[str] = set()
    for path in sorted((root / "docs" / "roadmap").glob("[0-9][0-9]-*.md")):
        text = path.read_text(encoding="utf-8")
        headings = list(ROADMAP_HEADING.finditer(text))
        for index, heading in enumerate(headings):
            end = headings[index + 1].start() if index + 1 < len(headings) else len(text)
            boxes = CHECKBOX.findall(text[heading.end() : end])
            if boxes and all(value == "x" for value in boxes):
                completed.add(heading.group(1))
    return completed


def render_changelog(manifest: dict[str, object]) -> str:
    changes = manifest["changes"]
    assert isinstance(changes, dict)
    lines = [
        "# Changelog",
        "",
        "This file is generated from reviewed metadata under `release/`; do not edit it directly.",
        "",
        f"## [{manifest['version']}] - {manifest['releaseDate']}",
        "",
        str(manifest["summary"]),
        "",
    ]
    for section in ("Added", "Changed", "Fixed", "Security"):
        lines.extend([f"### {section}", ""])
        values = changes[section]
        assert isinstance(values, list)
        if values:
            lines.extend(f"- {value}" for value in values)
        else:
            lines.append("- None")
        lines.append("")
    lines.extend(
        [
            "### Completed roadmap capabilities",
            "",
            ", ".join(f"`{value}`" for value in manifest["roadmapCompleted"]),
            "",
            f"Full release evidence: [{manifest['releaseNotes']}]({manifest['releaseNotes']})",
            "",
        ]
    )
    return "\n".join(lines)


def validate_manifest(root: Path, path: Path) -> list[str]:
    errors: list[str] = []
    try:
        manifest = load_manifest(path)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        return [str(error)]

    if manifest.get("schemaVersion") != 1:
        errors.append("release manifest schemaVersion must be 1")

    version = manifest.get("version")
    match = VERSION.fullmatch(version) if isinstance(version, str) else None
    if match is None:
        errors.append("release version must be canonical SemVer without build metadata or a prefix")
        return errors
    if path.name != f"{version}.json":
        errors.append("release manifest filename must match its version")
    if manifest.get("tag") != f"v{version}":
        errors.append("release tag must be v<version>")
    if manifest.get("sourceBranch") != "main":
        errors.append("releases must originate from main")
    prerelease = match.group(4)
    channel_match = (
        re.fullmatch(r"(alpha|beta|rc)(?:[.-]?(?:0|[1-9]\d*))?", prerelease)
        if prerelease
        else None
    )
    expected_channel = channel_match.group(1) if channel_match else "stable"
    if prerelease and channel_match is None:
        errors.append("release prerelease must use an alpha, beta, or rc identifier")
    elif manifest.get("channel") != expected_channel:
        errors.append(f"release channel must be {expected_channel} for version {version}")
    if manifest.get("longTermSupport") is not False:
        errors.append("the initial release line must not claim long-term support")
    if manifest.get("compatibilityBaseline") is not None:
        errors.append("the first supported release cannot name a compatibility baseline")

    notes_value = manifest.get("releaseNotes")
    notes_path = root / str(notes_value)
    if not notes_path.is_file():
        errors.append(f"release notes do not exist: {notes_value}")
    else:
        notes = notes_path.read_text(encoding="utf-8")
        if not notes.startswith(f"# Nao {version} release notes\n"):
            errors.append("release notes title does not match the manifest version")
        if release_date(notes) != manifest.get("releaseDate"):
            errors.append("release date differs between manifest and release notes")

    props = (root / "Directory.Build.props").read_text(encoding="utf-8")
    if f"<Version>{version}</Version>" not in props:
        errors.append("Directory.Build.props version differs from the release manifest")

    supply = json.loads((root / "supply-chain-policy.json").read_text(encoding="utf-8"))
    if supply.get("packageVersion") != version:
        errors.append("supply-chain package version differs from the release manifest")

    changes = manifest.get("changes")
    if not isinstance(changes, dict) or set(changes) != REQUIRED_CHANGE_SECTIONS:
        errors.append("changes must contain exactly Added, Changed, Fixed, and Security")
    else:
        for section, values in changes.items():
            if not isinstance(values, list) or any(
                not isinstance(value, str) or not value.strip() for value in values
            ):
                errors.append(f"changes.{section} must be a list of non-blank strings")

    if set(manifest.get("gates", [])) != REQUIRED_GATES:
        errors.append("release gates do not match the required gate set")
    if set(manifest.get("artifacts", [])) != REQUIRED_ARTIFACTS:
        errors.append("release artifacts do not match the required artifact set")

    recorded = manifest.get("roadmapCompleted")
    if not isinstance(recorded, list) or len(recorded) != len(set(recorded)):
        errors.append("roadmapCompleted must be a duplicate-free list")
    else:
        actual = completed_roadmap(root)
        if set(recorded) != actual:
            missing = sorted(actual - set(recorded))
            stale = sorted(set(recorded) - actual)
            if missing:
                errors.append(f"completed roadmap capabilities are not recorded: {missing}")
            if stale:
                errors.append(f"manifest records incomplete roadmap capabilities: {stale}")

    changelog = root / "CHANGELOG.md"
    expected = render_changelog(manifest)
    if not changelog.is_file() or changelog.read_text(encoding="utf-8") != expected:
        errors.append("CHANGELOG.md is not the deterministic manifest rendering")
    return errors


def packable_projects(root: Path) -> set[str]:
    packages: set[str] = set()
    for path in sorted((root / "src").glob("*/*.*proj")):
        tree = ElementTree.parse(path)
        is_packable = tree.findtext(".//IsPackable")
        output_type = tree.findtext(".//OutputType")
        if is_packable != "false" and output_type != "Exe":
            packages.add(path.stem)
    return packages


def verify_packages(root: Path, directory: Path, version: str) -> list[str]:
    errors: list[str] = []
    expected = packable_projects(root)
    packages = {
        path.name[: -len(f".{version}.nupkg")]: path
        for path in directory.glob(f"*.{version}.nupkg")
        if not path.name.endswith(".symbols.nupkg")
    }
    if set(packages) != expected:
        errors.append(
            f"package set differs from packable projects: expected {sorted(expected)}, got {sorted(packages)}"
        )

    for package_id, package in sorted(packages.items()):
        symbol = directory / f"{package_id}.{version}.snupkg"
        if not symbol.is_file():
            errors.append(f"missing symbol package for {package_id}")
        else:
            with zipfile.ZipFile(symbol) as symbol_archive:
                pdb_names = [
                    name for name in symbol_archive.namelist() if name.endswith(".pdb")
                ]
                if not pdb_names:
                    errors.append(f"{symbol.name}: missing portable PDB")
                elif not any(
                    b"raw.githubusercontent.com/jichang/Nao/"
                    in symbol_archive.read(name)
                    for name in pdb_names
                ):
                    errors.append(f"{symbol.name}: missing GitHub source-link mapping")
        with zipfile.ZipFile(package) as archive:
            names = set(archive.namelist())
            nuspec_name = f"{package_id}.nuspec"
            if nuspec_name not in names:
                errors.append(f"{package.name}: missing {nuspec_name}")
                continue
            nuspec = archive.read(nuspec_name).decode("utf-8")
            if "<repository type=\"git\"" not in nuspec or "github.com/jichang/Nao" not in nuspec:
                errors.append(f"{package.name}: missing repository/source-link metadata")
            if f"<version>{version}</version>" not in nuspec:
                errors.append(f"{package.name}: nuspec version differs from the release")
            if "<readme>README.md</readme>" not in nuspec:
                errors.append(f"{package.name}: nuspec does not advertise the package README")
            if f"docs/releases/{version}.md" not in nuspec:
                errors.append(f"{package.name}: nuspec release-notes link differs from the release")
            if "README.md" not in names:
                errors.append(f"{package.name}: missing package README")
            if not any(name.startswith("lib/net10.0/") and name.endswith(".dll") for name in names):
                errors.append(f"{package.name}: missing net10.0 library")
    return errors


def write_checksums(directory: Path) -> None:
    artifacts = sorted(
        path
        for path in directory.iterdir()
        if path.is_file() and path.name != "SHA256SUMS"
    )
    content = "".join(
        f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n"
        for path in artifacts
    )
    (directory / "SHA256SUMS").write_text(content, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--manifest", type=Path)
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("validate")
    subparsers.add_parser("render")
    subparsers.add_parser("version")
    verify = subparsers.add_parser("verify-packages")
    verify.add_argument("--directory", type=Path, required=True)
    checksums = subparsers.add_parser("checksums")
    checksums.add_argument("--directory", type=Path, required=True)
    args = parser.parse_args()

    root = args.root.resolve()
    try:
        manifest_path = resolve_manifest(root, args.manifest)
        manifest = load_manifest(manifest_path)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    if args.command == "version":
        print(manifest["version"])
        return 0
    if args.command == "render":
        (root / "CHANGELOG.md").write_text(render_changelog(manifest), encoding="utf-8")
        return 0
    if args.command == "verify-packages":
        errors = verify_packages(root, args.directory.resolve(), str(manifest["version"]))
    elif args.command == "checksums":
        write_checksums(args.directory.resolve())
        return 0
    else:
        errors = validate_manifest(root, manifest_path)

    if errors:
        for error in errors:
            print(f"error: {error}", file=sys.stderr)
        return 1
    print(f"Validated release {manifest['version']} {args.command}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
