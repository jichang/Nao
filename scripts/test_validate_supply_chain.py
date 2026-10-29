import json
import tempfile
import unittest
from pathlib import Path

import importlib.util


MODULE_PATH = Path(__file__).with_name("validate-supply-chain.py")
SPEC = importlib.util.spec_from_file_location("validate_supply_chain", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class VulnerabilityReportTests(unittest.TestCase):
    def write_report(self, vulnerabilities):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "report.json"
        path.write_text(
            json.dumps(
                {
                    "projects": [
                        {
                            "frameworks": [
                                {
                                    "topLevelPackages": [
                                        {
                                            "id": "Example",
                                            "vulnerabilities": vulnerabilities,
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ),
            encoding="utf-8",
        )
        return path

    def test_high_vulnerability_blocks_release(self):
        report = self.write_report(
            [{"severity": "High", "advisoryurl": "https://example.invalid/CVE-1"}]
        )
        errors = MODULE.validate_vulnerability_reports([report])
        self.assertEqual(1, len(errors))
        self.assertIn("release-blocking high", errors[0])

    def test_medium_vulnerability_does_not_block_release(self):
        report = self.write_report(
            [{"severity": "Moderate", "advisoryurl": "https://example.invalid/CVE-2"}]
        )
        self.assertEqual([], MODULE.validate_vulnerability_reports([report]))


if __name__ == "__main__":
    unittest.main()
