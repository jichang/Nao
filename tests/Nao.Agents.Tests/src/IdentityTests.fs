namespace Nao.Agents.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type IdentityTests() =
    [<TestMethod>]
    member _.TextIdentitiesRoundTripExactly() =
        Assert.AreEqual("tenant-1", TenantId.parse "tenant-1" |> TenantId.value)
        Assert.AreEqual("group-1", GroupId.parse "group-1" |> GroupId.value)
        Assert.AreEqual("user-1", UserId.parse "user-1" |> UserId.value)
        Assert.AreEqual("workspace-1", WorkspaceId.parse "workspace-1" |> WorkspaceId.value)
        Assert.AreEqual("session-1", SessionId.parse "session-1" |> SessionId.value)
        Assert.AreEqual("turn-1", TurnId.parse "turn-1" |> TurnId.value)
        Assert.AreEqual("source-1", SourceId.parse "source-1" |> SourceId.value)

    [<TestMethod>]
    member _.TextIdentitiesRejectBlankAndSurroundingWhitespace() =
        Assert.IsTrue(TenantId.tryParse "" |> Option.isNone)
        Assert.IsTrue(UserId.tryParse " " |> Option.isNone)
        Assert.IsTrue(WorkspaceId.tryParse " workspace-1" |> Option.isNone)

        Assert.ThrowsExactly<ArgumentException>(fun () -> SessionId.parse "session-1 " |> ignore)
        |> ignore

    [<TestMethod>]
    member _.GeneratedIdentitiesAreUniqueAndCanonical() =
        let executionIds = List.init 100 (fun _ -> ExecutionId.generate ())
        let serialized = executionIds |> List.map ExecutionId.serialize
        Assert.AreEqual(serialized.Length, serialized |> Set.ofList |> Set.count)

        serialized
        |> List.iter (fun value ->
            Assert.AreEqual(36, value.Length)
            Assert.AreEqual(value, ExecutionId.parse value |> ExecutionId.serialize))

        let artifact = ArtifactId.generate () |> ArtifactId.serialize
        Assert.AreEqual(artifact, ArtifactId.parse artifact |> ArtifactId.serialize)

        let trace = TraceId.generate () |> TraceId.serialize
        Assert.AreEqual(trace, TraceId.parse trace |> TraceId.serialize)

        let span = SpanId.generate () |> SpanId.serialize
        Assert.AreEqual(span, SpanId.parse span |> SpanId.serialize)

    [<TestMethod>]
    member _.DelegationRetainsCorrelationAndRecordsCausation() =
        let parent = ExecutionContext.Create SandboxConfig.Default
        let child = parent.CreateChild()
        Assert.AreNotEqual(parent.ExecutionId, child.ExecutionId)
        Assert.AreEqual(parent.Correlation.CorrelationId, child.Correlation.CorrelationId)
        Assert.AreEqual(Some parent.ExecutionId, child.Correlation.CausationId)
        Assert.AreEqual(1, child.Correlation.Attempt)

    [<TestMethod>]
    member _.RetryRetainsCorrelationAndAdvancesAttempt() =
        let execution = ExecutionContext.Create SandboxConfig.Default
        let retry = execution.CreateRetry()
        Assert.AreNotEqual(execution.ExecutionId, retry.ExecutionId)
        Assert.AreEqual(execution.Correlation.CorrelationId, retry.Correlation.CorrelationId)
        Assert.AreEqual(Some execution.ExecutionId, retry.Correlation.CausationId)
        Assert.AreEqual(2, retry.Correlation.Attempt)

    [<TestMethod>]
    member _.StandaloneContextsReceiveDistinctCorrelationRoots() =
        let firstAgent = AgentContext.unrestrictedForTests ()
        let secondAgent = AgentContext.unrestrictedForTests ()
        let firstEvent = EventScope.CreateEmpty()
        let secondEvent = EventScope.CreateEmpty()

        Assert.AreNotEqual(firstAgent.Correlation.ExecutionId, secondAgent.Correlation.ExecutionId)
        Assert.AreNotEqual(firstEvent.Correlation.ExecutionId, secondEvent.Correlation.ExecutionId)

    [<TestMethod>]
    member _.AuthorizationScopeFailsClosedAcrossTenantsAndGroups() =
        let group = GroupId.parse "engineering"

        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant-a") (UserId.parse "user-1") [ group ]

        let granted =
            AuthorizationScope.tryCreate
                principal
                (Some group)
                (WorkspaceId.parse "workspace-1")
                (Some(SessionId.parse "session-1"))
            |> Option.get

        let otherTenantPrincipal =
            SecurityPrincipal.create (TenantId.parse "tenant-b") (UserId.parse "user-1") [ group ]

        let sameNarrowIdsOtherTenant =
            AuthorizationScope.tryCreate
                otherTenantPrincipal
                (Some group)
                (WorkspaceId.parse "workspace-1")
                (Some(SessionId.parse "session-1"))
            |> Option.get

        Assert.IsFalse(AuthorizationScope.contains granted sameNarrowIdsOtherTenant)

        let unauthorizedGroup =
            AuthorizationScope.tryCreate
                principal
                (Some(GroupId.parse "finance"))
                (WorkspaceId.parse "workspace-1")
                None

        Assert.IsTrue(unauthorizedGroup |> Option.isNone)
