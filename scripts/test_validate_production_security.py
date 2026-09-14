import importlib.util
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("validate-production-security.py")
SPEC = importlib.util.spec_from_file_location("validate_production_security", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class ProductionSecurityValidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        fixture = self.root / "tests/Nao.Testing/src/NaoTestFixtures.fs"
        evaluation = self.root / "src/Nao.Eval/src/Running/EvalRunner.fs"
        fixture.parent.mkdir(parents=True)
        evaluation.parent.mkdir(parents=True)
        fixture.write_text(
            "AgentContext.unrestrictedForTests\n"
            "EtclovgConfig.Default\n"
            "HarnessServices.none\n",
            encoding="utf-8",
        )
        evaluation.write_text(
            "let create authorization createAgentContext harness sandbox = ()",
            encoding="utf-8",
        )

    def tearDown(self):
        self.temp.cleanup()

    def test_accepts_explicit_production_configuration(self):
        self.assertEqual([], MODULE.validate(self.root))

    def test_rejects_test_fixture_in_production_source(self):
        source = self.root / "src/Nao.Runtime.Orleans/Host.fs"
        source.parent.mkdir(parents=True)
        source.write_text("let config = EtclovgConfig.Default", encoding="utf-8")

        errors = MODULE.validate(self.root)

        self.assertTrue(any("EtclovgConfig.Default" in error for error in errors))

    def test_rejects_implicit_evaluation_configuration(self):
        evaluation = self.root / "src/Nao.Eval/src/Running/EvalRunner.fs"
        evaluation.write_text(
            "let create authorization createAgentContext = ()",
            encoding="utf-8",
        )

        errors = MODULE.validate(self.root)

        self.assertTrue(any("explicit harness and sandbox" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
