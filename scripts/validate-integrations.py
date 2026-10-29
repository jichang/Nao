#!/usr/bin/env python3
"""Validate selected integrations, support tiers, owners, and evidence."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from xml.etree import ElementTree

REQUIRED_CRITERIA = {
    "userDemand",
    "maintenanceHealth",
    "licensing",
    "deployment",
    "conformance",
}
REQUIRED_TIERS = {"production", "optional", "host-boundary"}
REQUIRED_CAPABILITIES = {
    "vector",
    "lexical",
    "graph",
    "telemetry",
    "identity",
    "secret",
    "reasoner-datalog",
    "reasoner-constraints",
}
REASONING_CAPABILITIES = {"reasoner-datalog", "reasoner-constraints"}


def load(path: Path) -> object:
    with path.open(encoding="utf-8") as stream:
        return json.load(stream)


def project_property(path: Path, name: str) -> str | None:
    return ElementTree.parse(path).findtext(f".//{name}")


def validate(root: Path) -> list[str]:
    errors: list[str] = []
    policy_path = root / "integration-policy.json"
    if not policy_path.is_file():
        return ["missing integration-policy.json"]

    policy = load(policy_path)
    if not isinstance(policy, dict):
        return ["integration policy must be a JSON object"]
    if policy.get("schemaVersion") != 1:
        errors.append("integration policy schemaVersion must be 1")

    criteria = policy.get("selectionCriteria")
    if not isinstance(criteria, dict) or set(criteria) != REQUIRED_CRITERIA:
        errors.append("selectionCriteria must define the required criteria exactly")
    elif any(not isinstance(value, str) or not value.strip() for value in criteria.values()):
        errors.append("selectionCriteria descriptions must be non-blank")

    tiers = policy.get("supportTiers")
    if not isinstance(tiers, dict) or set(tiers) != REQUIRED_TIERS:
        errors.append("supportTiers must define production, optional, and host-boundary")

    integrations = policy.get("integrations")
    if not isinstance(integrations, list):
        return errors + ["integrations must be a list"]

    ids: set[str] = set()
    capabilities: set[str] = set()
    codeowners = (root / ".github" / "CODEOWNERS").read_text(encoding="utf-8")

    for integration in integrations:
        if not isinstance(integration, dict):
            errors.append("every integration must be an object")
            continue
        identifier = integration.get("id")
        if not isinstance(identifier, str) or not identifier:
            errors.append("every integration requires an id")
            continue
        if identifier in ids:
            errors.append(f"duplicate integration id: {identifier}")
        ids.add(identifier)

        capability = integration.get("capability")
        if capability not in REQUIRED_CAPABILITIES:
            errors.append(f"{identifier}: unsupported capability {capability}")
        else:
            capabilities.add(str(capability))

        tier = integration.get("supportTier")
        if tier not in REQUIRED_TIERS:
            errors.append(f"{identifier}: invalid support tier")
        delivery = integration.get("delivery")
        if delivery not in {"package", "host-guidance"}:
            errors.append(f"{identifier}: invalid delivery mode")
        if tier == "host-boundary" and delivery != "host-guidance":
            errors.append(f"{identifier}: host-boundary integration requires host guidance")

        selection = integration.get("selection")
        if not isinstance(selection, dict) or set(selection) != REQUIRED_CRITERIA:
            errors.append(f"{identifier}: selection evidence is incomplete")
        elif any(not isinstance(value, str) or not value.strip() for value in selection.values()):
            errors.append(f"{identifier}: selection evidence contains a blank value")

        project_value = integration.get("project")
        project = root / str(project_value)
        if not project.is_file():
            errors.append(f"{identifier}: project does not exist: {project_value}")
        else:
            package = integration.get("package")
            if package != project.stem:
                errors.append(f"{identifier}: package must match the project name")
            if project_property(project, "IsPackable") == "false":
                errors.append(f"{identifier}: selected integration project is not packable")
            ownership = f"/{project.parent.as_posix().removeprefix(root.as_posix() + '/')}/"
            owner = integration.get("owner")
            if f"{ownership} {owner}" not in codeowners:
                errors.append(f"{identifier}: CODEOWNERS does not assign {owner} to {ownership}")

        evidence = integration.get("evidence")
        if not isinstance(evidence, dict) or set(evidence) != {"tests", "docs", "operations"}:
            errors.append(f"{identifier}: evidence must contain tests, docs, and operations")
        else:
            for kind, paths in evidence.items():
                if not isinstance(paths, list) or not paths:
                    errors.append(f"{identifier}: {kind} evidence cannot be empty")
                    continue
                for relative in paths:
                    if not isinstance(relative, str) or not (root / relative).is_file():
                        errors.append(f"{identifier}: missing {kind} evidence: {relative}")

        use_cases = integration.get("validatedDomainUseCases")
        if not isinstance(use_cases, list) or not use_cases or any(
            not isinstance(value, str) or not value.strip() for value in use_cases
        ):
            errors.append(f"{identifier}: validated domain use cases are required")

        if capability == "graph" and not str(integration.get("graphJustification", "")).strip():
            errors.append(f"{identifier}: graph integration requires explicit justification")
        if capability in REASONING_CAPABILITIES:
            if tier != "optional":
                errors.append(f"{identifier}: formal reasoners must remain optional")
            if integration.get("generalPurposeReasoning") is not False:
                errors.append(f"{identifier}: formal reasoners cannot claim general-purpose use")

    if capabilities != REQUIRED_CAPABILITIES:
        errors.append(
            f"integration capabilities differ: expected {sorted(REQUIRED_CAPABILITIES)}, got {sorted(capabilities)}"
        )

    workflows = [
        root / ".github" / "workflows" / "ci.yml",
        root / ".github" / "workflows" / "publish.yml",
    ]
    image = "pgvector/pgvector@sha256:ac08538c6f8b9904c33c8224c5e5706dbe760aca29db1d096972b4052c22a75d"
    for workflow in workflows:
        content = workflow.read_text(encoding="utf-8")
        for required in (
            image,
            "NAO_TEST_POSTGRES:",
            'TestCategory=ExternalDatabase',
            "scripts/validate-integrations.py",
        ):
            if required not in content:
                errors.append(
                    f"{workflow.relative_to(root)}: missing integration qualification control {required}"
                )
    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    errors = validate(args.root.resolve())
    if errors:
        for error in errors:
            print(f"error: {error}", file=sys.stderr)
        return 1
    policy = load(args.root.resolve() / "integration-policy.json")
    assert isinstance(policy, dict)
    print(f"Validated {len(policy['integrations'])} selected integrations.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
