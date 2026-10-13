namespace Nao.Agents.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type SessionMemoryEligibilityTests() =
    let scope user session =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse user) []

        AuthorizationScope.tryCreate
            principal
            None
            (WorkspaceId.parse "workspace")
            (session |> Option.map SessionId.parse)
        |> Option.get

    let authorization = scope "user" (Some "session")
    let now = DateTimeOffset(2026, 10, 11, 23, 55, 0, TimeSpan.Zero)

    let event authorization kind contentClass persistenceIntent occurredAt =
        SessionMemoryEvent.capture
            authorization
            (CorrelationContext.root ())
            kind
            "The user prefers concise answers."
            contentClass
            persistenceIntent
            occurredAt

    let reasons =
        function
        | SessionMemoryEligibilityResult.Eligible _ -> []
        | SessionMemoryEligibilityResult.Ineligible(_, values) -> values

    let assertReason expected actual =
        Assert.IsTrue(actual |> List.contains expected)

    [<TestMethod>]
    member _.``Recent ordinary user assertion marked durable becomes a candidate``() =
        let source =
            event
                authorization
                SessionMemoryEventKind.UserPreference
                SessionMemoryContentClass.Ordinary
                SessionMemoryPersistenceIntent.Durable
                now

        match
            SessionMemoryEligibility.evaluate SessionMemoryEligibilityPolicy.defaultPolicy authorization now source
        with
        | SessionMemoryEligibilityResult.Eligible candidate ->
            Assert.AreSame(source, DurableSessionMemoryCandidate.event candidate)
            Assert.AreEqual("nao.session-memory-eligibility", DurableSessionMemoryCandidate.policyId candidate)
            Assert.AreEqual("1", DurableSessionMemoryCandidate.policyVersion candidate)
            Assert.AreEqual(now, DurableSessionMemoryCandidate.evaluatedAt candidate)
        | result -> Assert.Fail $"Expected eligible memory candidate, received {result}."

    [<TestMethod>]
    member _.``Assistant tool and system events can never be configured for direct promotion``() =
        for kind in
            [ SessionMemoryEventKind.AssistantOutput
              SessionMemoryEventKind.ToolOutput
              SessionMemoryEventKind.SubAgentOutput
              SessionMemoryEventKind.SystemEvent ] do
            Assert.ThrowsExactly<ArgumentException>(fun () ->
                SessionMemoryEligibilityPolicy.create "unsafe" "1" [ kind ] (TimeSpan.FromDays 1.0)
                |> ignore)
            |> ignore

            let source =
                event authorization kind SessionMemoryContentClass.Ordinary SessionMemoryPersistenceIntent.Durable now

            let actual =
                SessionMemoryEligibility.evaluate SessionMemoryEligibilityPolicy.defaultPolicy authorization now source
                |> reasons

            assertReason SessionMemoryIneligibilityReason.NotUserAsserted actual
            assertReason SessionMemoryIneligibilityReason.EventKindDenied actual

    [<TestMethod>]
    member _.``Sensitive secret transient expired future and cross-owner events fail closed``() =
        let evaluate authorization evaluatedAt source =
            SessionMemoryEligibility.evaluate
                SessionMemoryEligibilityPolicy.defaultPolicy
                authorization
                evaluatedAt
                source
            |> reasons

        let sensitive =
            event
                authorization
                SessionMemoryEventKind.UserFact
                SessionMemoryContentClass.Sensitive
                SessionMemoryPersistenceIntent.Durable
                now

        evaluate authorization now sensitive
        |> assertReason SessionMemoryIneligibilityReason.SensitiveContentRequiresReview

        let secret =
            event
                authorization
                SessionMemoryEventKind.UserFact
                SessionMemoryContentClass.Secret
                SessionMemoryPersistenceIntent.Durable
                now

        evaluate authorization now secret
        |> assertReason SessionMemoryIneligibilityReason.SecretContentForbidden

        let transient =
            event
                authorization
                SessionMemoryEventKind.UserPreference
                SessionMemoryContentClass.Ordinary
                SessionMemoryPersistenceIntent.SessionOnly
                now

        evaluate authorization now transient
        |> assertReason SessionMemoryIneligibilityReason.NotMarkedDurable

        let old =
            event
                authorization
                SessionMemoryEventKind.UserFact
                SessionMemoryContentClass.Ordinary
                SessionMemoryPersistenceIntent.Durable
                (now.AddDays -8.0)

        evaluate authorization now old
        |> assertReason SessionMemoryIneligibilityReason.EventExpired

        let future =
            event
                authorization
                SessionMemoryEventKind.UserFact
                SessionMemoryContentClass.Ordinary
                SessionMemoryPersistenceIntent.Durable
                (now.AddMinutes 1.0)

        evaluate authorization now future
        |> assertReason SessionMemoryIneligibilityReason.EventFromFuture

        let other = scope "other-user" (Some "session")

        evaluate other now sensitive
        |> assertReason SessionMemoryIneligibilityReason.AuthorizationScopeMismatch

    [<TestMethod>]
    member _.``Session identity is mandatory at event capture``() =
        let sessionless = scope "user" None

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            event
                sessionless
                SessionMemoryEventKind.UserFact
                SessionMemoryContentClass.Ordinary
                SessionMemoryPersistenceIntent.Durable
                now
            |> ignore)
        |> ignore
