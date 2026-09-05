#!/usr/bin/env python3
"""Validate Nao source documentation and optionally generated HTML pages."""

from __future__ import annotations

import argparse
import re
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit

MARKDOWN_LINK = re.compile(r"\[[^]]*\]\(([^)]+)\)")
HEADING = re.compile(r"^(#{1,6})\s+(.+)$")
TASK_ID = re.compile(r"\b[A-Z]{3}-\d{2}\b")
EXTERNAL_SCHEMES = {"http", "https", "mailto", "data", "javascript"}
REQUIRED_ARCHITECTURE_SECTIONS = {
    "## Platform vocabulary",
    "## Data ownership and lifetime",
    "## Trust levels",
    "## Error categories and retryability",
}
REQUIRED_VOCABULARY_TERMS = {
    "Agent",
    "Orchestrator",
    "Harness",
    "Tool",
    "Provider",
    "Workspace",
    "Session",
    "Turn",
    "Execution",
}
REQUIRED_TEMPLATE_SECTIONS = {
    "docs/decisions/TEMPLATE.md": {
        "## Context",
        "## Decision",
        "## Options considered",
        "## Consequences",
        "## Compatibility and migration",
        "## Security and trust",
        "## Validation",
    },
    "docs/migrations/TEMPLATE.md": {
        "## Scope",
        "## Breaking changes",
        "## Before upgrade",
        "## Migration",
        "## Validation",
        "## Rollback",
    },
    "docs/releases/TEMPLATE.md": {
        "## Summary",
        "## Added",
        "## Changed",
        "## Fixed",
        "## Breaking changes",
        "## Security",
        "## Upgrade and rollback",
        "## Validation",
        "## Known limitations",
    },
}
REQUIRED_CODEOWNER_PATHS = {
    "/src/Nao.Agents/",
    "/src/Nao.Protocols/",
    "/src/Nao.Eval/",
    "/src/Nao.Persistence/",
    "/src/Nao.Persistence.Infrastructure/",
    "/src/Nao.Persistence.Memory/",
    "/src/Nao.Persistence.Observability/",
    "/src/Nao.Persistence.Feedback/",
    "/src/Nao.Providers/",
    "/src/Nao.Providers.OpenAICompatible/",
    "/src/Nao.Providers.Anthropic/",
    "/src/Nao.Providers.Ollama/",
    "/src/Nao.Runtime.Orleans/",
    "/src/Nao.Runtime.Orleans.Codegen/",
    "/docs/roadmap/00-foundations.md",
    "/docs/roadmap/01-harness-security-governance.md",
    "/docs/roadmap/02-knowledge-rag.md",
    "/docs/roadmap/03-evaluation-observability.md",
    "/docs/roadmap/04-providers-runtime.md",
    "/docs/roadmap/05-ontology-logic.md",
    "/docs/roadmap/06-platform-operations-dx.md",
}
REQUIRED_PR_CHECKS = {
    "owning roadmap task",
    "Updated roadmap checkboxes",
    "Added or updated an ADR",
    "Added a migration guide",
    "Updated release notes",
}


