namespace Nao.Eval.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval

[<TestClass>]
type SafetyMetricsTests() =

    let constitutionResult =
        { Passed = true
          Violations = []
          Evaluations = [] }

    let evaluation caseId actual =
        { CaseId = caseId
          Actual = actual
          ConstitutionResult = constitutionResult }

    [<TestMethod>]
    member _.``Measurement reports false positive and false negative rates with revision provenance``() =
        let dataset = SafetyDatasets.adversarialMultilingualV1
        let cases = SafetyDataset.cases dataset

        let evaluations =
            cases
            |> List.map (fun case ->
                let expected = SafetyCase.expected case

                let actual =
                    match SafetyCase.id case with
                    | "en-benign-safety" -> SafetyLabel.Unsafe
                    | "en-override-harm" -> SafetyLabel.Safe
                    | _ -> expected

                evaluation (SafetyCase.id case) actual)

        let measurement = SafetyMetrics.measure dataset evaluations
        let overall = measurement.Overall

        Assert.AreEqual(SafetyDataset.name dataset, measurement.DatasetName)
        Assert.AreEqual(SafetyDataset.version dataset, measurement.DatasetVersion)
        Assert.AreEqual(SafetyDataset.contentSha256 dataset, measurement.DatasetContentSha256)
        Assert.AreEqual(12, overall.Total)
        Assert.AreEqual(5, overall.TruePositives)
        Assert.AreEqual(5, overall.TrueNegatives)
        Assert.AreEqual(1, overall.FalsePositives)
        Assert.AreEqual(1, overall.FalseNegatives)
        Assert.AreEqual(10.0 / 12.0, overall.Accuracy, 0.000001)
        Assert.AreEqual(1.0 / 6.0, overall.FalsePositiveRate.Value, 0.000001)
        Assert.AreEqual(1.0 / 6.0, overall.FalseNegativeRate.Value, 0.000001)

        Assert.AreEqual(3, measurement.ByLanguage["en"].Total)
        Assert.AreEqual(1, measurement.ByLanguage["en"].FalsePositives)
        Assert.AreEqual(1, measurement.ByLanguage["en"].FalseNegatives)
        Assert.AreEqual(4, measurement.ByTag["multilingual"].TruePositives)
        Assert.AreEqual(1, measurement.ByTechnique["benign-control"].FalsePositives)
        Assert.AreEqual(1, measurement.ByTechnique["instruction-override"].FalseNegatives)

    [<TestMethod>]
    member _.``Undefined class rates remain explicit``() =
        let safeCase =
            SafetyCase.create "safe" "en" AdversarialTechnique.BenignControl "input" "output" SafetyLabel.Safe

        let dataset = SafetyDataset.create "safe-only" "1" [ safeCase ]

        let measurement =
            SafetyMetrics.measure dataset [ evaluation "safe" SafetyLabel.Safe ]

        Assert.AreEqual(Some 0.0, measurement.Overall.FalsePositiveRate)
        Assert.AreEqual(None, measurement.Overall.FalseNegativeRate)

    [<TestMethod>]
    member _.``Measurement requires exact one-to-one dataset coverage``() =
        let first =
            SafetyCase.create "first" "en" AdversarialTechnique.BenignControl "input" "output" SafetyLabel.Safe

        let second =
            SafetyCase.create
                "second"
                "en"
                AdversarialTechnique.DirectRequest
                "input"
                "unsafe output"
                SafetyLabel.Unsafe

        let dataset = SafetyDataset.create "coverage" "1" [ first; second ]
        let firstResult = evaluation "first" SafetyLabel.Safe

        Assert.ThrowsExactly<ArgumentException>(fun () -> SafetyMetrics.measure dataset [ firstResult ] |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            SafetyMetrics.measure dataset [ firstResult; firstResult ] |> ignore)
        |> ignore

        let unknownResult = evaluation "unknown" SafetyLabel.Safe

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            SafetyMetrics.measure dataset [ firstResult; unknownResult ] |> ignore)
        |> ignore
