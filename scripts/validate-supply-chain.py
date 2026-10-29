#!/usr/bin/env python3
"""Validate Nao supply-chain policy and optional scanner evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

ACTION_REFERENCE = re.compile(r"^\s*uses:\s*[^@\s]+@([^\s#]+)")
IMMUTABLE_COMMIT = re.compile(r"^[0-9a-f]{40}$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")


def load_json(path: Path) -> object:
    with path.open(encoding="utf-8") as stream:
        return json.load(stream)


def current_release_version(root: Path) -> str:
    pointer = load_json(root / "release" / "current.json")
    if not isinstance(pointer, dict) or pointer.get("schemaVersion") != 1:
        raise ValueError("release/current.json is invalid")
    manifest_name = pointer.get("manifest")
    if not isinstance(manifest_name, str) or Path(manifest_name).name != manifest_name:
        raise ValueError("release/current.json manifest is invalid")
    manifest = load_json(root / "release" / manifest_name)
    if not isinstance(manifest, dict) or not isinstance(manifest.get("version"), str):
        raise ValueError("current release manifest is invalid")
    return manifest["version"]


def validate_publish_workflow(publish: str) -> list[str]:
    errors: list[str] = []
    if "tags: ['v*']" not in publish or "scripts/release.py version" not in publish:
        errors.append("NuGet publication must derive package metadata from the release manifest")
    if (
        "NuGet/login@" not in publish
        or "user: ${{ secrets.NUGET_USERNAME }}" not in publish
    ):
        errors.append("NuGet publication must use the configured Trusted Publishing identity")
    if "id-token: write" not in publish:
        errors.append("NuGet Trusted Publishing requires id-token write permission")
    if "steps.login.outputs.NUGET_API_KEY" not in publish:
        errors.append("NuGet publication must push with the Trusted Publishing credential")
    if "NuGet/login@" in publish and "- name: Build" in publish:
        if publish.index("NuGet/login@") > publish.index("- name: Build"):
            errors.append("NuGet Trusted Publishing must fail before the expensive build")
    for forbidden in (
        "dotnet nuget sign",
        "NUGET_SIGNING_CERTIFICATE",
        "--skip-duplicate",
    ):
        if forbidden in publish:
            errors.append(f"NuGet publication retains unsupported release dependency: {forbidden}")
    return errors


def validate_policy(root: Path) -> list[str]:
    errors: list[str] = []
    policy_path = root / "supply-chain-policy.json"

    if not policy_path.exists():
        return ["missing supply-chain-policy.json"]

    policy = load_json(policy_path)
    if not isinstance(policy, dict):
        return ["supply-chain policy must be a JSON object"]

    if policy.get("schemaVersion") != 1:
        errors.append("supply-chain policy schemaVersion must be 1")

    try:
        release_version = current_release_version(root)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        return [str(error)]

    if policy.get("packageVersion") != release_version:
        errors.append("supply-chain package version must match the current release manifest")

    lock = policy.get("dependencyLock", {})
    lock_path = root / str(lock.get("path", ""))
    expected_digest = lock.get("sha256")

    if not lock_path.is_file():
        errors.append("dependency lock path is missing")
    elif not isinstance(expected_digest, str) or not SHA256.fullmatch(expected_digest):
        errors.append("dependency lock digest must be lowercase SHA-256")
    else:
        actual_digest = hashlib.sha256(lock_path.read_bytes()).hexdigest()
        if actual_digest != expected_digest:
            errors.append(
                f"dependency lock digest changed: expected {expected_digest}, got {actual_digest}"
            )

    versions = policy.get("supportedVersions", {})
    if versions.get("projectReleased") is not False:
        errors.append("supported-version policy must identify Nao as unreleased")
    if versions.get("releaseTargets") != [release_version]:
        errors.append("supported release targets must contain only the current release")
    if not str(versions.get("policy", "")).strip():
        errors.append("supported-version policy text is required")

    response = policy.get("vulnerabilityResponse", {})
    for severity in ("critical", "high", "medium", "low"):
        entry = response.get(severity, {})
        if entry.get("releaseAction") not in {"block", "review", "track"}:
            errors.append(f"{severity} vulnerability release action is invalid")
        if not isinstance(entry.get("triageHours"), int) or entry["triageHours"] <= 0:
            errors.append(f"{severity} vulnerability triageHours must be positive")
        if not isinstance(entry.get("remediationHours"), int) or entry["remediationHours"] <= 0:
            errors.append(f"{severity} vulnerability remediationHours must be positive")

    if response.get("critical", {}).get("releaseAction") != "block":
        errors.append("critical vulnerabilities must block release")
    if response.get("high", {}).get("releaseAction") != "block":
        errors.append("high vulnerabilities must block release")

    licenses = policy.get("licenses", {})
    allowed = set(licenses.get("allowed", []))
    denied = set(licenses.get("denied", []))
    if not allowed:
        errors.append("license allowlist cannot be empty")
    if not denied:
        errors.append("license denylist cannot be empty")
    if allowed & denied:
        errors.append("license allowlist and denylist overlap")

    native = policy.get("nativeDependencies", [])
    required_native = {"libc", "Z3", "PostgreSQL"}
    discovered_native = {
        item.get("name")
        for item in native
        if isinstance(item, dict)
        and all(
            str(item.get(field, "")).strip()
            for field in ("name", "kind", "purpose", "distribution", "review")
        )
        and isinstance(item.get("consumers"), list)
        and item["consumers"]
    }
    missing_native = required_native - discovered_native
    if missing_native:
        errors.append(f"native dependency inventory is incomplete: {sorted(missing_native)}")

    tool_manifest = load_json(root / "dotnet-tools.json")
    for name, tool in tool_manifest.get("tools", {}).items():
        version = str(tool.get("version", ""))
        if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", version):
            errors.append(f"dotnet tool {name} is not pinned to an exact version")
        if tool.get("rollForward") is not False:
            errors.append(f"dotnet tool {name} must disable roll-forward")

    workflow_dir = root / ".github" / "workflows"
    for workflow in sorted(workflow_dir.glob("*.yml")):
        for line_number, line in enumerate(
            workflow.read_text(encoding="utf-8").splitlines(), start=1
        ):
            match = ACTION_REFERENCE.match(line)
            if match and not IMMUTABLE_COMMIT.fullmatch(match.group(1)):
                errors.append(
                    f"{workflow.relative_to(root)}:{line_number} action is not pinned to a commit"
                )

    for workflow_name in ("publish.yml", "container-images.yml"):
        content = (workflow_dir / workflow_name).read_text(encoding="utf-8")
        if "environment:" not in content:
            errors.append(f"{workflow_name} must use a protected release environment")
        if "attest-" not in content:
            errors.append(f"{workflow_name} must attest published artifacts")

    publish = (workflow_dir / "publish.yml").read_text(encoding="utf-8")
    errors.extend(validate_publish_workflow(publish))
    containers = (workflow_dir / "container-images.yml").read_text(encoding="utf-8")
    if "tags: ['v*']" not in containers or "scripts/release.py version" not in containers:
        errors.append("container publication must validate its tag against the release manifest")
    if "sbom" not in publish.lower():
        errors.append("NuGet publication must generate an SBOM")
    for required in (
        "TestCategory=Compatibility",
        "TestCategory=Migration",
        "TestCategory=EvalFast",
        "TestCategory=FailClosed",
        "TestCategory=Performance",
        "verify-packages",
        "test-release-consumer.sh",
        "checksums",
        "*.snupkg",
    ):
        if required not in publish:
            errors.append(f"NuGet publication is missing release control: {required}")

    if "anchore/scan-action" not in containers:
        errors.append("container publication must run a vulnerability scan")

    return errors


def vulnerabilities(value: object) -> list[dict[str, object]]:
    found: list[dict[str, object]] = []
    if isinstance(value, dict):
        for key, nested in value.items():
            if key.lower() == "vulnerabilities" and isinstance(nested, list):
                found.extend(item for item in nested if isinstance(item, dict))
            else:
                found.extend(vulnerabilities(nested))
    elif isinstance(value, list):
        for nested in value:
            found.extend(vulnerabilities(nested))
    return found


def validate_vulnerability_reports(paths: list[Path]) -> list[str]:
    errors: list[str] = []
    for path in paths:
        report = load_json(path)
        if isinstance(report, dict):
            for problem in report.get("problems", []):
                if str(problem.get("level", "")).lower() == "error":
                    errors.append(
                        f"{path}: scanner error for {problem.get('project', 'unknown project')}: "
                        f"{problem.get('text', 'unknown error')}"
                    )
        for vulnerability in vulnerabilities(report):
            severity = str(vulnerability.get("severity", "unknown")).lower()
            advisory = (
                vulnerability.get("advisoryurl")
                or vulnerability.get("advisoryUrl")
                or vulnerability.get("url")
                or "unknown advisory"
            )
            if severity in {"critical", "high"}:
                errors.append(f"{path}: release-blocking {severity} vulnerability: {advisory}")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--root", type=Path, default=Path(__file__).resolve().parents[1]
    )
    parser.add_argument(
        "--vulnerability-report", action="append", type=Path, default=[]
    )
    args = parser.parse_args()

    errors = validate_policy(args.root.resolve())
    errors.extend(validate_vulnerability_reports(args.vulnerability_report))

    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    print(
        f"Validated supply-chain policy and {len(args.vulnerability_report)} vulnerability reports."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
