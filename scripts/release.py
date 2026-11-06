#!/usr/bin/env python3
"""Validate and render Nao's reviewed release manifest."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import zipfile
from datetime import date, datetime, timezone
from pathlib import Path
from xml.etree import ElementTree

VERSION = re.compile(
    r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?:-((?:0|[1-9]\d*|[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9]\d*|[A-Za-z-][0-9A-Za-z-]*))*))?$"
)
DEVELOPMENT_VERSION = "1.0.0-dev"
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

def release_version(tag: str) -> str:
    if not tag.startswith("v"):
        raise ValueError("release tag must be v<version>")
    version = tag[1:]
    match = VERSION.fullmatch(version)
    if match is None:
        raise ValueError("release tag must contain canonical SemVer without build metadata")
    prerelease = match.group(4)
    if prerelease and re.fullmatch(r"(alpha|beta|rc)(?:[.-]?(?:0|[1-9]\d*))?", prerelease) is None:
        raise ValueError("release tag must use a stable, alpha, beta, or rc version; development versions cannot be published")
    return version


def release_channel(version: str) -> str:
    match = VERSION.fullmatch(version)
    assert match is not None
    prerelease = match.group(4)
    return re.match(r"[a-z]+", prerelease).group() if prerelease else "stable"


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
        or (target != "template.json" and not VERSION.fullmatch(target_path.stem))
    ):
        raise ValueError("release/current.json manifest must be template.json or a versioned JSON filename")
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
        f"## [{'Unreleased' if manifest['version'] == DEVELOPMENT_VERSION else manifest['version']}] - {manifest['releaseDate']}",
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


def validate_release_record(root: Path, path: Path) -> list[str]:
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
    if manifest.get("releaseDate") != "Unreleased":
        try:
            date.fromisoformat(str(manifest.get("releaseDate")))
        except ValueError:
            errors.append("releaseDate must be Unreleased or an ISO release date")
    development = version == DEVELOPMENT_VERSION
    if path.name != ("template.json" if development else f"{version}.json"):
        errors.append("release manifest filename must match its version")
    if manifest.get("tag") != (None if development else f"v{version}"):
        errors.append("release tag must be v<version>")
    if manifest.get("sourceBranch") != "main":
        errors.append("releases must originate from main")
    if development:
        if manifest.get("channel") != "development":
            errors.append("the release template channel must be development")
    else:
        try:
            release_version(f"v{version}")
        except ValueError as error:
            errors.append(str(error))
        else:
            if manifest.get("channel") != release_channel(version):
                errors.append(f"release channel differs from version {version}")
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
        notes_version = "development" if development else version
        if not notes.startswith(f"# Nao {notes_version} release notes\n"):
            errors.append("release notes title does not match the manifest version")
        if release_date(notes) != manifest.get("releaseDate"):
            errors.append("release date differs between manifest and release notes")

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

    return errors


def validate_manifest(root: Path, path: Path) -> list[str]:
    errors = validate_release_record(root, path)
    if errors:
        return errors

    manifest = load_manifest(path)
    props = ElementTree.parse(root / "Directory.Build.props")
    if props.findtext(".//Version") != DEVELOPMENT_VERSION:
        errors.append("Directory.Build.props must default to 1.0.0-dev, not a release version")

    supply = json.loads((root / "supply-chain-policy.json").read_text(encoding="utf-8"))
    if supply.get("packageVersion") != manifest["version"]:
        errors.append("supply-chain package version differs from the release manifest")

    changelog = root / "CHANGELOG.md"
    expected = render_changelog(manifest)
    if not changelog.is_file() or changelog.read_text(encoding="utf-8") != expected:
        errors.append("CHANGELOG.md is not the deterministic manifest rendering")
    return errors


def prepare_publication(root: Path, manifest_path: Path, tag: str) -> list[str]:
    try:
        version = release_version(tag)
    except ValueError as error:
        return [str(error)]
    errors = validate_manifest(root, manifest_path)
    if errors:
        return errors

    manifest = load_manifest(manifest_path)
    if manifest["version"] != DEVELOPMENT_VERSION:
        if manifest["version"] != version:
            return ["prepared release version differs from the Git tag"]
        if manifest["releaseDate"] == "Unreleased":
            return ["prepared release requires an ISO release date"]
        return []

    target_manifest = root / "release" / f"{version}.json"
    target_notes = root / "docs" / "releases" / f"{version}.md"
    if target_manifest.exists() or target_notes.exists():
        if not target_manifest.is_file() or not target_notes.is_file():
            return ["release records already exist; finalizing a draft requires both its manifest and notes"]
        errors = validate_release_record(root, target_manifest)
        if errors:
            return [f"existing release draft: {error}" for error in errors]
        manifest = load_manifest(target_manifest)
        if manifest["releaseDate"] != "Unreleased":
            return ["release records already exist; dated manifests and notes must not be overwritten"]
        if manifest["releaseNotes"] != f"docs/releases/{version}.md":
            return ["existing release draft must reference its matching versioned notes"]

    publication_date = manifest["releaseDate"]
    if publication_date == "Unreleased":
        publication_date = datetime.now(timezone.utc).date().isoformat()
    notes = (root / str(manifest["releaseNotes"])).read_text(encoding="utf-8")
    notes = notes.replace("# Nao development release notes\n", f"# Nao {version} release notes\n", 1)
    notes = notes.replace(f"- Release date: {manifest['releaseDate']}", f"- Release date: {publication_date}", 1)
    manifest["version"] = version
    manifest["tag"] = tag
    manifest["channel"] = release_channel(version)
    manifest["releaseDate"] = publication_date
    manifest["releaseNotes"] = f"docs/releases/{version}.md"
    supply_path = root / "supply-chain-policy.json"
    supply = json.loads(supply_path.read_text(encoding="utf-8"))
    supply["packageVersion"] = version
    supply["supportedVersions"]["releaseTargets"] = [version]
    target_manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    target_notes.write_text(notes, encoding="utf-8")
    supply_path.write_text(json.dumps(supply, indent=2) + "\n", encoding="utf-8")
    (root / "release" / "current.json").write_text(
        json.dumps({"schemaVersion": 1, "manifest": target_manifest.name}, indent=2) + "\n",
        encoding="utf-8")
    (root / "CHANGELOG.md").write_text(render_changelog(manifest), encoding="utf-8")
    print(f"Prepared {tag} dated {publication_date} in the working checkout.")
    return []


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
            if f"/blob/v{version}/docs/releases/template.md" not in nuspec:
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
    version_parser = subparsers.add_parser("version")
    version_parser.add_argument("--tag", help="derive a publishable version from a Git tag")
    prepare = subparsers.add_parser(
        "prepare-publication",
        help="prepare tagged release records from reviewed templates or matching Unreleased drafts",
    )
    prepare.add_argument("--tag", required=True)
    verify = subparsers.add_parser("verify-packages")
    verify.add_argument("--directory", type=Path, required=True)
    checksums = subparsers.add_parser("checksums")
    checksums.add_argument("--directory", type=Path, required=True)
    args = parser.parse_args()

    root = args.root.resolve()
    if args.command == "version" and args.tag is not None:
        try:
            print(release_version(args.tag))
        except ValueError as error:
            print(f"error: {error}", file=sys.stderr)
            return 1
        return 0
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
    if args.command == "prepare-publication":
        errors = prepare_publication(root, manifest_path, args.tag)
    elif args.command == "verify-packages":
        try:
            release_version(str(manifest["tag"]))
        except ValueError as error:
            errors = [str(error)]
        else:
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
