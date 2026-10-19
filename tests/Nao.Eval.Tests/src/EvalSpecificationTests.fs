namespace Nao.Eval.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval

[<TestClass>]
type EvalSpecificationTests() =
    let owner =
        { TenantId = TenantId.parse "eval-spec-tenant"
          GroupId = Some(GroupId.parse "eval-spec-group")
          UserId = UserId.parse "eval-spec-user"
          WorkspaceId = WorkspaceId.parse "eval-spec-workspace"
          SessionId = Some(SessionId.parse "eval-spec-session") }

    let approval =
        { Reviewer = "quality-reviewer"
          ReviewedAt = DateTimeOffset(2026, 10, 13, 7, 0, 0, TimeSpan.Zero)
          Rationale = "Sensitive fields were replaced with synthetic values." }

    let expected =
        { Answer = Some "approved answer"
          Properties = Map [ "schema", "answer-v1" ]
          ToolCalls =
            [ { Tool = "search"
                Operation = "query"
                ArgumentsSha256 = Some(String.replicate 64 "a")
                MinimumCount = 1
                MaximumCount = 2 } ]
          ForbiddenActions = [ "write-production-data" ]
          Artifacts =
            [ { MediaType = "application/json"
                ContentSha256 = Some(String.replicate 64 "b")
                RequiredMetadata = Map [ "classification", "synthetic" ] } ]
          Citations =
            [ { SourceId = "source-1"
                ChunkId = Some "chunk-1" } ]
          Budgets =
            Some
                { MaximumDuration = Some(TimeSpan.FromSeconds 5.0)
                  MaximumInputTokens = Some 100
                  MaximumOutputTokens = Some 50
                  MaximumToolCalls = Some 2
                  MaximumCostUsd = Some 0.01M }
          TerminalStates = Set.singleton EvalExpectedTerminalState.Succeeded }

    let productionCase description source reviewStatus =
        EvalCase.create
            "quality-team"
            "production-case"
            description
            [ { Id = "system"
                Role = EvalTurnRole.System
                Content = "Use only approved evidence." }
              { Id = "question"
                Role = EvalTurnRole.User
                Content = "What is approved?" } ]
            expected
            (Set [ "production-derived"; "reviewed" ])
            (Map [ "locale", "en-US" ])
            reviewStatus
            (Some(DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)))
            source

    let fixture =
        { Id = "workspace"
          Version = "1"
          Setup =
            [ { Sequence = 0
                Adapter = "workspace-fixture"
                AdapterVersion = "1"
                Operation = "create"
                InputSha256 = String.replicate 64 "c" } ]
          Teardown =
            [ { Sequence = 0
                Adapter = "workspace-fixture"
                AdapterVersion = "1"
                Operation = "delete"
                InputSha256 = String.replicate 64 "d" } ] }

    [<TestMethod>]
    member _.``Dataset revisions hash every executable specification field``() =
        let sanitization =
            { SanitizedBy = "privacy-reviewer"
              SanitizedAt = DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero)
              AppliedRules = [ "replace-identifiers"; "remove-secrets" ]
              Approval = approval }

        let case =
            productionCase
                "Reviewed production regression."
                (EvalCaseSource.ProductionDerived sanitization)
                (EvalReviewStatus.Approved approval)

        let createdAt = DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero)

        let first =
            EvalDataset.createAt
                (Guid.Parse "7ee99410-a434-481e-b8be-b63a8a3a6f29")
                owner
                createdAt
                "release"
                1
                [ case ]
                [ fixture ]

        let equivalent =
            EvalDataset.createAt (Guid.NewGuid()) owner createdAt "release" 1 [ case ] [ fixture ]

        Assert.AreEqual(EvalDataset.contentSha256 first, EvalDataset.contentSha256 equivalent)
        Assert.AreEqual(1, EvalDataset.revision first)
        Assert.AreEqual(EvalDataset.CurrentSchemaVersion, EvalDataset.schemaVersion first)

        let revisedCase =
            case
            |> EvalCase.withDescription "Reviewed production regression with corrected expectation."

        let revised =
            EvalDataset.revise (createdAt.AddMinutes 1.0) [ revisedCase ] [ fixture ] first

        Assert.AreEqual(EvalDataset.id first, EvalDataset.id revised)
        Assert.AreEqual(2, EvalDataset.revision revised)
        Assert.AreNotEqual(EvalDataset.contentSha256 first, EvalDataset.contentSha256 revised)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            EvalDataset.restore
                EvalDataset.CurrentSchemaVersion
                (EvalDataset.id first)
                owner
                createdAt
                "release"
                1
                (String.replicate 64 "0")
                [ case ]
                [ fixture ]
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Production-derived cases require sanitization and matching approval evidence``() =
        let sanitization =
            { SanitizedBy = "privacy-reviewer"
              SanitizedAt = DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero)
              AppliedRules = [ "replace-identifiers" ]
              Approval = approval }

        let accepted =
            productionCase
                "Reviewed."
                (EvalCaseSource.ProductionDerived sanitization)
                (EvalReviewStatus.Approved approval)

        Assert.AreEqual("quality-team", EvalCase.owner accepted)
        Assert.AreEqual(2, (EvalCase.turns accepted).Length)
        Assert.AreEqual(expected, EvalCase.expectedBehavior accepted)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            productionCase "Unreviewed." (EvalCaseSource.ProductionDerived sanitization) EvalReviewStatus.Unreviewed
            |> ignore)
        |> ignore

        let unsanitized = { sanitization with AppliedRules = [] }

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            productionCase
                "Missing sanitization."
                (EvalCaseSource.ProductionDerived unsanitized)
                (EvalReviewStatus.Approved approval)
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Reports retain the complete executable configuration``() =
        let identity name configuration =
            EvalComponentIdentity.create name "2026.10" configuration

        let configuration =
            { Agent = identity "agent" "agent-config"
              Prompt = identity "prompt" "prompt-config"
              ResponseProtocol = identity "protocol" "protocol-config"
              Tools = [ identity "search" "tool-config" ]
              Provider = Some(identity "provider" "provider-config")
              Model = Some(identity "model" "model-config")
              Harness = identity "harness" "harness-config"
              Policies = [ identity "policy" "policy-config" ]
              KnowledgeIndexes = [ identity "index" "index-config" ]
              Evaluators = [ identity "evaluator" "evaluator-config" ] }

        let case = EvalCase.repository "case" "input" "answer"

        let dataset =
            EvalDataset.createAt (Guid.NewGuid()) owner DateTimeOffset.UtcNow "report" 3 [ case ] []

        let run =
            { Id = Guid.NewGuid()
              Owner = owner
              DatasetId = EvalDataset.id dataset
              DatasetRevision = EvalDataset.revision dataset
              DatasetContentSha256 = EvalDataset.contentSha256 dataset
              Configuration = configuration
              Reproducibility =
                { RandomSeed = 1
                  Deterministic = true
                  Scheduling =
                    { MaximumParallelism = 1
                      StopOnFirstFailure = false }
                  Mode = "pinned-mock"
                  FixtureSchemaVersion = None
                  FixtureFormatVersion = None }
              StartedAt = DateTimeOffset.UtcNow }

        let report = EvalReport.fromResults run "release" []

        Assert.AreEqual(EvalReport.CurrentSchemaVersion, report.SchemaVersion)
        Assert.AreEqual(3, report.DatasetRevision)
        Assert.AreEqual(EvalDataset.contentSha256 dataset, report.DatasetContentSha256)
        Assert.AreEqual(configuration, report.Configuration)

        Assert.AreNotEqual(
            EvalComponentIdentity.configurationSha256 (identity "prompt" "prompt-config"),
            EvalComponentIdentity.configurationSha256 (identity "prompt" "changed-prompt")
        )
