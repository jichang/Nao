namespace Nao.Runtime.Orleans.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

[<TestClass; TestCategory("Runtime"); TestCategory("Administration")>]
type RuntimeAdministrationTests() =
    let tenant = TenantId.parse "tenant"

    let principal tenant roles strength =
        SecurityPrincipal.createAuthenticated tenant (UserId.parse "operator") None [] roles Map.empty strength

    [<TestMethod>]
    member _.ControlPlaneAuthorityIsStrongTenantScopedAndResourceSpecific() =
        let authorized =
            principal
                tenant
                [ RuntimeAdministrationAuthorization.resourceRole AdministrativeResourceKind.Provider ]
                AuthenticationStrength.MultiFactor

        RuntimeAdministrationAuthorization.authorize tenant AdministrativeResourceKind.Provider "reload" authorized

        let agentExecutionRole =
            principal tenant [ "nao.workspace.manage" ] AuthenticationStrength.PhishingResistant

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            RuntimeAdministrationAuthorization.authorize
                tenant
                AdministrativeResourceKind.Provider
                "reload"
                agentExecutionRole)
        |> ignore

        let weak =
            principal
                tenant
                [ RuntimeAdministrationAuthorization.AdministratorRole ]
                AuthenticationStrength.SingleFactor

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            RuntimeAdministrationAuthorization.authorize tenant AdministrativeResourceKind.Workspace "promote" weak)
        |> ignore

        let otherTenant =
            principal
                (TenantId.parse "other")
                [ RuntimeAdministrationAuthorization.AdministratorRole ]
                AuthenticationStrength.PhishingResistant

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            RuntimeAdministrationAuthorization.authorize tenant AdministrativeResourceKind.Policy "replace" otherTenant)
        |> ignore

    [<TestMethod>]
    member _.EmergencyDisablementRequiresDedicatedAuthority() =
        let ordinaryAdministrator =
            principal tenant [ RuntimeAdministrationAuthorization.AdministratorRole ] AuthenticationStrength.MultiFactor

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            RuntimeAdministrationAuthorization.authorize
                tenant
                AdministrativeResourceKind.Model
                "disable"
                ordinaryAdministrator)
        |> ignore

        let emergencyAdministrator =
            principal
                tenant
                [ RuntimeAdministrationAuthorization.AdministratorRole
                  RuntimeAdministrationAuthorization.EmergencyRole ]
                AuthenticationStrength.PhishingResistant

        RuntimeAdministrationAuthorization.authorize
            tenant
            AdministrativeResourceKind.Model
            "disable"
            emergencyAdministrator

    [<TestMethod>]
    member _.AdministrativeRequestsAreCanonicalAndNeverCarrySecretMaterial() =
        RuntimeAdministrationValidation.validate
            AdministrativeResourceKind.Provider
            "primary"
            "reload"
            (Map.ofList [ "configuration_version", "2026.1"; "secret_reference", "provider/openai" ])

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            RuntimeAdministrationValidation.validate
                AdministrativeResourceKind.Provider
                "primary"
                "reload"
                (Map.ofList [ "api_key", "sensitive" ]))
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            RuntimeAdministrationValidation.validate AdministrativeResourceKind.Model "model" "reload" Map.empty)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            RuntimeAdministrationValidation.validate AdministrativeResourceKind.Provider "primary" "disable" Map.empty)
        |> ignore

    [<TestMethod>]
    member _.OperationFingerprintBindsRevisionAndCanonicalParameters() =
        let operationId = Guid.NewGuid()

        let first =
            RuntimeAdministrationValidation.fingerprint
                operationId
                AdministrativeResourceKind.Quota
                "group"
                "replace"
                4L
                (Map.ofList [ "sessions", "10"; "tokens", "1000" ])

        let reordered =
            RuntimeAdministrationValidation.fingerprint
                operationId
                AdministrativeResourceKind.Quota
                "group"
                "replace"
                4L
                (Map.ofList [ "tokens", "1000"; "sessions", "10" ])

        let stale =
            RuntimeAdministrationValidation.fingerprint
                operationId
                AdministrativeResourceKind.Quota
                "group"
                "replace"
                3L
                (Map.ofList [ "sessions", "10"; "tokens", "1000" ])

        Assert.AreEqual(first, reordered)
        Assert.AreNotEqual(first, stale)

    [<TestMethod>]
    member _.ResourceStateTracksRevisionAndEmergencyDisablement() =
        let state = RuntimeAdministrationState(TenantId = "tenant")

        state.Resources.Add(
            AdministrativeResourceSnapshot(
                ResourceKind = "connector",
                ResourceId = "knowledge",
                Revision = 7L,
                Disabled = true
            )
        )

        Assert.AreEqual(7L, RuntimeAdministrationState.revision AdministrativeResourceKind.Connector "knowledge" state)

        Assert.IsTrue(RuntimeAdministrationState.disabled AdministrativeResourceKind.Connector "knowledge" state)

        Assert.AreEqual(0L, RuntimeAdministrationState.revision AdministrativeResourceKind.Session "missing" state)
        Assert.IsFalse(RuntimeAdministrationState.disabled AdministrativeResourceKind.Tool "missing" state)
