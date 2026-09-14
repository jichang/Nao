#!/usr/bin/env python3
"""Reject permissive test fixtures and implicit security defaults in production code."""

from __future__ import annotations

import sys
from pathlib import Path

TEST_FIXTURES = {
    "AgentContext.unrestrictedForTests",
    "EtclovgConfig.Default",
    "HarnessServices.none",
}

TEST_FIXTURE_FILE = Path("tests/Nao.Testing/src/NaoTestFixtures.fs")


def validate(root: Path) -> list[str]:
    errors: list[str] = []

    fixture_source = (root / TEST_FIXTURE_FILE).read_text(encoding="utf-8-sig")
    for fixture in sorted(TEST_FIXTURES):
        member = fixture.split(".", 1)[1]
        if member not in fixture_source:
            errors.append(f"{TEST_FIXTURE_FILE} is missing test fixture {fixture}")

    for path in sorted((root / "src").rglob("*.fs")):
        relative = path.relative_to(root)
        source = path.read_text(encoding="utf-8-sig")
        for fixture in sorted(TEST_FIXTURES):
            if fixture in source:
                errors.append(f"production source uses permissive test fixture {fixture}: {relative}")

    evaluation = (root / "src/Nao.Eval/src/Running/EvalRunner.fs").read_text(
        encoding="utf-8-sig"
    )
    if "let create authorization createAgentContext harness sandbox" not in evaluation:
        errors.append("EvalExecutionConfig.create must require explicit harness and sandbox configuration")

    return errors


def main() -> int:
    root = Path(__file__).resolve().parent.parent
    errors = validate(root)

    if errors:
        for error in errors:
            print(f"error: {error}", file=sys.stderr)
        return 1

    print("Validated that permissive fixtures exist only in test support.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
