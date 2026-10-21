namespace Nao.Runtime.Orleans.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type GroupGovernanceTests() =
    let tenantId = TenantId.parse "tenant"
    let groupId = GroupId.parse "group"
    let ownerId = UserId.parse "owner"
    let address = RuntimeAddress.group tenantId groupId
    let ownerPrincipal = SecurityPrincipal.create tenantId ownerId [ groupId ]

    let quota () =
        GroupQuotaRecord(MaximumSessions = 10, MaximumConcurrentExecutions = 3, MaximumMonthlyTokens = 1000L)

    let createMember userId role =
        GroupMemberRecord(
            TenantId = TenantId.value tenantId,
            GroupId = GroupId.value groupId,
            UserId = userId,
            Role = GroupRole.serialize role
        )

    [<TestMethod>]
    member _.ConfigurationBootstrapsAnActiveOwnerAndExplicitDefaults() =
        let state =
            GroupGovernance.configure address ownerPrincipal "workspace@1" (quota ())

        Assert.AreEqual("tenant", state.TenantId)
        Assert.AreEqual("group", state.GroupId)
        Assert.AreEqual("workspace@1", state.DefaultWorkspaceId)
        Assert.AreEqual("active", state.Lifecycle)
        Assert.AreEqual(10, state.Quota.MaximumSessions)
        Assert.AreEqual(3, state.Quota.MaximumConcurrentExecutions)
        Assert.AreEqual(1000L, state.Quota.MaximumMonthlyTokens)
        Assert.AreEqual(1, state.Members.Count)
        Assert.AreEqual("owner", state.Members.[0].UserId)
        Assert.AreEqual("owner", state.Members.[0].Role)
        Assert.IsTrue(GroupGovernance.isAuthorizedMember address ownerPrincipal state)
        Assert.IsTrue(GroupGovernance.canAdminister ownerPrincipal state)

    [<TestMethod>]
    member _.EveryQuotaDimensionMustBePositive() =
        let invalidQuotas =
            [ GroupQuotaRecord(MaximumSessions = 0, MaximumConcurrentExecutions = 1, MaximumMonthlyTokens = 1L)
              GroupQuotaRecord(MaximumSessions = 1, MaximumConcurrentExecutions = 0, MaximumMonthlyTokens = 1L)
              GroupQuotaRecord(MaximumSessions = 1, MaximumConcurrentExecutions = 1, MaximumMonthlyTokens = 0L) ]

        for invalidQuota in invalidQuotas do
            Assert.ThrowsExactly<ArgumentException>(fun () -> GroupGovernance.validateQuota invalidQuota)
            |> ignore

    [<TestMethod>]
    member _.PersistedMembershipAndTenantScopeBothAuthorizeAccess() =
        let state =
            GroupGovernance.configure address ownerPrincipal "workspace@1" (quota ())

        let administratorId = UserId.parse "administrator"
        let ordinaryMemberId = UserId.parse "member"
        state.Members.Add(createMember "administrator" GroupRole.Administrator)
        state.Members.Add(createMember "member" GroupRole.Member)

        let administrator = SecurityPrincipal.create tenantId administratorId [ groupId ]

        let ordinaryMember = SecurityPrincipal.create tenantId ordinaryMemberId [ groupId ]

        let removedMember =
            SecurityPrincipal.create tenantId (UserId.parse "removed") [ groupId ]

        let crossTenant =
            SecurityPrincipal.create (TenantId.parse "other-tenant") ownerId [ groupId ]

        Assert.IsTrue(GroupGovernance.isAuthorizedMember address administrator state)
        Assert.IsTrue(GroupGovernance.canAdminister administrator state)
        Assert.IsTrue(GroupGovernance.isAuthorizedMember address ordinaryMember state)
        Assert.IsFalse(GroupGovernance.canAdminister ordinaryMember state)
        Assert.IsFalse(GroupGovernance.isAuthorizedMember address removedMember state)
        Assert.IsFalse(GroupGovernance.isAuthorizedMember address crossTenant state)

        state.Members.[1].TenantId <- "other-tenant"
        Assert.IsFalse(GroupGovernance.isAuthorizedMember address ownerPrincipal state)
