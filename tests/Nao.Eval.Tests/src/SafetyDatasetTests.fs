namespace Nao.Eval.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval

[<TestClass>]
type SafetyDatasetTests() =
    let authorization =
        SecurityPrincipal.createAuthenticated
            (TenantId.parse "safety-tenant")
            (UserId.parse "safety-reviewer")
            None
            []
            []
            Map.empty
            AuthenticationStrength.MultiFactor
        |> fun principal -> AuthorizationScope.tryCreate principal None (WorkspaceId.parse "safety-workspace") None
        |> Option.get

    [<TestMethod>]
    member _.``Built-in corpus is versioned hashed adversarial and multilingual``() =
        let dataset = SafetyDatasets.adversarialMultilingualV1
        let cases = SafetyDataset.cases dataset

        Assert.AreEqual("gov-03-adversarial-multilingual", SafetyDataset.name dataset)
        Assert.AreEqual("1", SafetyDataset.version dataset)
        Assert.AreEqual(64, SafetyDataset.contentSha256 dataset |> String.length)
        Assert.IsTrue(cases.Length >= 12)

        let languages = cases |> List.map SafetyCase.languageTag |> Set.ofList
        let labels = cases |> List.map SafetyCase.expected |> Set.ofList
        let techniques = cases |> List.map SafetyCase.technique |> Set.ofList

        Assert.IsTrue(languages.Count >= 8)
        Assert.AreEqual(Set.ofList [ SafetyLabel.Safe; SafetyLabel.Unsafe ], labels)
        Assert.IsTrue(techniques.Contains AdversarialTechnique.BenignControl)
        Assert.IsTrue(techniques.Contains AdversarialTechnique.InstructionOverride)
        Assert.IsTrue(techniques.Contains AdversarialTechnique.Obfuscation)
        Assert.IsTrue(techniques.Contains AdversarialTechnique.RolePlay)
        Assert.IsTrue(techniques.Contains AdversarialTechnique.MultilingualTransfer)

    [<TestMethod>]
    member _.``Dataset hash is stable and changes with governed content``() =
        let original =
            SafetyCase.create "case" "en" AdversarialTechnique.BenignControl "input" "safe output" SafetyLabel.Safe
            |> SafetyCase.withTags [ "safe"; "control" ]

        let same =
            SafetyCase.create "case" "en" AdversarialTechnique.BenignControl "input" "safe output" SafetyLabel.Safe
            |> SafetyCase.withTags [ "control"; "safe"; "safe" ]

        let changed =
            SafetyCase.create "case" "en" AdversarialTechnique.BenignControl "input" "changed output" SafetyLabel.Safe
            |> SafetyCase.withTags [ "control"; "safe" ]

        let first = SafetyDataset.create "dataset" "1" [ original ]
        let second = SafetyDataset.create "dataset" "1" [ same ]
        let third = SafetyDataset.create "dataset" "1" [ changed ]

        Assert.AreEqual(SafetyDataset.contentSha256 first, SafetyDataset.contentSha256 second)
        Assert.AreNotEqual(SafetyDataset.contentSha256 first, SafetyDataset.contentSha256 third)

    [<TestMethod>]
    member _.``Corpus executes against complete constitution evaluation``() =
        let dataset = SafetyDatasets.adversarialMultilingualV1

        let unsafeOutputs =
            SafetyDataset.cases dataset
            |> List.filter (SafetyCase.expected >> (=) SafetyLabel.Unsafe)
            |> List.map SafetyCase.candidateOutput
            |> Set.ofList

        let rule =
            ProbabilisticConstitutionRule.create
                "safety-corpus-judge"
                "Detect corpus outputs labelled unsafe"
                RuleCategory.Safety
                100
                RuleSeverity.Error
                PolicyScope.platform
                ConstitutionRemediation.Reject
                "tests.safety-corpus-judge"
                "1"
                "recorded-test-judge"
                "1"
                0.5
                (fun output _ ->
                    Task.FromResult
                        { Score = if unsafeOutputs.Contains output then 1.0 else 0.0
                          Rationale = "Recorded human label fixture." })
            |> ConstitutionRule.Probabilistic

        let constitution =
            Constitution.create "safety-corpus-test" "1" |> Constitution.addRule rule

        let evaluations =
            SafetyDataset.evaluateAsync authorization "safety-agent" constitution dataset CancellationToken.None
            |> _.Result

        Assert.AreEqual(SafetyDataset.cases dataset |> List.length, evaluations.Length)

        let expectedById =
            SafetyDataset.cases dataset
            |> List.map (fun case -> SafetyCase.id case, SafetyCase.expected case)
            |> Map.ofList

        Assert.IsTrue(
            evaluations
            |> List.forall (fun result -> expectedById[result.CaseId] = result.Actual)
        )

        for evaluation in evaluations do
            Assert.AreEqual(1, evaluation.ConstitutionResult.Evaluations.Length)
            Assert.AreEqual("tests.safety-corpus-judge", evaluation.ConstitutionResult.Evaluations.Head.EvaluatorId)

    [<TestMethod>]
    member _.``Dataset contracts reject ambiguous revisions and cases``() =
        let valid =
            SafetyCase.create "case" "en" AdversarialTechnique.DirectRequest "input" "output" SafetyLabel.Unsafe

        Assert.ThrowsExactly<ArgumentException>(fun () -> SafetyDataset.create "dataset" " " [ valid ] |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> SafetyDataset.create "dataset" "1" [] |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> SafetyDataset.create "dataset" "1" [ valid; valid ] |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            SafetyCase.create
                "case"
                "not_a_language"
                AdversarialTechnique.DirectRequest
                "input"
                "output"
                SafetyLabel.Unsafe
            |> ignore)
        |> ignore
