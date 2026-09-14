namespace Nao.Agents.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type IdentityTests() =
    [<TestMethod>]
    member _.TextIdentitiesRoundTripExactly() =
        Assert.AreEqual("tenant-1", TenantId.parse "tenant-1" |> TenantId.value)
        Assert.AreEqual("group-1", GroupId.parse "group-1" |> GroupId.value)
        Assert.AreEqual("user-1", UserId.parse "user-1" |> UserId.value)
        Assert.AreEqual("service-1", ServiceId.parse "service-1" |> ServiceId.value)
        Assert.AreEqual("workspace-1", WorkspaceId.parse "workspace-1" |> WorkspaceId.value)
        Assert.AreEqual("session-1", SessionId.parse "session-1" |> SessionId.value)
        Assert.AreEqual("turn-1", TurnId.parse "turn-1" |> TurnId.value)
        Assert.AreEqual("source-1", SourceId.parse "source-1" |> SourceId.value)

    [<TestMethod>]
    member _.TextIdentitiesRejectBlankAndSurroundingWhitespace() =
        Assert.IsTrue(TenantId.tryParse "" |> Option.isNone)
        Assert.IsTrue(UserId.tryParse " " |> Option.isNone)
        Assert.IsTrue(ServiceId.tryParse "service-1 " |> Option.isNone)
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

    [<TestMethod>]
    member _.AgentAuthorityPreservesOriginalPrincipalAndOrderedDelegation() =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []

        let scope =
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

        let rootExecution = ExecutionId.generate ()
        let childExecution = ExecutionId.generate ()
        let grandchildExecution = ExecutionId.generate ()
        let root = AuthorizationScope.startAgent "root" rootExecution scope
        let child = AuthorizationScope.delegateAgent "root" "child" childExecution root

        let grandchild =
            AuthorizationScope.delegateAgent "child" "grandchild" grandchildExecution child

        Assert.AreEqual(principal, AuthorizationScope.principal grandchild)
        Assert.AreEqual(AuthorizationScope.identity scope, AuthorizationScope.identity grandchild)
        Assert.IsTrue(AuthorizationScope.contains scope root)
        Assert.IsTrue(AuthorizationScope.contains root child)
        Assert.IsTrue(AuthorizationScope.contains root grandchild)
        Assert.IsTrue(AuthorizationScope.contains child grandchild)
        Assert.IsFalse(AuthorizationScope.contains child root)
        Assert.IsFalse(AuthorizationScope.contains grandchild child)

        Assert.AreEqual(
            [ { AgentId = "root"
                ExecutionId = rootExecution }
              { AgentId = "child"
                ExecutionId = childExecution }
              { AgentId = "grandchild"
                ExecutionId = grandchildExecution } ],
            AuthorizationScope.agentLineage grandchild
        )

        AuthorizationScope.requireAgent "grandchild" grandchildExecution grandchild

    [<TestMethod>]
    member _.AgentAuthorityRejectsSubstitutionEscalationAndReplay() =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []

        let scope =
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

        let rootExecution = ExecutionId.generate ()
        let root = AuthorizationScope.startAgent "root" rootExecution scope

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            AuthorizationScope.startAgent "replacement" (ExecutionId.generate ()) root
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            AuthorizationScope.delegateAgent "forged" "child" (ExecutionId.generate ()) root
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            AuthorizationScope.delegateAgent "root" "root" (ExecutionId.generate ()) root
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            AuthorizationScope.delegateAgent "root" "child" rootExecution root |> ignore)
        |> ignore

        let elevatedPrincipal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse "tenant")
                (UserId.parse "user")
                None
                []
                [ "administrator" ]
                Map.empty
                AuthenticationStrength.MultiFactor

        let elevated =
            AuthorizationScope.tryCreate elevatedPrincipal None (WorkspaceId.parse "workspace") None
            |> Option.get

        Assert.IsFalse(AuthorizationScope.contains scope elevated)

    [<TestMethod>]
    member _.ImpersonationPreservesOriginalUserAndOwnsResourcesAsEffectiveUser() =
        let originalUser = UserId.parse "support-operator"
        let effectiveUser = UserId.parse "customer"

        let principal =
            SecurityPrincipal.createImpersonated
                (TenantId.parse "tenant")
                originalUser
                effectiveUser
                "support-case-123"
                []
                [ "support" ]
                Map.empty
                AuthenticationStrength.MultiFactor

        let scope =
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

        let executionId = ExecutionId.generate ()
        let agentScope = AuthorizationScope.startAgent "support-agent" executionId scope

        Assert.AreEqual(originalUser, SecurityPrincipal.subjectId principal)
        Assert.AreEqual(effectiveUser, SecurityPrincipal.userId principal)

        Assert.AreEqual(
            Some
                { EffectiveUserId = effectiveUser
                  Reason = "support-case-123" },
            SecurityPrincipal.impersonation principal
        )

        Assert.AreEqual(effectiveUser, AuthorizationScope.userId scope)
        Assert.AreEqual(principal, AuthorizationScope.principal agentScope)

        let directTarget =
            SecurityPrincipal.create (TenantId.parse "tenant") effectiveUser []
            |> fun direct ->
                AuthorizationScope.tryCreate direct None (WorkspaceId.parse "workspace") None
                |> Option.get

        Assert.AreEqual(AuthorizationScope.identity directTarget, AuthorizationScope.identity scope)
        Assert.IsFalse(AuthorizationScope.contains directTarget scope)
        Assert.IsFalse(AuthorizationScope.contains scope directTarget)

    [<TestMethod>]
    member _.ImpersonationRejectsAmbiguousOrUnauthenticatedTransitions() =
        let create effectiveUser reason strength =
            SecurityPrincipal.createImpersonated
                (TenantId.parse "tenant")
                (UserId.parse "operator")
                (UserId.parse effectiveUser)
                reason
                []
                []
                Map.empty
                strength
            |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            create "operator" "support-case-123" AuthenticationStrength.MultiFactor)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> create "customer" " " AuthenticationStrength.MultiFactor)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            create "customer" "support-case-123" AuthenticationStrength.Unspecified)
        |> ignore

    [<TestMethod>]
    member _.ServiceDelegationIsOrderedContainedAndCompletedBeforeAgents() =
        let gateway = ServiceId.parse "gateway"
        let coordinator = ServiceId.parse "coordinator"
        let worker = ServiceId.parse "worker"

        let principal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse "tenant")
                (UserId.parse "user")
                (Some gateway)
                []
                [ "execute" ]
                Map.empty
                AuthenticationStrength.PhishingResistant

        let scope =
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

        let coordinated = AuthorizationScope.delegateService gateway coordinator scope
        let delegated = AuthorizationScope.delegateService coordinator worker coordinated

        Assert.AreEqual(
            [ gateway; coordinator; worker ],
            delegated |> AuthorizationScope.principal |> SecurityPrincipal.serviceLineage
        )

        Assert.AreEqual(AuthorizationScope.identity scope, AuthorizationScope.identity delegated)
        Assert.IsTrue(AuthorizationScope.contains scope coordinated)
        Assert.IsTrue(AuthorizationScope.contains scope delegated)
        Assert.IsTrue(AuthorizationScope.contains coordinated delegated)
        Assert.IsFalse(AuthorizationScope.contains delegated coordinated)

        let executing =
            AuthorizationScope.startAgent "worker-agent" (ExecutionId.generate ()) delegated

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            AuthorizationScope.delegateService worker (ServiceId.parse "late-service") executing
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.ServiceDelegationRejectsSubstitutionSelfDelegationAndReplay() =
        let directPrincipal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []

        let directScope =
            AuthorizationScope.tryCreate directPrincipal None (WorkspaceId.parse "workspace") None
            |> Option.get

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            AuthorizationScope.delegateService (ServiceId.parse "forged") (ServiceId.parse "worker") directScope
            |> ignore)
        |> ignore

        let gateway = ServiceId.parse "gateway"

        let servicePrincipal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse "tenant")
                (UserId.parse "user")
                (Some gateway)
                []
                []
                Map.empty
                AuthenticationStrength.SingleFactor

        let serviceScope =
            AuthorizationScope.tryCreate servicePrincipal None (WorkspaceId.parse "workspace") None
            |> Option.get

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            AuthorizationScope.delegateService (ServiceId.parse "forged") (ServiceId.parse "worker") serviceScope
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            AuthorizationScope.delegateService gateway gateway serviceScope |> ignore)
        |> ignore

        let delegated =
            AuthorizationScope.delegateService gateway (ServiceId.parse "worker") serviceScope

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            AuthorizationScope.delegateService (ServiceId.parse "worker") gateway delegated
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.AuthorizationIdentityRetainsOwnershipWithoutAuthenticationEvidence() =
        let principal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse "tenant")
                (UserId.parse "user")
                (Some(ServiceId.parse "service"))
                [ GroupId.parse "group" ]
                [ "reader" ]
                (Map.ofList [ "private", "claim" ])
                AuthenticationStrength.MultiFactor

        let scope =
            AuthorizationScope.tryCreate
                principal
                (Some(GroupId.parse "group"))
                (WorkspaceId.parse "workspace")
                (Some(SessionId.parse "session"))
            |> Option.get

        let identity = AuthorizationScope.identity scope
        Assert.AreEqual(TenantId.parse "tenant", identity.TenantId)
        Assert.AreEqual(UserId.parse "user", identity.UserId)
        Assert.AreEqual(Some(GroupId.parse "group"), identity.GroupId)
        Assert.AreEqual(WorkspaceId.parse "workspace", identity.WorkspaceId)
        Assert.AreEqual(Some(SessionId.parse "session"), identity.SessionId)
        AuthorizationScope.requireOwner identity scope

        for other in
            [ { identity with
                  TenantId = TenantId.parse "other" }
              { identity with
                  UserId = UserId.parse "other" }
              { identity with GroupId = None }
              { identity with
                  WorkspaceId = WorkspaceId.parse "other" }
              { identity with SessionId = None } ] do
            Assert.ThrowsExactly<UnauthorizedAccessException>(fun () -> AuthorizationScope.requireOwner other scope)
            |> ignore

    [<TestMethod>]
    member _.AuthorizationScopeRejectsUninitializedIdentityComponents() =
        let tenant, user = TenantId.parse "tenant", UserId.parse "user"

        let group, workspace, session =
            GroupId.parse "group", WorkspaceId.parse "workspace", SessionId.parse "session"

        let create tenant user group workspace session =
            let principal = SecurityPrincipal.create tenant user [ group ]
            AuthorizationScope.tryCreate principal (Some group) workspace (Some session)

        Assert.IsTrue(
            create Unchecked.defaultof<TenantId> user group workspace session
            |> Option.isNone
        )

        Assert.IsTrue(
            create tenant Unchecked.defaultof<UserId> group workspace session
            |> Option.isNone
        )

        Assert.IsTrue(
            create tenant user Unchecked.defaultof<GroupId> workspace session
            |> Option.isNone
        )

        Assert.IsTrue(
            create tenant user group Unchecked.defaultof<WorkspaceId> session
            |> Option.isNone
        )

        Assert.IsTrue(
            create tenant user group workspace Unchecked.defaultof<SessionId>
            |> Option.isNone
        )

    [<TestMethod>]
    member _.StoragePartitionsPreserveEveryOwnershipBoundaryAndDelimiter() =
        let identity =
            { TenantId = TenantId.parse "tenant"
              UserId = UserId.parse "user"
              GroupId = Some(GroupId.parse "group")
              WorkspaceId = WorkspaceId.parse "workspace"
              SessionId = Some(SessionId.parse "session") }

        let owners =
            [ identity
              { identity with
                  TenantId = TenantId.parse "other" }
              { identity with
                  UserId = UserId.parse "other" }
              { identity with GroupId = None }
              { identity with
                  GroupId = Some(GroupId.parse "other") }
              { identity with
                  WorkspaceId = WorkspaceId.parse "other" }
              { identity with SessionId = None }
              { identity with
                  SessionId = Some(SessionId.parse "other") }
              { identity with
                  TenantId = TenantId.parse "a/b" }
              { identity with
                  TenantId = TenantId.parse "a_b" }
              { identity with
                  TenantId = TenantId.parse "a"
                  UserId = UserId.parse "b/c" }
              { identity with
                  TenantId = TenantId.parse "a/b"
                  UserId = UserId.parse "c" }
              { identity with
                  TenantId = TenantId.parse "a\nb"
                  UserId = UserId.parse "c" }
              { identity with
                  TenantId = TenantId.parse "a"
                  UserId = UserId.parse "b\nc" } ]

        let keys = owners |> List.map AuthorizationIdentity.storageKey
        Assert.AreEqual(keys.Length, keys |> Set.ofList |> Set.count)

        for key in keys do
            Assert.AreEqual(64, key.Length)
            Assert.IsTrue(key |> Seq.forall (fun character -> "0123456789abcdef".Contains character))

        Assert.AreEqual(keys.Head, AuthorizationIdentity.storageKey identity)

    [<TestMethod>]
    member _.AuthenticatedPrincipalRetainsTransportNeutralIdentityEvidence() =
        let representativeRoles = [ "reader"; "operator"; "auditor" ]

        let selectedRoles =
            representativeRoles
            |> List.take (Random.Shared.Next(1, representativeRoles.Length + 1))

        let representativeClaims =
            [ "region", "eu-west"
              "department", "engineering"
              "assurance-policy", "workforce-v2" ]

        let selectedClaims =
            representativeClaims
            |> List.take (Random.Shared.Next(1, representativeClaims.Length + 1))
            |> Map.ofList

        let subjectId = UserId.parse "user-1"
        let serviceId = ServiceId.parse "assistant-api"

        let principal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse "tenant-1")
                subjectId
                (Some serviceId)
                [ GroupId.parse "engineering" ]
                selectedRoles
                selectedClaims
                AuthenticationStrength.PhishingResistant

        Assert.AreEqual(subjectId, SecurityPrincipal.subjectId principal)
        Assert.AreEqual(Some serviceId, SecurityPrincipal.serviceId principal)
        Assert.AreEqual([ serviceId ], SecurityPrincipal.serviceLineage principal)
        Assert.AreEqual(None, SecurityPrincipal.impersonation principal)
        Assert.AreEqual(Set.ofList selectedRoles, SecurityPrincipal.roles principal)
        Assert.AreEqual(selectedClaims, SecurityPrincipal.claims principal)
        Assert.AreEqual(AuthenticationStrength.PhishingResistant, SecurityPrincipal.authenticationStrength principal)

    [<TestMethod>]
    member _.MinimalPrincipalHasNoUnassertedAuthenticationEvidence() =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant-1") (UserId.parse "user-1") []

        Assert.AreEqual(None, SecurityPrincipal.serviceId principal)
        Assert.AreEqual([], SecurityPrincipal.serviceLineage principal)
        Assert.AreEqual(None, SecurityPrincipal.impersonation principal)
        Assert.AreEqual(Set.empty, SecurityPrincipal.roles principal)
        Assert.AreEqual(Map.empty, SecurityPrincipal.claims principal)
        Assert.AreEqual(AuthenticationStrength.Unspecified, SecurityPrincipal.authenticationStrength principal)

    [<TestMethod>]
    member _.AuthenticatedPrincipalRejectsMalformedRolesAndClaims() =
        let create roles claims =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse "tenant-1")
                (UserId.parse "user-1")
                None
                []
                roles
                claims
                AuthenticationStrength.MultiFactor
            |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> create [ " operator" ] Map.empty)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> create [] (Map.ofList [ " region", "eu-west" ]))
        |> ignore
