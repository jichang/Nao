namespace Nao.Agents.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type BackgroundMemoryExtractionTests() =
    let scope user workspace session =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse user) []

        AuthorizationScope.tryCreate
            principal
            None
            (WorkspaceId.parse workspace)
            (session |> Option.map SessionId.parse)
        |> Option.get

    let sessionAuthorization = scope "user" "workspace" (Some "session")
    let backgroundAuthorization = scope "user" "workspace" None
    let now = DateTimeOffset(2026, 10, 12, 0, 10, 0, TimeSpan.Zero)
    let content = "My preferred response style is concise, and my timezone is UTC."

    let approvedCandidate () =
        let event =
            SessionMemoryEvent.capture
                sessionAuthorization
                (CorrelationContext.root ())
                SessionMemoryEventKind.UserPreference
                content
                SessionMemoryContentClass.Ordinary
                SessionMemoryPersistenceIntent.Durable
                now

        let candidate =
            match
                SessionMemoryEligibility.evaluate
                    SessionMemoryEligibilityPolicy.defaultPolicy
                    sessionAuthorization
                    now
                    event
            with
            | SessionMemoryEligibilityResult.Eligible value -> value
            | result -> failwith $"Expected eligible candidate, received {result}."

        let sourceOwner = SessionMemoryEvent.owner event
        let targetOwner = { sourceOwner with SessionId = None }

        let consent =
            CrossSessionMemoryConsent.grant
                (CrossSessionMemorySubject.ofIdentity sourceOwner)
                (CrossSessionMemoryConsentScope.Memory(SessionMemoryEvent.id event))
                now
                None

        let decision =
            CrossSessionMemoryPolicyDecision.create
                "nao.cross-session-memory"
                "1"
                (SessionMemoryEvent.id event)
                targetOwner
                true
                now
                (now.AddMinutes 5.0)

        match CrossSessionMemoryApproval.evaluate sessionAuthorization now (Some consent) decision candidate with
        | CrossSessionMemoryApprovalResult.Approved value -> value
        | result -> failwith $"Expected approved candidate, received {result}."

    let descriptor =
        MemoryExtractorDescriptor.create
            "test.memory-extractor"
            "1"
            (Some "test-model")
            (Some "1")
            "configuration-hash"
            false

    let usage = MemoryExtractionUsage.create 1 100 0.001m

    let extractor proposals =
        { Descriptor = descriptor
          ExtractAsync = fun _ _ _ -> Task.FromResult(MemoryExtractionOutput.create proposals usage) }

    [<TestMethod>]
    member _.``Background extraction preserves provenance and separates low confidence proposals``() =
        let accepted =
            MemoryExtractionProposal.create
                "preference.response-style"
                "concise"
                "preferred response style is concise"
                (MemoryExtractionConfidence.create 0.95)

        let belowThreshold =
            MemoryExtractionProposal.create
                "preference.timezone"
                "UTC"
                "timezone is UTC"
                (MemoryExtractionConfidence.create 0.4)

        let source = approvedCandidate ()

        let batch =
            BackgroundMemoryExtraction.extractAsync
                (extractor [ accepted; belowThreshold ])
                backgroundAuthorization
                BackgroundMemoryExtractionPolicy.defaultPolicy
                now
                source
                CancellationToken.None
            |> _.Result

        Assert.AreEqual(source, BackgroundMemoryExtractionBatch.source batch)
        Assert.AreEqual(descriptor, BackgroundMemoryExtractionBatch.extractor batch)
        Assert.AreEqual(usage, BackgroundMemoryExtractionBatch.usage batch)
        Assert.AreEqual(now, BackgroundMemoryExtractionBatch.extractedAt batch)
        Assert.AreEqual(1, BackgroundMemoryExtractionBatch.accepted batch |> List.length)
        Assert.AreEqual(1, BackgroundMemoryExtractionBatch.belowConfidenceThreshold batch |> List.length)

        let acceptedAssessment =
            BackgroundMemoryExtractionBatch.accepted batch |> List.exactlyOne

        Assert.AreEqual(accepted, MemoryExtractionAssessment.proposal acceptedAssessment)

        let sourceEvent =
            batch
            |> BackgroundMemoryExtractionBatch.source
            |> CrossSessionUserMemoryCandidate.candidate
            |> DurableSessionMemoryCandidate.event

        Assert.AreEqual(content, SessionMemoryEvent.content sourceEvent)

    [<TestMethod>]
    member _.``Extractor receives approved event content kind correlation and background owner``() =
        let source = approvedCandidate ()
        let mutable observed = None

        let observingExtractor =
            { Descriptor = descriptor
              ExtractAsync =
                fun authorization request _ ->
                    observed <- Some(authorization, request)
                    Task.FromResult(MemoryExtractionOutput.create [] (MemoryExtractionUsage.create 0 0 0m)) }

        BackgroundMemoryExtraction.extractAsync
            observingExtractor
            backgroundAuthorization
            BackgroundMemoryExtractionPolicy.defaultPolicy
            now
            source
            CancellationToken.None
        |> _.Wait()

        match observed with
        | Some(authorization, request) ->
            Assert.AreEqual(
                AuthorizationScope.identity backgroundAuthorization,
                AuthorizationScope.identity authorization
            )

            Assert.AreEqual(SessionMemoryEventKind.UserPreference, MemoryExtractionRequest.eventKind request)
            Assert.AreEqual(content, MemoryExtractionRequest.content request)
        | None -> Assert.Fail "Expected extractor invocation."

    [<TestMethod>]
    member _.``Unapproved owner source spoofing duplicates and budget excess fail closed``() =
        let source = approvedCandidate ()
        let wrongAuthorization = scope "other-user" "workspace" None

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            BackgroundMemoryExtraction.extractAsync
                (extractor [])
                wrongAuthorization
                BackgroundMemoryExtractionPolicy.defaultPolicy
                now
                source
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let spoofed =
            MemoryExtractionProposal.create
                "preference.response-style"
                "verbose"
                "I prefer verbose answers"
                (MemoryExtractionConfidence.create 0.99)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            BackgroundMemoryExtraction.extractAsync
                (extractor [ spoofed ])
                backgroundAuthorization
                BackgroundMemoryExtractionPolicy.defaultPolicy
                now
                source
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let proposal =
            MemoryExtractionProposal.create
                "preference.response-style"
                "concise"
                "preferred response style is concise"
                (MemoryExtractionConfidence.create 0.99)

        let duplicate =
            MemoryExtractionProposal.create
                "PREFERENCE.RESPONSE-STYLE"
                "concise"
                "preferred response style is concise"
                (MemoryExtractionConfidence.create 0.99)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            BackgroundMemoryExtraction.extractAsync
                (extractor [ proposal; duplicate ])
                backgroundAuthorization
                BackgroundMemoryExtractionPolicy.defaultPolicy
                now
                source
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let excessiveUsageExtractor =
            { Descriptor = descriptor
              ExtractAsync =
                fun _ _ _ ->
                    Task.FromResult(MemoryExtractionOutput.create [] (MemoryExtractionUsage.create 2 100 0.001m)) }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            BackgroundMemoryExtraction.extractAsync
                excessiveUsageExtractor
                backgroundAuthorization
                BackgroundMemoryExtractionPolicy.defaultPolicy
                now
                source
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Cancellation is observed before extractor invocation``() =
        let mutable invoked = false

        let observingExtractor =
            { Descriptor = descriptor
              ExtractAsync =
                fun _ _ _ ->
                    invoked <- true
                    Task.FromResult(MemoryExtractionOutput.create [] usage) }

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        Assert.ThrowsExactly<OperationCanceledException>(fun () ->
            BackgroundMemoryExtraction.extractAsync
                observingExtractor
                backgroundAuthorization
                BackgroundMemoryExtractionPolicy.defaultPolicy
                now
                (approvedCandidate ())
                cancellation.Token
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        Assert.IsFalse invoked
