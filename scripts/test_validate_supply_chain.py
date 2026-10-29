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
    def write_report(self, vulnerabilities, *, problems=None):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "report.json"
        path.write_text(
            json.dumps(
                {
                    "problems": problems if problems is not None else [],
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

    def test_scanner_error_blocks_release_without_vulnerabilities(self):
        report = self.write_report(
            [],
            problems=[
                {
                    "project": "Example.fsproj",
                    "level": "error",
                    "text": "No assets file was found. Run restore before running this command.",
                }
            ],
        )
        errors = MODULE.validate_vulnerability_reports([report])
        self.assertEqual(1, len(errors))
        self.assertIn("scanner error for Example.fsproj", errors[0])
        self.assertIn("No assets file was found", errors[0])

    def test_scanner_warning_does_not_block_release(self):
        report = self.write_report(
            [],
            problems=[
                {"project": "Example.fsproj", "level": "warning", "text": "Example warning"}
            ],
        )
        self.assertEqual([], MODULE.validate_vulnerability_reports([report]))

    def test_clean_report_does_not_block_release(self):
        self.assertEqual([], MODULE.validate_vulnerability_reports([self.write_report([])]))

    def test_current_release_version_follows_pointer(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        release = root / "release"
        release.mkdir()
        (release / "current.json").write_text(
            json.dumps(
                {"schemaVersion": 1, "manifest": "1.0.0-alpha1.json"}
            ),
            encoding="utf-8",
        )
        (release / "1.0.0-alpha1.json").write_text(
            json.dumps({"version": "1.0.0-alpha1"}),
            encoding="utf-8",
        )
        self.assertEqual(
            "1.0.0-alpha1", MODULE.current_release_version(root)
        )

    def test_trusted_publishing_contract_accepts_configured_identity_preflight(self):
        workflow = """
permissions:
  id-token: write
  attestations: write
on:
  push:
    tags: ['v*']
steps:
  - run: python3 scripts/release.py version
  - id: login
    uses: NuGet/login@commit
    with:
      user: ${{ secrets.NUGET_USERNAME }}
  - name: Build
  - run: dotnet nuget push package.nupkg --api-key ${{ steps.login.outputs.NUGET_API_KEY }}
"""
        self.assertEqual([], MODULE.validate_publish_workflow(workflow))

    def test_unsupported_signing_secrets_and_duplicate_skips_are_rejected(self):
        workflow = """
permissions:
  id-token: write
  attestations: write
on:
  push:
    tags: ['v*']
steps:
  - run: python3 scripts/release.py version
  - uses: NuGet/login@commit
    with:
      user: ${{ secrets.NUGET_USERNAME }}
  - name: Build
  - run: dotnet nuget sign package.nupkg --skip-duplicate
    env:
      NUGET_SIGNING_CERTIFICATE_BASE64: value
  - run: echo ${{ steps.login.outputs.NUGET_API_KEY }}
"""
        errors = MODULE.validate_publish_workflow(workflow)
        self.assertTrue(any("dotnet nuget sign" in error for error in errors))
        self.assertTrue(any("NUGET_SIGNING_CERTIFICATE" in error for error in errors))
        self.assertTrue(any("--skip-duplicate" in error for error in errors))

    def test_missing_attestation_permission_is_rejected(self):
        workflow = """
permissions:
  id-token: write
on:
  push:
    tags: ['v*']
steps:
  - run: python3 scripts/release.py version
  - id: login
    uses: NuGet/login@commit
    with:
      user: ${{ secrets.NUGET_USERNAME }}
  - name: Build
  - run: echo ${{ steps.login.outputs.NUGET_API_KEY }}
"""
        errors = MODULE.validate_publish_workflow(workflow)
        self.assertTrue(any("attestations write permission" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
