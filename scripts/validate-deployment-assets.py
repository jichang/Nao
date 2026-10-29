#!/usr/bin/env python3
"""Validate fail-closed image and Kubernetes deployment assets."""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

SHA256_IMAGE = re.compile(r"^[^\s\"']+@sha256:[a-f0-9]{64}$")


def validate_dockerfile(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8")
    errors: list[str] = []
    from_lines = [line.split(maxsplit=1)[1].split(" AS ", 1)[0] for line in text.splitlines() if line.startswith("FROM ")]

    if len(from_lines) != 2 or any(not SHA256_IMAGE.fullmatch(image) for image in from_lines):
        errors.append(f"{path}: every build and runtime base image must use an immutable SHA-256 digest")

    required = [
        "USER 65532:65532",
        "COPY --from=build --chown=65532:65532",
        "STOPSIGNAL SIGTERM",
        'ENTRYPOINT ["dotnet",',
    ]

    for marker in required:
        if marker not in text:
            errors.append(f"{path}: missing hardened image marker: {marker}")

    return errors


def validate_chart(root: Path) -> list[str]:
    templates = "\n".join(
        path.read_text(encoding="utf-8")
        for path in sorted((root / "templates").glob("*"))
        if path.is_file()
    )
    values = (root / "values.yaml").read_text(encoding="utf-8")
    schema = (root / "values.schema.json").read_text(encoding="utf-8")
    errors: list[str] = []

    markers = [
        "kind: Deployment",
        "kind: Service",
        "kind: PodDisruptionBudget",
        "kind: HorizontalPodAutoscaler",
        "kind: NetworkPolicy",
        "kind: Job",
        "startupProbe:",
        "readinessProbe:",
        "livenessProbe:",
        "terminationGracePeriodSeconds:",
        "topologySpreadConstraints:",
        "readOnlyRootFilesystem: true",
        "allowPrivilegeEscalation: false",
        'drop: ["ALL"]',
        "runAsNonRoot: true",
        "automountServiceAccountToken: false",
        "maxUnavailable: 0",
        "capacity.providerQuotaSafeReplicas",
        "capacity.storageSafeReplicas",
    ]

    for marker in markers:
        if marker not in templates:
            errors.append(f"{root}: missing orchestration safety marker: {marker}")

    if "repository: \"\"" not in values or "digest: \"\"" not in values:
        errors.append(f"{root}: host images must be explicit inputs without mutable defaults")

    if "^$|^sha256:[a-f0-9]{64}$" not in schema:
        errors.append(f"{root}: values schema must reject non-digest image identifiers")

    return errors


def validate_rendered(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8")
    errors: list[str] = []

    for line in text.splitlines():
        stripped = line.strip()

        if stripped.startswith("image:"):
            image = stripped.removeprefix("image:").strip().strip('"')

            if not SHA256_IMAGE.fullmatch(image):
                errors.append(f"{path}: rendered workload uses a mutable image reference: {image}")

    for kind in (
        "Deployment",
        "Service",
        "ConfigMap",
        "ServiceAccount",
        "PodDisruptionBudget",
        "HorizontalPodAutoscaler",
        "NetworkPolicy",
    ):
        if f"kind: {kind}" not in text:
            errors.append(f"{path}: rendered chart is missing {kind}")

    return errors


def validate(root: Path, rendered: Path | None) -> list[str]:
    errors = []

    for name in ("agents-worker.Dockerfile", "documents-worker.Dockerfile"):
        errors.extend(validate_dockerfile(root / "deploy" / "images" / name))

    errors.extend(validate_chart(root / "deploy" / "helm" / "nao"))

    if rendered is not None:
        errors.extend(validate_rendered(rendered))

    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rendered", type=Path)
    arguments = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    errors = validate(root, arguments.rendered)

    if errors:
        print("\n".join(f"error: {error}" for error in errors), file=sys.stderr)
        return 1

    print("Validated immutable images and fail-closed orchestration assets.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
