namespace Nao.Agents.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type MemoryBoundaryMatrixTests() =
    let scope tenant user group workspace session =
        let groupId = group |> Option.map GroupId.parse

        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse user) (groupId |> Option.toList)

        AuthorizationScope.tryCreate
            principal
            groupId
            (WorkspaceId.parse workspace)
            (session |> Option.map SessionId.parse)
        |> Option.get

    let baseSession =
        scope "tenant-a" "user-a" (Some "group-a") "workspace-a" (Some "session-a")

    let baseDurable = scope "tenant-a" "user-a" (Some "group-a") "workspace-a" None

    let now = DateTimeOffset(2026, 10, 13, 3, 20, 0, TimeSpan.Zero)

    let sessionVariants =
        [ "tenant", scope "tenant-b" "user-a" (Some "group-a") "workspace-a" (Some "session-a")
          "user", scope "tenant-a" "user-b" (Some "group-a") "workspace-a" (Some "session-a")
          "group", scope "tenant-a" "user-a" (Some "group-b") "workspace-a" (Some "session-a")
          "workspace", scope "tenant-a" "user-a" (Some "group-a") "workspace-b" (Some "session-a")
          "session", scope "tenant-a" "user-a" (Some "group-a") "workspace-a" (Some "session-b") ]

    let durableVariants =
        [ "tenant", scope "tenant-b" "user-a" (Some "group-a") "workspace-a" None
          "user", scope "tenant-a" "user-b" (Some "group-a") "workspace-a" None
          "group", scope "tenant-a" "user-a" (Some "group-b") "workspace-a" None
          "workspace", scope "tenant-a" "user-a" (Some "group-a") "workspace-b" None
          "session", baseSession ]

    let approvedCandidate () =
        let event =
            SessionMemoryEvent.capture
                baseSession
                (CorrelationContext.root ())
                SessionMemoryEventKind.UserPreference
                "The user prefers concise answers."
                SessionMemoryContentClass.Ordinary
                SessionMemoryPersistenceIntent.Durable
                now

        let candidate =
            match
                SessionMemoryEligibility.evaluate SessionMemoryEligibilityPolicy.defaultPolicy baseSession now event
            with
            | SessionMemoryEligibilityResult.Eligible value -> value
            | result -> failwith $"Expected eligible candidate, received {result}."

        let sourceOwner = SessionMemoryEvent.owner event

        let consent =
            CrossSessionMemoryConsent.grant
                (CrossSessionMemorySubject.ofIdentity sourceOwner)
                CrossSessionMemoryConsentScope.User
                now
                None

        let decision =
            CrossSessionMemoryPolicyDecision.create
                "nao.cross-session-memory"
                "1"
                (SessionMemoryEvent.id event)
                (AuthorizationScope.identity baseDurable)
                true
                now
                (now.AddMinutes 5.0)

        match CrossSessionMemoryApproval.evaluate baseSession now (Some consent) decision candidate with
        | CrossSessionMemoryApprovalResult.Approved approved -> event, candidate, consent, decision, approved
        | result -> failwith $"Expected approved candidate, received {result}."

    let extractedBatch () =
        let _, _, _, _, approved = approvedCandidate ()

        let proposal =
            MemoryExtractionProposal.create
                "preference.response-style"
                "concise"
                "prefers concise answers"
                (MemoryExtractionConfidence.create 0.95)

        let extractor =
            { Descriptor =
                MemoryExtractorDescriptor.create "test.memory-extractor" "1" None None "configuration-hash" true
              ExtractAsync =
                fun _ _ _ ->
                    Task.FromResult(MemoryExtractionOutput.create [ proposal ] (MemoryExtractionUsage.create 0 0 0m)) }

        BackgroundMemoryExtraction.extractAsync
            extractor
            baseDurable
            BackgroundMemoryExtractionPolicy.defaultPolicy
            now
            approved
            CancellationToken.None
        |> _.Result

    let activeRecord () =
        let batch = extractedBatch ()

        DurableMemoryLifecycle.introduce
            baseDurable
            (DurableMemoryLifecyclePolicy.create (TimeSpan.FromDays 30.0) 8)
            now
            batch
            (BackgroundMemoryExtractionBatch.accepted batch |> List.exactlyOne)
        |> DurableMemoryLifecycleResult.record

    [<TestMethod>]
    member _.``Eligibility and cross-session approval bind every session identity dimension``() =
        let event, candidate, consent, decision, _ = approvedCandidate ()

        for dimension, variant in sessionVariants do
            match SessionMemoryEligibility.evaluate SessionMemoryEligibilityPolicy.defaultPolicy variant now event with
            | SessionMemoryEligibilityResult.Ineligible(_, reasons) ->
                Assert.IsTrue(
                    reasons
                    |> List.contains SessionMemoryIneligibilityReason.AuthorizationScopeMismatch,
                    $"Eligibility did not reject the changed {dimension}."
                )
            | result -> Assert.Fail $"Eligibility accepted changed {dimension}: {result}."

            match CrossSessionMemoryApproval.evaluate variant now (Some consent) decision candidate with
            | CrossSessionMemoryApprovalResult.Denied(_, reasons) ->
                Assert.IsTrue(
                    reasons
                    |> List.contains CrossSessionMemoryDenialReason.AuthorizationScopeMismatch,
                    $"Cross-session approval did not reject the changed {dimension}."
                )
            | result -> Assert.Fail $"Cross-session approval accepted changed {dimension}: {result}."

    [<TestMethod>]
    member _.``Extraction and lifecycle bind every durable identity dimension``() =
        let _, _, _, _, approved = approvedCandidate ()
        let record = activeRecord ()
        let mutable extractorCalls = 0

        let extractor =
            { Descriptor =
                MemoryExtractorDescriptor.create "test.boundary-extractor" "1" None None "configuration-hash" true
              ExtractAsync =
                fun _ _ _ ->
                    extractorCalls <- extractorCalls + 1
                    Task.FromResult(MemoryExtractionOutput.create [] (MemoryExtractionUsage.create 0 0 0m)) }

        for dimension, variant in durableVariants do
            Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                BackgroundMemoryExtraction.extractAsync
                    extractor
                    variant
                    BackgroundMemoryExtractionPolicy.defaultPolicy
                    now
                    approved
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore)
            |> ignore

            Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                DurableMemoryLifecycle.expire variant (now.AddDays 30.0) 1 record |> ignore)
            |> ignore

        Assert.AreEqual(0, extractorCalls)

    [<TestMethod>]
    member _.``Forgetting is cross-session for one owner but cannot cross other dimensions``() =
        let record = activeRecord ()

        let laterSession =
            scope "tenant-a" "user-a" (Some "group-a") "workspace-a" (Some "session-b")

        let laterRequest =
            UserMemoryForgetRequest.create
                laterSession
                (CorrelationContext.root ())
                (DurableMemoryRecord.id record)
                "The user requested forgetting from a later session."
                now

        match DurableMemoryLifecycle.forget laterSession now 1 laterRequest record with
        | DurableMemoryLifecycleResult.Forgotten _ -> ()
        | result -> Assert.Fail $"Same-owner cross-session forgetting failed: {result}."

        let originalRequest =
            UserMemoryForgetRequest.create
                baseSession
                (CorrelationContext.root ())
                (DurableMemoryRecord.id record)
                "The user requested forgetting."
                now

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            DurableMemoryLifecycle.forget laterSession now 1 originalRequest record
            |> ignore)
        |> ignore

        for dimension, variant in sessionVariants |> List.filter (fst >> (<>) "session") do
            let request =
                UserMemoryForgetRequest.create
                    variant
                    (CorrelationContext.root ())
                    (DurableMemoryRecord.id record)
                    $"Changed {dimension} requested forgetting."
                    now

            Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                DurableMemoryLifecycle.forget variant now 1 request record |> ignore)
            |> ignore

    [<TestMethod>]
    member _.``Federated recall forwards each complete identity without retaining another session``() =
        let observed = ResizeArray<AuthorizationIdentity>()

        let knowledge =
            MemoryKnowledgeRecall.create
                (ResourceAccess.knowledge "workspace-knowledge" KnowledgeAction.Retrieve)
                (fun authorization _ _ ->
                    observed.Add(AuthorizationScope.identity authorization)
                    Task.FromResult [])

        let search =
            MemoryTools.create MemoryToolConfig.Default (InMemoryStore.create ()) (Some knowledge)
            |> List.find (fun tool -> tool.Name = "memory_search")

        for _, authorization in (("base", baseSession) :: sessionVariants) do
            let context =
                { AgentContext.unrestrictedForTests () with
                    Authorization = Some authorization }

            match
                search.RunAsync
                    context
                    """{"query":"policy","intent":"Verify identity propagation","source":"knowledge"}"""
                |> _.Result
            with
            | Ok _ -> ()
            | Error failure -> Assert.Fail failure.Message

        let expected =
            (baseSession :: (sessionVariants |> List.map snd))
            |> List.map AuthorizationScope.identity

        Assert.AreEqual(expected, List.ofSeq observed)
