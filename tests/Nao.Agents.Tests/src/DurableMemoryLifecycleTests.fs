namespace Nao.Agents.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type DurableMemoryLifecycleTests() =
    let scope user session =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse user) []

        AuthorizationScope.tryCreate
            principal
            None
            (WorkspaceId.parse "workspace")
            (session |> Option.map SessionId.parse)
        |> Option.get

    let sessionAuthorization = scope "user" (Some "session")
    let durableAuthorization = scope "user" None
    let now = DateTimeOffset(2026, 10, 12, 0, 20, 0, TimeSpan.Zero)
    let policy = DurableMemoryLifecyclePolicy.create (TimeSpan.FromDays 30.0) 4

    let extractionBatch kind key value at =
        let content = $"The remembered value is {value}."

        let event =
            SessionMemoryEvent.capture
                sessionAuthorization
                (CorrelationContext.root ())
                kind
                content
                SessionMemoryContentClass.Ordinary
                SessionMemoryPersistenceIntent.Durable
                at

        let candidate =
            match
                SessionMemoryEligibility.evaluate
                    SessionMemoryEligibilityPolicy.defaultPolicy
                    sessionAuthorization
                    at
                    event
            with
            | SessionMemoryEligibilityResult.Eligible value -> value
            | result -> failwith $"Expected eligible candidate, received {result}."

        let sourceOwner = SessionMemoryEvent.owner event
        let owner = { sourceOwner with SessionId = None }

        let consent =
            CrossSessionMemoryConsent.grant
                (CrossSessionMemorySubject.ofIdentity sourceOwner)
                CrossSessionMemoryConsentScope.User
                at
                None

        let policyDecision =
            CrossSessionMemoryPolicyDecision.create
                "nao.cross-session-memory"
                "1"
                (SessionMemoryEvent.id event)
                owner
                true
                at
                (at.AddMinutes 5.0)

        let approved =
            match
                CrossSessionMemoryApproval.evaluate sessionAuthorization at (Some consent) policyDecision candidate
            with
            | CrossSessionMemoryApprovalResult.Approved value -> value
            | result -> failwith $"Expected approved candidate, received {result}."

        let proposal =
            MemoryExtractionProposal.create
                key
                value
                $"remembered value is {value}"
                (MemoryExtractionConfidence.create 0.95)

        let extractor =
            { Descriptor =
                MemoryExtractorDescriptor.create "test.memory-extractor" "1" None None "configuration-hash" true
              ExtractAsync =
                fun _ _ _ ->
                    Task.FromResult(MemoryExtractionOutput.create [ proposal ] (MemoryExtractionUsage.create 0 0 0m)) }

        let batch =
            BackgroundMemoryExtraction.extractAsync
                extractor
                durableAuthorization
                BackgroundMemoryExtractionPolicy.defaultPolicy
                at
                approved
                CancellationToken.None
            |> _.Result

        batch, (BackgroundMemoryExtractionBatch.accepted batch |> List.exactlyOne)

    let introduce key value =
        let batch, assessment =
            extractionBatch SessionMemoryEventKind.UserPreference key value now

        DurableMemoryLifecycle.introduce durableAuthorization policy now batch assessment
        |> DurableMemoryLifecycleResult.record

    let activeValue record =
        match DurableMemoryRecord.state record with
        | DurableMemoryState.Active value -> value
        | state -> failwith $"Expected active memory, received {state}."

    [<TestMethod>]
    member _.``Accepted extraction creates expiring versioned memory with provenance``() =
        let record = introduce "preference.theme" "dark"
        let value = activeValue record

        Assert.AreEqual(1, DurableMemoryRecord.version record)
        Assert.AreEqual(Some "preference.theme", DurableMemoryRecord.key record)
        Assert.AreEqual(AuthorizationScope.identity durableAuthorization, DurableMemoryRecord.owner record)
        Assert.AreEqual("dark", DurableMemoryValue.value value)
        Assert.AreEqual(now.AddDays 30.0, DurableMemoryValue.expiresAt value)
        Assert.AreEqual(1, DurableMemoryValue.sources value |> List.length)
        Assert.AreEqual(64, DurableMemoryValue.valueHash value |> String.length)

    [<TestMethod>]
    member _.``Matching evidence reinforces while ordinary contradiction becomes contested``() =
        let initial = introduce "preference.theme" "dark"

        let reinforcedBatch, reinforcedAssessment =
            extractionBatch SessionMemoryEventKind.UserPreference "preference.theme" "dark" (now.AddDays 1.0)

        let reinforced =
            DurableMemoryLifecycle.applyExtracted
                durableAuthorization
                policy
                (now.AddDays 1.0)
                1
                reinforcedBatch
                reinforcedAssessment
                initial
            |> DurableMemoryLifecycleResult.record

        Assert.AreEqual(2, DurableMemoryRecord.version reinforced)
        Assert.AreEqual(2, reinforced |> activeValue |> DurableMemoryValue.sources |> List.length)

        let conflictBatch, conflictAssessment =
            extractionBatch SessionMemoryEventKind.UserPreference "preference.theme" "light" (now.AddDays 2.0)

        let contested =
            DurableMemoryLifecycle.applyExtracted
                durableAuthorization
                policy
                (now.AddDays 2.0)
                2
                conflictBatch
                conflictAssessment
                reinforced
            |> DurableMemoryLifecycleResult.record

        match DurableMemoryRecord.state contested with
        | DurableMemoryState.Contested conflict ->
            Assert.AreEqual(2, DurableMemoryConflict.alternatives conflict |> List.length)
            Assert.AreEqual(now.AddDays 2.0, DurableMemoryConflict.detectedAt conflict)
        | state -> Assert.Fail $"Expected contested memory, received {state}."

    [<TestMethod>]
    member _.``Direct user correction supersedes active and contested values``() =
        let initial = introduce "preference.theme" "dark"

        let correctionBatch, correctionAssessment =
            extractionBatch SessionMemoryEventKind.UserCorrection "preference.theme" "light" (now.AddDays 1.0)

        let corrected =
            DurableMemoryLifecycle.applyExtracted
                durableAuthorization
                policy
                (now.AddDays 1.0)
                1
                correctionBatch
                correctionAssessment
                initial
            |> DurableMemoryLifecycleResult.record

        Assert.AreEqual("light", corrected |> activeValue |> DurableMemoryValue.value)
        Assert.AreEqual(2, DurableMemoryRecord.version corrected)

        let conflictBatch, conflictAssessment =
            extractionBatch SessionMemoryEventKind.UserFact "preference.theme" "blue" (now.AddDays 2.0)

        let contested =
            DurableMemoryLifecycle.applyExtracted
                durableAuthorization
                policy
                (now.AddDays 2.0)
                2
                conflictBatch
                conflictAssessment
                corrected
            |> DurableMemoryLifecycleResult.record

        let finalBatch, finalAssessment =
            extractionBatch SessionMemoryEventKind.UserCorrection "preference.theme" "system" (now.AddDays 3.0)

        let resolved =
            DurableMemoryLifecycle.applyExtracted
                durableAuthorization
                policy
                (now.AddDays 3.0)
                3
                finalBatch
                finalAssessment
                contested
            |> DurableMemoryLifecycleResult.record

        Assert.AreEqual("system", resolved |> activeValue |> DurableMemoryValue.value)

    [<TestMethod>]
    member _.``Expiry and user forgetting scrub retrievable key and content``() =
        let expiring = introduce "preference.theme" "dark"

        let expired =
            DurableMemoryLifecycle.expire durableAuthorization (now.AddDays 30.0) 1 expiring
            |> DurableMemoryLifecycleResult.record

        Assert.AreEqual(None, DurableMemoryRecord.key expired)

        match DurableMemoryRecord.state expired with
        | DurableMemoryState.Expired value -> Assert.AreEqual(now.AddDays 30.0, value)
        | state -> Assert.Fail $"Expected expired memory, received {state}."

        let forgettable = introduce "preference.locale" "en-US"

        let request =
            UserMemoryForgetRequest.create
                sessionAuthorization
                (CorrelationContext.root ())
                (DurableMemoryRecord.id forgettable)
                "The user asked to remove this preference."
                (now.AddMinutes 1.0)

        let forgotten =
            DurableMemoryLifecycle.forget sessionAuthorization (now.AddMinutes 1.0) 1 request forgettable
            |> DurableMemoryLifecycleResult.record

        Assert.AreEqual(None, DurableMemoryRecord.key forgotten)

        match DurableMemoryRecord.state forgotten with
        | DurableMemoryState.Forgotten evidence ->
            Assert.AreEqual("The user asked to remove this preference.", UserMemoryForgetEvidence.reason evidence)
        | state -> Assert.Fail $"Expected forgotten memory, received {state}."

    [<TestMethod>]
    member _.``Lifecycle rejects cross-owner stale and mismatched transitions``() =
        let record = introduce "preference.theme" "dark"
        let otherDurable = scope "other-user" None

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            DurableMemoryLifecycle.expire otherDurable (now.AddDays 30.0) 1 record |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            DurableMemoryLifecycle.expire durableAuthorization (now.AddDays 29.0) 1 record
            |> ignore)
        |> ignore

        let wrongRequest =
            UserMemoryForgetRequest.create
                sessionAuthorization
                (CorrelationContext.root ())
                (Guid.NewGuid())
                "Forget another record."
                now

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            DurableMemoryLifecycle.forget sessionAuthorization now 1 wrongRequest record
            |> ignore)
        |> ignore

        let expired =
            DurableMemoryLifecycle.expire durableAuthorization (now.AddDays 30.0) 1 record
            |> DurableMemoryLifecycleResult.record

        let batch, assessment =
            extractionBatch SessionMemoryEventKind.UserCorrection "preference.theme" "light" (now.AddDays 31.0)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            DurableMemoryLifecycle.applyExtracted
                durableAuthorization
                policy
                (now.AddDays 31.0)
                2
                batch
                assessment
                expired
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            DurableMemoryLifecycle.expire durableAuthorization (now.AddDays 30.0) 0 record
            |> ignore)
        |> ignore
