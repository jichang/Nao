#!/usr/bin/env python3
"""Validate production project directions and core package isolation."""

from __future__ import annotations

import sys
from pathlib import Path
from xml.etree import ElementTree

PROJECT_EXTENSIONS = ("*.fsproj", "*.csproj")

ALLOWED_PROJECT_REFERENCES = {
    Path("src/Nao.Protocols/Nao.Protocols.fsproj"): set(),
    Path("src/Nao.Agents/Nao.Agents.fsproj"): {
        Path("src/Nao.Protocols/Nao.Protocols.fsproj"),
    },
    Path("src/Nao.Eval/Nao.Eval.fsproj"): {
        Path("src/Nao.Agents/Nao.Agents.fsproj"),
    },
    Path("src/Nao.Persistence/Nao.Persistence.fsproj"): {
        Path("src/Nao.Agents/Nao.Agents.fsproj"),
        Path("src/Nao.Persistence.Feedback/Nao.Persistence.Feedback.fsproj"),
        Path("src/Nao.Persistence.Infrastructure/Nao.Persistence.Infrastructure.fsproj"),
        Path("src/Nao.Persistence.Memory/Nao.Persistence.Memory.fsproj"),
        Path("src/Nao.Persistence.Observability/Nao.Persistence.Observability.fsproj"),
    },
    Path("src/Nao.Persistence.Feedback/Nao.Persistence.Feedback.fsproj"): {
        Path("src/Nao.Agents/Nao.Agents.fsproj"),
        Path("src/Nao.Persistence.Infrastructure/Nao.Persistence.Infrastructure.fsproj"),
    },
    Path("src/Nao.Persistence.Infrastructure/Nao.Persistence.Infrastructure.fsproj"): {
        Path("src/Nao.Agents/Nao.Agents.fsproj"),
    },
    Path("src/Nao.Persistence.Memory/Nao.Persistence.Memory.fsproj"): {
        Path("src/Nao.Agents/Nao.Agents.fsproj"),
        Path("src/Nao.Persistence.Infrastructure/Nao.Persistence.Infrastructure.fsproj"),
    },
    Path("src/Nao.Persistence.Observability/Nao.Persistence.Observability.fsproj"): {
        Path("src/Nao.Agents/Nao.Agents.fsproj"),
        Path("src/Nao.Persistence.Infrastructure/Nao.Persistence.Infrastructure.fsproj"),
    },
    Path("src/Nao.Providers/Nao.Providers.fsproj"): {
        Path("src/Nao.Agents/Nao.Agents.fsproj"),
        Path("src/Nao.Providers.Anthropic/Nao.Providers.Anthropic.fsproj"),
        Path("src/Nao.Providers.Ollama/Nao.Providers.Ollama.fsproj"),
        Path("src/Nao.Providers.OpenAICompatible/Nao.Providers.OpenAICompatible.fsproj"),
    },
    Path("src/Nao.Providers.Anthropic/Nao.Providers.Anthropic.fsproj"): {
        Path("src/Nao.Agents/Nao.Agents.fsproj"),
    },
    Path("src/Nao.Providers.Ollama/Nao.Providers.Ollama.fsproj"): {
        Path("src/Nao.Agents/Nao.Agents.fsproj"),
        Path("src/Nao.Providers.OpenAICompatible/Nao.Providers.OpenAICompatible.fsproj"),
    },
    Path("src/Nao.Providers.OpenAICompatible/Nao.Providers.OpenAICompatible.fsproj"): {
        Path("src/Nao.Agents/Nao.Agents.fsproj"),
    },
    Path("src/Nao.Runtime.Orleans/Nao.Runtime.Orleans.fsproj"): {
        Path("src/Nao.Agents/Nao.Agents.fsproj"),
    },
    Path("src/Nao.Runtime.Orleans.Codegen/Nao.Runtime.Orleans.Codegen.csproj"): {
        Path("src/Nao.Runtime.Orleans/Nao.Runtime.Orleans.fsproj"),
    },
}

CORE_PROJECTS = {
    Path("src/Nao.Protocols/Nao.Protocols.fsproj"),
    Path("src/Nao.Agents/Nao.Agents.fsproj"),
}

FORBIDDEN_CORE_PACKAGE_PREFIXES = (
    "anthropic",
    "azure.ai.openai",
    "google.cloud.aiplatform",
    "microsoft.data.sql",
    "microsoft.entityframeworkcore",
    "microsoft.extensions.ai.openai",
    "microsoft.orleans",
    "mongodb",
    "mysql",
    "npgsql",
    "openai",
    "oracle",
    "pinecone",
    "qdrant",
    "stackexchange.redis",
    "weaviate",
)


def production_projects(root: Path) -> set[Path]:
    return {
        path.relative_to(root)
        for extension in PROJECT_EXTENSIONS
        for path in (root / "src").rglob(extension)
    }


def project_references(root: Path, project: Path) -> set[Path]:
    project_path = root / project
    document = ElementTree.parse(project_path)
    references = set()

    for element in document.iter():
        if element.tag.rsplit("}", 1)[-1] != "ProjectReference":
            continue

        include = element.attrib.get("Include")
        if include:
            reference = (project_path.parent / include.replace("\\", "/")).resolve()
            references.add(reference.relative_to(root.resolve()))

    return references


def direct_packages(root: Path, project: Path) -> set[str]:
    project_path = root / project
    document = ElementTree.parse(project_path)
    packages = {
        element.attrib["Include"]
        for element in document.iter()
        if element.tag.rsplit("}", 1)[-1] == "PackageReference"
        and "Include" in element.attrib
    }

    paket_references = project_path.parent / "paket.references"
    if paket_references.exists():
        packages.update(
            line.split()[0]
            for line in paket_references.read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.lstrip().startswith(("#", "//"))
        )

    return packages


def validate(root: Path) -> list[str]:
    discovered = production_projects(root)
    errors: list[str] = []

    for project in sorted(discovered - ALLOWED_PROJECT_REFERENCES.keys()):
        errors.append(f"production project has no dependency policy: {project}")

    for project in sorted(ALLOWED_PROJECT_REFERENCES.keys() - discovered):
        errors.append(f"dependency policy names a missing project: {project}")

    for project in sorted(discovered & ALLOWED_PROJECT_REFERENCES.keys()):
        actual = project_references(root, project)
        allowed = ALLOWED_PROJECT_REFERENCES[project]

        for dependency in sorted(actual - allowed):
            errors.append(f"forbidden project dependency: {project} -> {dependency}")

    for project in sorted(discovered & CORE_PROJECTS):
        for package in sorted(direct_packages(root, project)):
            normalized = package.lower()
            if normalized.startswith(FORBIDDEN_CORE_PACKAGE_PREFIXES):
                errors.append(f"forbidden core package dependency: {project} -> {package}")

    return errors


def main() -> int:
    root = Path(__file__).resolve().parent.parent
    errors = validate(root)

    if errors:
        for error in errors:
            print(f"error: {error}", file=sys.stderr)
        return 1

    print(
        f"Validated dependency policy for {len(ALLOWED_PROJECT_REFERENCES)} "
        "production projects."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())