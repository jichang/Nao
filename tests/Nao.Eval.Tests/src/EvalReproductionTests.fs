namespace Nao.Eval.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Eval.Evaluators

[<TestClass>]
type EvalReproductionTests() =
    let authorization =
        let principal =
            SecurityPrincipal.create (TenantId.parse "reproduction-tenant") (UserId.parse "reproduction-user") []

        AuthorizationScope.tryCreate
            principal
            None
            (WorkspaceId.parse "reproduction-workspace")
            (Some(SessionId.parse "reproduction-session"))
        |> Option.get

    let agent =
        Agent.create
            "reproduction-agent"
            "Reproduction"
            "Returns recorded-safe output"
            0
            []
            AgentContract.Text
            (fun _ input -> Task.FromResult("actual:" + input))

    let adapterComponent name =
        EvalComponentIdentity.create name "1" (name + "-configuration")

    let specification =
        EvalExecutionSpecification.create
            (Map [ agent.Metadata.Id, adapterComponent agent.Metadata.Id ])
            (adapterComponent "prompt")
            (adapterComponent "response-protocol")
            []
            (Some(adapterComponent "provider"))
            (Some(adapterComponent "model"))
            (adapterComponent "harness")
            []
            []

    let dataset =
        EvalDataset.createAt
            (Guid.Parse "f92b4ec2-b4b9-4075-be82-90ea9c095e77")
            (AuthorizationScope.identity authorization)
            (DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero))
            "portable-reproduction"
            1
            [ EvalCase.repository "failed-case" "input" "expected" ]
            []

    let fixtures = EvalRecordedFixtureSet.create "nao-replay-v1" ReplayFixtures.empty
    let timestamp = DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.Zero)

    let execution () =
        EvalExecutionConfig.create
            authorization
            AgentContext.unrestrictedForTests
            EtclovgConfig.Default
            SandboxConfig.Default
            specification
            (EvalExecutionMode.RecordedReplay fixtures)
            (EvalRuntime.deterministic 42 timestamp)

    [<TestMethod>]
    member _.``Recorded deterministic runs are equivalent and never install live replay access``() =
        let config = EvalRunnerConfig.create (execution ())

        let first =
            EvalRunner.runDatasetAsync config ExactMatch.evaluator agent dataset CancellationToken.None
            |> _.Result

        let second =
            EvalRunner.runDatasetAsync config ExactMatch.evaluator agent dataset CancellationToken.None
            |> _.Result

        Assert.AreEqual(EvalRunComparison.Equivalent, EvalRunComparison.compare first second)
        Assert.AreEqual(first.Id, second.Id)
        Assert.AreEqual(first.RunAt, second.RunAt)
        Assert.AreEqual(first.Results.Head.Id, second.Results.Head.Id)
        Assert.AreEqual(first.Results.Head.ExecutionId, second.Results.Head.ExecutionId)
        Assert.AreEqual(0L, first.Results.Head.LatencyMs)
        Assert.AreEqual("recorded-replay", first.Reproducibility.Mode)
        Assert.AreEqual(Some EvalRecordedFixtureSet.CurrentSchemaVersion, first.Reproducibility.FixtureSchemaVersion)

    [<TestMethod>]
    member _.``Failed run round trips through a portable bundle and reproduces locally``() =
        let report =
            EvalRunner.runDatasetAsync
                (EvalRunnerConfig.create (execution ()))
                ExactMatch.evaluator
                agent
                dataset
                CancellationToken.None
            |> _.Result

        let bundle =
            EvalReproductionBundle.create dataset report agent.Metadata.Id ExactMatch.evaluator.Identity fixtures

        let portable =
            bundle |> EvalReproductionBundle.serialize |> EvalReproductionBundle.deserialize

        CollectionAssert.AreEqual([| "failed-case" |], EvalReproductionBundle.caseIds portable |> List.toArray)

        let reproduced =
            EvalReproductionBundle.runAsync
                authorization
                AgentContext.unrestrictedForTests
                EtclovgConfig.Default
                SandboxConfig.Default
                specification
                agent
                ExactMatch.evaluator
                portable
                CancellationToken.None
            |> _.Result

        Assert.AreEqual(EvalRunComparison.Equivalent, EvalRunComparison.compare report reproduced)

    [<TestMethod>]
    member _.``Variance is distinct from deterministic regression``() =
        let baseline =
            EvalRunner.runDatasetAsync
                (EvalRunnerConfig.create (execution ()))
                ExactMatch.evaluator
                agent
                dataset
                CancellationToken.None
            |> _.Result

        let changedResult =
            { baseline.Results.Head with
                ActualOutput = "changed"
                Verdict = EvalVerdict.Pass
                Reason = "changed" }

        let deterministicCandidate =
            { baseline with
                Results = [ changedResult ] }

        Assert.AreEqual(
            EvalRunComparison.Regression [ "failed-case" ],
            EvalRunComparison.compare baseline deterministicCandidate
        )

        let variableCandidate =
            { deterministicCandidate with
                Reproducibility =
                    { deterministicCandidate.Reproducibility with
                        Deterministic = false } }

        Assert.AreEqual(
            EvalRunComparison.NondeterministicVariance [ "failed-case" ],
            EvalRunComparison.compare baseline variableCandidate
        )

    [<TestMethod>]
    member _.``Pinned live mode requires provider and model identities``() =
        let incomplete =
            EvalExecutionSpecification.create
                (Map [ agent.Metadata.Id, adapterComponent agent.Metadata.Id ])
                (adapterComponent "prompt")
                (adapterComponent "response-protocol")
                []
                None
                None
                (adapterComponent "harness")
                []
                []

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            EvalExecutionConfig.create
                authorization
                AgentContext.unrestrictedForTests
                EtclovgConfig.Default
                SandboxConfig.Default
                incomplete
                EvalExecutionMode.PinnedLive
                (EvalRuntime.system 1)
            |> ignore)
        |> ignore
