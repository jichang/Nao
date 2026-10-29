from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("validate-integrations.py")
SPEC = importlib.util.spec_from_file_location("validate_integrations", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class IntegrationPolicyTests(unittest.TestCase):
    def test_repository_policy_passes(self) -> None:
        root = Path(__file__).resolve().parents[1]
        self.assertEqual([], MODULE.validate(root))

    def test_missing_policy_fails(self) -> None:
        root = Path(tempfile.mkdtemp())
        self.assertEqual(["missing integration-policy.json"], MODULE.validate(root))

    def test_non_object_policy_fails(self) -> None:
        root = Path(tempfile.mkdtemp())
        (root / "integration-policy.json").write_text(json.dumps([]), encoding="utf-8")
        self.assertEqual(
            ["integration policy must be a JSON object"],
            MODULE.validate(root),
        )


if __name__ == "__main__":
    unittest.main()
