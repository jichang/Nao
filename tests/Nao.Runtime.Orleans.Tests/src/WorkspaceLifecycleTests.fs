namespace Nao.Runtime.Orleans.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

[<TestClass; TestCategory("Runtime"); TestCategory("WorkspaceLifecycle")>]
type WorkspaceLifecycleTests() =
    let tenantId = TenantId.parse "tenant"
    let definitions = WorkspaceDefinitions.create [] [] []

    let registration name version digest =
        WorkspaceRegistration.create (WorkspaceVersionId.create name version) digest "1" [] definitions

    let state () =
        WorkspaceRouteState(TenantId = "tenant", WorkspaceName = "workspace")

    let first =
        registration "workspace" "2026.1" "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

    let second =
        registration "workspace" "2026.2" "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

    [<TestMethod>]
    member _.PromotionCanaryRollbackAndRetirementAreDeterministic() =
        let route = state ()
        WorkspaceRouting.register 0L "operator" DateTimeOffset.UtcNow first route
        WorkspaceRouting.promote 1L first.Identity route
        WorkspaceRouting.register 2L "operator" DateTimeOffset.UtcNow second route
        WorkspaceRouting.setCanary 3L second.Identity 20 route

        let stable =
            [ 1..10 ] |> List.map (fun _ -> WorkspaceRouting.resolve "session-42" route)

        Assert.IsTrue(stable |> List.forall ((=) stable.Head))

        let resolved =
            [ 1..1000 ]
            |> List.map (fun index -> WorkspaceRouting.resolve $"session-{index}" route)
            |> Set.ofList

        CollectionAssert.AreEquivalent([| first.Identity; second.Identity |], resolved |> Set.toArray)

        WorkspaceRouting.promote 4L second.Identity route
        Assert.AreEqual(WorkspaceVersionId.value second.Identity, route.ActiveVersionId)
        CollectionAssert.Contains(route.PromotionHistory, WorkspaceVersionId.value first.Identity)

        WorkspaceRouting.rollback 5L first.Identity route
        Assert.AreEqual(WorkspaceVersionId.value first.Identity, route.ActiveVersionId)

        WorkspaceRouting.retire 6L second.Identity route

        let retired =
            route.Versions
            |> Seq.find (fun version -> version.VersionId = WorkspaceVersionId.value second.Identity)

        Assert.AreEqual(WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Retired, retired.Lifecycle)
        Assert.AreEqual(7L, route.Revision)

    [<TestMethod>]
    member _.StaleRevisionAndInvalidTransitionsFailClosed() =
        let route = state ()
        WorkspaceRouting.register 0L "operator" DateTimeOffset.UtcNow first route

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> WorkspaceRouting.promote 0L first.Identity route)
        |> ignore

        WorkspaceRouting.promote 1L first.Identity route

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> WorkspaceRouting.retire 2L first.Identity route)
        |> ignore

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> WorkspaceRouting.rollback 2L second.Identity route)
        |> ignore

    [<TestMethod>]
    member _.RetirementNeverRemovesCompiledCodeNeededByPinnedSessions() =
        let catalog = WorkspaceCatalog.create Set.empty
        catalog.Register(tenantId, first)
        catalog.Register(tenantId, second)

        let route = state ()
        WorkspaceRouting.register 0L "operator" DateTimeOffset.UtcNow first route
        WorkspaceRouting.promote 1L first.Identity route
        WorkspaceRouting.register 2L "operator" DateTimeOffset.UtcNow second route
        WorkspaceRouting.promote 3L second.Identity route
        WorkspaceRouting.retire 4L first.Identity route

        Assert.AreSame(first, catalog.Get(tenantId, first.Identity))
        Assert.AreSame(second, catalog.Get(tenantId, second.Identity))

    [<TestMethod>]
    member _.UnavailableDeclaredDependencyPreventsCatalogRegistration() =
        let dependency =
            { Kind = WorkspaceDependencyKind.Provider
              Id = "provider"
              Version = "2026.1" }

        let catalog = WorkspaceCatalog.create Set.empty

        let dependent =
            WorkspaceRegistration.create
                (WorkspaceVersionId.create "workspace" "2026.3")
                "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                "1"
                [ dependency ]
                definitions

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> catalog.Register(tenantId, dependent))
        |> ignore

        Assert.IsTrue(catalog.TryGet(tenantId, dependent.Identity).IsNone)

    [<TestMethod>]
    member _.LifecycleMutationRequiresTenantRoleAndStrongAuthentication() =
        let address = RuntimeAddress.workspaceRoute tenantId "workspace"

        let principal tenant roles strength =
            SecurityPrincipal.createAuthenticated tenant (UserId.parse "operator") None [] roles Map.empty strength

        let authorized =
            principal tenantId [ WorkspaceLifecycleAuthorization.RequiredRole ] AuthenticationStrength.MultiFactor

        WorkspaceLifecycleAuthorization.authorize address authorized

        let weak =
            principal tenantId [ WorkspaceLifecycleAuthorization.RequiredRole ] AuthenticationStrength.SingleFactor

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            WorkspaceLifecycleAuthorization.authorize address weak)
        |> ignore

        let crossTenant =
            principal
                (TenantId.parse "other")
                [ WorkspaceLifecycleAuthorization.RequiredRole ]
                AuthenticationStrength.PhishingResistant

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            WorkspaceLifecycleAuthorization.authorize address crossTenant)
        |> ignore

    [<TestMethod>]
    member _.WorkspaceRouteKeysAreCanonicalAndTenantPartitioned() =
        let firstAddress = RuntimeAddress.workspaceRoute tenantId "workspace"

        let secondAddress =
            RuntimeAddress.workspaceRoute (TenantId.parse "other") "workspace"

        let firstKey = RuntimeAddress.workspaceRouteKey firstAddress
        let secondKey = RuntimeAddress.workspaceRouteKey secondAddress

        Assert.AreNotEqual(firstKey, secondKey)
        Assert.IsTrue((RuntimeAddress.tryParseWorkspaceRouteKey firstKey).IsSome)
        Assert.IsTrue((RuntimeAddress.tryParseWorkspaceRouteKey "v1/not/***").IsNone)
