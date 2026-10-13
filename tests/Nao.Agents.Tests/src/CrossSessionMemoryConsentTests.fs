namespace Nao.Agents.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type CrossSessionMemoryConsentTests() =
    let scope user workspace session =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse user) []

        AuthorizationScope.tryCreate
            principal
            None
            (WorkspaceId.parse workspace)
            (session |> Option.map SessionId.parse)
        |> Option.get

    let authorization = scope "user" "workspace" (Some "session")
    let now = DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero)

    let candidateFor authorization kind =
        let event =
            SessionMemoryEvent.capture
                authorization
                (CorrelationContext.root ())
                kind
                "The user prefers concise answers."
                SessionMemoryContentClass.Ordinary
                SessionMemoryPersistenceIntent.Durable
                now

        match
            SessionMemoryEligibility.evaluate SessionMemoryEligibilityPolicy.defaultPolicy authorization now event
        with
        | SessionMemoryEligibilityResult.Eligible candidate -> candidate
        | result -> failwith $"Expected eligible candidate, received {result}."

    let targetOwner candidate =
        let source =
            candidate |> DurableSessionMemoryCandidate.event |> SessionMemoryEvent.owner

        { source with SessionId = None }

    let policy candidate owner allowed evaluatedAt validUntil =
        let eventId =
            candidate |> DurableSessionMemoryCandidate.event |> SessionMemoryEvent.id

        CrossSessionMemoryPolicyDecision.create
            "nao.cross-session-memory"
            "1"
            eventId
            owner
            allowed
            evaluatedAt
            validUntil

    let consent candidate scope grantedAt expiresAt =
        let subject =
            candidate
            |> DurableSessionMemoryCandidate.event
            |> SessionMemoryEvent.owner
            |> CrossSessionMemorySubject.ofIdentity

        CrossSessionMemoryConsent.grant subject scope grantedAt expiresAt

    let reasons =
        function
        | CrossSessionMemoryApprovalResult.Approved _ -> []
        | CrossSessionMemoryApprovalResult.Denied(_, values) -> values

    let assertReason expected actual =
        Assert.IsTrue(actual |> List.contains expected, $"Expected {expected}, received {actual}.")

    [<TestMethod>]
    member _.``Active exact consent and allow policy remove only the session scope``() =
        let candidate = candidateFor authorization SessionMemoryEventKind.UserPreference
        let owner = targetOwner candidate

        let eventId =
            candidate |> DurableSessionMemoryCandidate.event |> SessionMemoryEvent.id

        let consent =
            consent candidate (CrossSessionMemoryConsentScope.Memory eventId) now (Some(now.AddDays 30.0))

        let decision = policy candidate owner true now (now.AddMinutes 5.0)

        match CrossSessionMemoryApproval.evaluate authorization now (Some consent) decision candidate with
        | CrossSessionMemoryApprovalResult.Approved approved ->
            Assert.AreEqual(owner, CrossSessionUserMemoryCandidate.owner approved)
            Assert.AreEqual(None, (CrossSessionUserMemoryCandidate.owner approved).SessionId)
            Assert.AreEqual(candidate, CrossSessionUserMemoryCandidate.candidate approved)
            Assert.AreEqual(consent, CrossSessionUserMemoryCandidate.consent approved)
            Assert.AreEqual(decision, CrossSessionUserMemoryCandidate.policyDecision approved)
        | result -> Assert.Fail $"Expected approved cross-session memory, received {result}."

    [<TestMethod>]
    member _.``Category workspace and user consent cover matching candidates``() =
        let candidate = candidateFor authorization SessionMemoryEventKind.UserPreference
        let owner = targetOwner candidate
        let decision = policy candidate owner true now (now.AddMinutes 5.0)

        for scope in
            [ CrossSessionMemoryConsentScope.Category SessionMemoryEventKind.UserPreference
              CrossSessionMemoryConsentScope.Workspace(WorkspaceId.parse "workspace")
              CrossSessionMemoryConsentScope.User ] do
            let consent = consent candidate scope now None

            match CrossSessionMemoryApproval.evaluate authorization now (Some consent) decision candidate with
            | CrossSessionMemoryApprovalResult.Approved _ -> ()
            | result -> Assert.Fail $"Expected {scope} consent to approve the candidate, received {result}."

    [<TestMethod>]
    member _.``Missing mismatched inactive expired and revoked consent fail closed``() =
        let candidate = candidateFor authorization SessionMemoryEventKind.UserPreference
        let owner = targetOwner candidate
        let decision = policy candidate owner true now (now.AddMinutes 5.0)

        CrossSessionMemoryApproval.evaluate authorization now None decision candidate
        |> reasons
        |> assertReason CrossSessionMemoryDenialReason.ConsentMissing

        let otherCandidate = candidateFor authorization SessionMemoryEventKind.UserFact

        let otherEventId =
            otherCandidate |> DurableSessionMemoryCandidate.event |> SessionMemoryEvent.id

        let wrongScope =
            consent candidate (CrossSessionMemoryConsentScope.Memory otherEventId) now None

        CrossSessionMemoryApproval.evaluate authorization now (Some wrongScope) decision candidate
        |> reasons
        |> assertReason CrossSessionMemoryDenialReason.ConsentScopeMismatch

        let otherAuthorization = scope "other-user" "workspace" (Some "session")

        let otherSubject =
            otherAuthorization
            |> AuthorizationScope.identity
            |> CrossSessionMemorySubject.ofIdentity

        let wrongSubject =
            CrossSessionMemoryConsent.grant otherSubject CrossSessionMemoryConsentScope.User now None

        CrossSessionMemoryApproval.evaluate authorization now (Some wrongSubject) decision candidate
        |> reasons
        |> assertReason CrossSessionMemoryDenialReason.ConsentSubjectMismatch

        let future =
            consent candidate CrossSessionMemoryConsentScope.User (now.AddMinutes 1.0) None

        CrossSessionMemoryApproval.evaluate authorization now (Some future) decision candidate
        |> reasons
        |> assertReason CrossSessionMemoryDenialReason.ConsentNotYetActive

        let expired =
            consent candidate CrossSessionMemoryConsentScope.User (now.AddDays -1.0) (Some now)

        CrossSessionMemoryApproval.evaluate authorization now (Some expired) decision candidate
        |> reasons
        |> assertReason CrossSessionMemoryDenialReason.ConsentExpired

        let revoked =
            consent candidate CrossSessionMemoryConsentScope.User (now.AddDays -1.0) None
            |> CrossSessionMemoryConsent.revoke now

        CrossSessionMemoryApproval.evaluate authorization now (Some revoked) decision candidate
        |> reasons
        |> assertReason CrossSessionMemoryDenialReason.ConsentRevoked

    [<TestMethod>]
    member _.``Policy denial stale evidence and scope widening fail closed``() =
        let candidate = candidateFor authorization SessionMemoryEventKind.UserPreference
        let owner = targetOwner candidate
        let consent = consent candidate CrossSessionMemoryConsentScope.User now None

        policy candidate owner false now (now.AddMinutes 5.0)
        |> fun decision -> CrossSessionMemoryApproval.evaluate authorization now (Some consent) decision candidate
        |> reasons
        |> assertReason CrossSessionMemoryDenialReason.PolicyDenied

        policy candidate owner true (now.AddMinutes 1.0) (now.AddMinutes 5.0)
        |> fun decision -> CrossSessionMemoryApproval.evaluate authorization now (Some consent) decision candidate
        |> reasons
        |> assertReason CrossSessionMemoryDenialReason.PolicyDecisionNotYetActive

        policy candidate owner true (now.AddMinutes -2.0) now
        |> fun decision -> CrossSessionMemoryApproval.evaluate authorization now (Some consent) decision candidate
        |> reasons
        |> assertReason CrossSessionMemoryDenialReason.PolicyDecisionExpired

        let widenedOwner =
            { owner with
                WorkspaceId = WorkspaceId.parse "other-workspace" }

        policy candidate widenedOwner true now (now.AddMinutes 5.0)
        |> fun decision -> CrossSessionMemoryApproval.evaluate authorization now (Some consent) decision candidate
        |> reasons
        |> assertReason CrossSessionMemoryDenialReason.PolicyOwnerMismatch

        let otherAuthorization = scope "user" "workspace" (Some "other-session")

        policy candidate owner true now (now.AddMinutes 5.0)
        |> fun decision -> CrossSessionMemoryApproval.evaluate otherAuthorization now (Some consent) decision candidate
        |> reasons
        |> assertReason CrossSessionMemoryDenialReason.AuthorizationScopeMismatch
