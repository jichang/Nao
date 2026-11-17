import json
import re
import shutil
import tempfile
import unittest
from pathlib import Path

import importlib.util


MODULE_PATH = Path(__file__).with_name("validate-supply-chain.py")
SPEC = importlib.util.spec_from_file_location("validate_supply_chain", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class SdkLockTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        repository = MODULE_PATH.parents[1]
        for name in (".github/workflows", "deploy/images"):
            shutil.copytree(repository / name, self.root / name)
        (self.root / "scripts").mkdir()
        self.consumer = self.root / "scripts/test-release-consumer.sh"
        shutil.copyfile(repository / "scripts/test-release-consumer.sh", self.consumer)
        self.lock = self.root / "global.json"
        shutil.copyfile(repository / "global.json", self.lock)

    def update_sdk(self, **changes):
        configuration = json.loads(self.lock.read_text())
        configuration["sdk"].update(changes)
        self.lock.write_text(json.dumps(configuration), encoding="utf-8")

    def test_repository_has_one_consistent_sdk_lock(self):
        self.assertEqual([], MODULE.validate_sdk(MODULE_PATH.parents[1]))
        self.assertEqual([], MODULE.validate_sdk(self.root))

    def test_missing_or_malformed_lock_is_rejected(self):
        self.lock.unlink()
        self.assertIn("cannot read SDK lock", MODULE.validate_sdk(self.root)[0])
        for value in ("{", "[]", '{"sdk": null}'):
            with self.subTest(value=value):
                self.lock.write_text(value, encoding="utf-8")
                self.assertTrue(MODULE.validate_sdk(self.root))

    def test_sdk_baseline_must_allow_every_stable_feature_band(self):
        for version in ("10.0.x", "10.0", "10.0.101", "10.0.404", "11.0.100-preview.1", None, 10):
            with self.subTest(version=version):
                self.update_sdk(version=version)
                self.assertIn(
                    "global.json SDK version must start at major.minor.100 to allow every stable feature band",
                    MODULE.validate_sdk(self.root),
                )

    def test_roll_forward_must_allow_all_patches_but_not_another_major_minor(self):
        for policy in ("disable", "patch", "latestPatch", "feature", "minor", "latestMinor", "major", "latestMajor", None):
            with self.subTest(policy=policy):
                self.update_sdk(rollForward=policy)
                self.assertIn(
                    "global.json SDK rollForward must be latestFeature",
                    MODULE.validate_sdk(self.root),
                )
        self.update_sdk(rollForward="latestFeature", allowPrerelease=True)
        self.assertIn(
            "global.json SDK allowPrerelease must be false",
            MODULE.validate_sdk(self.root),
        )

    def test_nested_sdk_locks_are_rejected_for_all_solution_trees(self):
        for directory in ("src", "tests", "examples"):
            path = self.root / directory / "Project" / "global.json"
            path.parent.mkdir(parents=True)
            shutil.copyfile(self.lock, path)
            with self.subTest(directory=directory):
                self.assertIn(
                    f"{directory}/Project/global.json overrides the root SDK lock",
                    MODULE.validate_sdk(self.root),
                )

    def test_every_workflow_must_install_the_supported_major_minor(self):
        for workflow in sorted((self.root / ".github/workflows").glob("*.yml")):
            original = workflow.read_text()
            if "uses: actions/setup-dotnet@" not in original:
                continue
            for selection in ("10.1.x", "11.0.x", "10.0.404"):
                with self.subTest(workflow=workflow.name, selection=selection):
                    workflow.write_text(
                        original.replace("dotnet-version: '10.0.x'", f"dotnet-version: '{selection}'"),
                        encoding="utf-8",
                    )
                    self.assertTrue(any("SDK setup must install only 10.0.x" in error for error in MODULE.validate_sdk(self.root)))
            workflow.write_text(original, encoding="utf-8")

    def test_workflow_cannot_replace_the_family_selector_with_the_baseline(self):
        workflow = self.root / ".github/workflows/ci.yml"
        workflow.write_text(
            workflow.read_text().replace(
                "dotnet-version: '10.0.x'",
                "global-json-file: global.json",
            ),
            encoding="utf-8",
        )
        self.assertTrue(any("SDK setup must install only 10.0.x" in error for error in MODULE.validate_sdk(self.root)))

    def test_workflow_cannot_install_extra_sdks_alongside_the_family(self):
        workflow = self.root / ".github/workflows/ci.yml"
        original = workflow.read_text()
        for selection in (
            "dotnet-version: |\n            10.0.x\n            11.0.x",
            "dotnet-version: '10.0.x'\n          dotnet-version: '11.0.x'",
            "dotnet-version: '10.0.x'\n          dotnet-version-file: versions.txt",
        ):
            with self.subTest(selection=selection):
                workflow.write_text(original.replace("dotnet-version: '10.0.x'", selection), encoding="utf-8")
                self.assertTrue(any("SDK setup must install only 10.0.x" in error for error in MODULE.validate_sdk(self.root)))

    def test_worker_sdk_images_must_match_the_major_minor(self):
        for dockerfile in sorted((self.root / "deploy/images").glob("*.Dockerfile")):
            original = dockerfile.read_text()
            for version in ("10.1.100", "11.0.100", "10.0.100-preview.1"):
                with self.subTest(dockerfile=dockerfile.name, version=version):
                    dockerfile.write_text(re.sub(r"sdk:[^@\s]+@", f"sdk:{version}@", original), encoding="utf-8")
                    self.assertTrue(any("SDK image must match the global.json major.minor" in error for error in MODULE.validate_sdk(self.root)))
            dockerfile.write_text(original, encoding="utf-8")

    def test_worker_sdk_images_can_use_different_patches_and_feature_bands(self):
        for dockerfile in sorted((self.root / "deploy/images").glob("*.Dockerfile")):
            original = dockerfile.read_text()
            for version in ("10.0", "10.0.100", "10.0.102", "10.0.403", "10.0.404"):
                with self.subTest(dockerfile=dockerfile.name, version=version):
                    dockerfile.write_text(re.sub(r"sdk:[^@\s]+@", f"sdk:{version}@", original), encoding="utf-8")
                    self.assertEqual([], MODULE.validate_sdk(self.root))
            dockerfile.write_text(original, encoding="utf-8")

    def test_worker_sdk_images_cannot_drop_the_digest(self):
        dockerfile = self.root / "deploy/images/agents-worker.Dockerfile"
        text = dockerfile.read_text()
        first_line, remaining = text.split(" AS build", 1)
        dockerfile.write_text(first_line.split("@sha256:", 1)[0] + " AS build" + remaining, encoding="utf-8")
        self.assertTrue(any("SDK image must match the global.json major.minor" in error for error in MODULE.validate_sdk(self.root)))

    def test_release_consumer_cannot_escape_the_sdk_lock(self):
        original = self.consumer.read_text()
        for command in ('cp "$root/global.json" "$consumer/global.json"', 'cd "$consumer"'):
            with self.subTest(command=command):
                self.consumer.write_text(original.replace(command, ""), encoding="utf-8")
                self.assertIn(
                    "release consumer must copy and enter the root SDK lock before running dotnet",
                    MODULE.validate_sdk(self.root),
                )


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
  - run: |
      test "$GITHUB_REF_TYPE" = "tag"
      python3 scripts/release.py version --tag "$GITHUB_REF_NAME"
  - id: login
    uses: NuGet/login@commit
    with:
      user: ${{ secrets.NUGET_USERNAME }}
  - name: Build
    run: dotnet build Nao.slnx -c Release -p:Version=${{ steps.version.outputs.version }}
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
  - run: |
      test "$GITHUB_REF_TYPE" = "tag"
      python3 scripts/release.py version --tag "$GITHUB_REF_NAME"
  - uses: NuGet/login@commit
    with:
      user: ${{ secrets.NUGET_USERNAME }}
  - name: Build
    run: dotnet build Nao.slnx -c Release -p:Version=${{ steps.version.outputs.version }}
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
  - run: |
      test "$GITHUB_REF_TYPE" = "tag"
      python3 scripts/release.py version --tag "$GITHUB_REF_NAME"
  - id: login
    uses: NuGet/login@commit
    with:
      user: ${{ secrets.NUGET_USERNAME }}
  - name: Build
    run: dotnet build Nao.slnx -c Release -p:Version=${{ steps.version.outputs.version }}
  - run: echo ${{ steps.login.outputs.NUGET_API_KEY }}
"""
        errors = MODULE.validate_publish_workflow(workflow)
        self.assertTrue(any("attestations write permission" in error for error in errors))

    def test_branch_publication_guard_cannot_be_removed(self):
        workflow = (MODULE_PATH.parents[1] / ".github/workflows/publish.yml").read_text()
        workflow = workflow.replace('test "$GITHUB_REF_TYPE" = "tag"', "")
        errors = MODULE.validate_publish_workflow(workflow)
        self.assertTrue(any("from a Git tag" in error for error in errors))

    def test_release_build_cannot_use_the_development_default(self):
        workflow = (MODULE_PATH.parents[1] / ".github/workflows/publish.yml").read_text()
        workflow = workflow.replace(
            "dotnet build Nao.slnx -c Release -p:Version=${{ steps.version.outputs.version }}",
            "dotnet build Nao.slnx -c Release",
        )
        errors = MODULE.validate_publish_workflow(workflow)
        self.assertTrue(any("build assemblies with the tag-derived" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