class HtmlLinks(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.links: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag != "a":
            return
        href = dict(attrs).get("href")
        if href:
            self.links.append(href)


def slugify_heading(value: str) -> str:
    value = re.sub(r"[`*_]", "", value.lower())
    value = re.sub(r"[^a-z0-9 -]", "", value)
    return re.sub(r"\s+", "-", value.strip())


def relative_target(source: Path, target: str) -> Path | None:
    parsed = urlsplit(target)
    if parsed.scheme in EXTERNAL_SCHEMES or parsed.netloc:
        return None
    path = unquote(parsed.path)
    if not path:
        return None
    return source.parent / path


def validate_markdown(root: Path) -> tuple[list[str], int, int]:
    files = [root / "README.md", *sorted((root / "docs").rglob("*.md"))]
    errors: list[str] = []
    task_ids: dict[str, list[str]] = {}
    link_count = 0

    for source in files:
        if not source.exists():
            errors.append(f"missing source document: {source.relative_to(root)}")
            continue

        seen_headings: dict[str, int] = {}
        for line_number, line in enumerate(source.read_text(encoding="utf-8").splitlines(), 1):
            location = f"{source.relative_to(root)}:{line_number}"
            if line.rstrip() != line:
                errors.append(f"{location}: trailing whitespace")

            heading = HEADING.match(line)
            if heading:
                slug = slugify_heading(heading.group(2))
                if slug in seen_headings:
                    errors.append(
                        f"{location}: duplicate heading #{slug} "
                        f"(first at line {seen_headings[slug]})"
                    )
                seen_headings[slug] = line_number
                if heading.group(1) == "##":
                    for task_id in TASK_ID.findall(heading.group(2)):
                        task_ids.setdefault(task_id, []).append(location)

            for raw_target in MARKDOWN_LINK.findall(line):
                link_count += 1
                target = relative_target(source, raw_target)
                if target is None:
                    continue
                if target.suffix == ".html":
                    if "reference" in target.parts:
                        continue
                    target = target.with_suffix(".md")
                if not target.exists():
                    errors.append(f"{location}: missing link target {raw_target}")

    for task_id, locations in task_ids.items():
        if len(locations) > 1:
            errors.append(f"duplicate roadmap task {task_id}: {', '.join(locations)}")

    for roadmap_page in sorted((root / "docs" / "roadmap").glob("*.md")):
        if "[Back to roadmap](../roadmap.md)" not in roadmap_page.read_text(encoding="utf-8"):
            errors.append(f"{roadmap_page.relative_to(root)}: missing roadmap backlink")

    architecture = root / "docs" / "architecture.md"
    architecture_lines = architecture.read_text(encoding="utf-8").splitlines()
    architecture_sections = set(architecture_lines) & REQUIRED_ARCHITECTURE_SECTIONS
    for section in sorted(REQUIRED_ARCHITECTURE_SECTIONS - architecture_sections):
        errors.append(f"docs/architecture.md: missing required section {section}")

    vocabulary_terms = {
        columns[1].strip()
        for line in architecture_lines
        if line.startswith("|")
        for columns in [line.split("|")]
        if len(columns) > 2
    }
    for term in sorted(REQUIRED_VOCABULARY_TERMS - vocabulary_terms):
        errors.append(f"docs/architecture.md: missing platform vocabulary term {term}")

    return errors, len(files), link_count


def validate_documentation_process(root: Path) -> list[str]:
    errors: list[str] = []

    for relative_path, required_sections in REQUIRED_TEMPLATE_SECTIONS.items():
        template = root / relative_path
        if not template.exists():
            errors.append(f"missing documentation template: {relative_path}")
            continue
        lines = set(template.read_text(encoding="utf-8").splitlines())
        for section in sorted(required_sections - lines):
            errors.append(f"{relative_path}: missing required section {section}")

    decisions = root / "docs" / "decisions"
    if decisions.is_dir():
        for decision in sorted(decisions.glob("[0-9][0-9][0-9][0-9]-*.md")):
            text = decision.read_text(encoding="utf-8")
            for field in ("Status", "Date", "Owners", "Roadmap"):
                if not re.search(rf"^- {field}: \S.+$", text, re.MULTILINE):
                    errors.append(
                        f"{decision.relative_to(root)}: missing non-empty ADR field {field}"
                    )

    codeowners = root / ".github" / "CODEOWNERS"
    if not codeowners.exists():
        errors.append("missing ownership metadata: .github/CODEOWNERS")
    else:
        owned_paths = {
            line.split()[0]
            for line in codeowners.read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.lstrip().startswith("#")
        }
        for path in sorted(REQUIRED_CODEOWNER_PATHS - owned_paths):
            errors.append(f".github/CODEOWNERS: missing required ownership path {path}")

    pull_request_template = root / ".github" / "pull_request_template.md"
    if not pull_request_template.exists():
        errors.append("missing review checklist: .github/pull_request_template.md")
    else:
        text = pull_request_template.read_text(encoding="utf-8")
        for check in sorted(REQUIRED_PR_CHECKS):
            if check not in text:
                errors.append(
                    f".github/pull_request_template.md: missing required check {check}"
                )

    docs_workflow = root / ".github" / "workflows" / "docs.yml"
    if not docs_workflow.exists():
        errors.append("missing documentation workflow: .github/workflows/docs.yml")
    else:
        workflow = docs_workflow.read_text(encoding="utf-8")
        for command in (
            "python3 scripts/validate-docs.py",
            "dotnet fsdocs build",
            "python3 scripts/validate-docs.py --site ./site",
        ):
            if command not in workflow:
                errors.append(f".github/workflows/docs.yml: missing required command {command}")

    return errors


def validate_generated_site(site: Path) -> list[str]:
    errors: list[str] = []
    if not site.is_dir():
        return [f"generated site does not exist: {site}"]

    for source in sorted(site.rglob("*.html")):
        parser = HtmlLinks()
        parser.feed(source.read_text(encoding="utf-8", errors="replace"))
        for raw_target in parser.links:
            parsed = urlsplit(raw_target)
            if parsed.scheme in EXTERNAL_SCHEMES or parsed.netloc or not parsed.path:
                continue

            path = unquote(parsed.path)
            if path.startswith("/"):
                # FSharp.Formatting receives a configurable deployment root.
                # Absolute site links cannot be resolved without that deployment prefix.
                continue

            target = (source.parent / path).resolve()
            if not target.exists():
                errors.append(
                    f"{source.relative_to(site)}: missing generated link target {raw_target}"
                )

    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parent.parent,
        help="Nao repository root",
    )
    parser.add_argument(
        "--site",
        type=Path,
        help="Optional generated FSharp.Formatting output directory",
    )
    args = parser.parse_args()

    root = args.root.resolve()
    errors, file_count, link_count = validate_markdown(root)
    errors.extend(validate_documentation_process(root))
    if args.site:
        errors.extend(validate_generated_site(args.site.resolve()))

    if errors:
        print("Documentation validation failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    generated = f" and generated site {args.site}" if args.site else ""
    print(f"Validated {file_count} source documents and {link_count} links{generated}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
