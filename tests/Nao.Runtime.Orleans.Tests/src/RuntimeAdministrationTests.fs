namespace Nao.Runtime.Orleans.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

type private RecordingAdministrationTransport() =
    let requests = ResizeArray<AdministrativeMutationRequest>()

    member _.Requests = requests |> Seq.toList

    interface IRuntimeAdministrationTransport with
        member _.PreviewAsync(_, _, request, _) =
            requests.Add request

            AdministrativeImpactPreview(
                Summary = "validated",
                CurrentRevision = request.ExpectedRevision,
                TargetRevision = request.ExpectedRevision + 1L
            )
            |> Task.FromResult

        member _.MutateAsync(_, _, request, _) =
            requests.Add request

            AdministrativeMutationResult(OperationId = request.OperationId, Revision = request.ExpectedRevision + 1L)
            |> Task.FromResult

        member _.GetResourceAsync(_, _, resourceKind, resourceId, _) =
            AdministrativeResourceSnapshot(ResourceKind = resourceKind, ResourceId = resourceId)
            |> Task.FromResult

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
                "increase governed capacity"
                4L
                (Map.ofList [ "sessions", "10"; "tokens", "1000" ])

        let reordered =
            RuntimeAdministrationValidation.fingerprint
                operationId
                AdministrativeResourceKind.Quota
                "group"
                "replace"
                "increase governed capacity"
                4L
                (Map.ofList [ "tokens", "1000"; "sessions", "10" ])

        let stale =
            RuntimeAdministrationValidation.fingerprint
                operationId
                AdministrativeResourceKind.Quota
                "group"
                "replace"
                "increase governed capacity"
                3L
                (Map.ofList [ "sessions", "10"; "tokens", "1000" ])

        let changedReason =
            RuntimeAdministrationValidation.fingerprint
                operationId
                AdministrativeResourceKind.Quota
                "group"
                "replace"
                "unreviewed reason"
                4L
                (Map.ofList [ "sessions", "10"; "tokens", "1000" ])

        Assert.AreEqual(first, reordered)
        Assert.AreNotEqual(first, stale)
        Assert.AreNotEqual(first, changedReason)

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

    [<TestMethod>]
    member _.AuditEvidenceRecordsActorReasonVersionsAndAffectedResources() =
        let actor = UserId.parse "operator"

        let evidence =
            AdministrationAuditEvidence.create
                (Guid.NewGuid())
                (Guid.NewGuid())
                DateTimeOffset.UtcNow
                tenant
                actor
                AdministrativeResourceKind.Knowledge
                "catalog"
                "replace"
                "publish reviewed source configuration"
                4L
                5L
                [ "revision" ]
                "one catalog and two indexes"
                [ "index/vector"; "index/lexical" ]
                0

        Assert.AreEqual(actor, AdministrationAuditEvidence.actorId evidence)
        Assert.AreEqual("publish reviewed source configuration", AdministrationAuditEvidence.reason evidence)
        Assert.AreEqual(4L, AdministrationAuditEvidence.priorRevision evidence)
        Assert.AreEqual(5L, AdministrationAuditEvidence.newRevision evidence)

        CollectionAssert.AreEquivalent(
            [| "catalog"; "index/vector"; "index/lexical" |],
            evidence |> AdministrationAuditEvidence.affectedResourceIds |> List.toArray
        )

    [<TestMethod>]
    member _.ReferenceClientRequiresAuthenticatedAuthorityAndPreservesReason() : Task =
        task {
            let authorized =
                principal
                    tenant
                    [ RuntimeAdministrationAuthorization.resourceRole AdministrativeResourceKind.Evaluation ]
                    AuthenticationStrength.MultiFactor

            let transport = RecordingAdministrationTransport()
            let client = RuntimeAdministrationClient(authorized, tenant, transport)

            let change =
                AdministrativeChange.create
                    (Guid.NewGuid())
                    AdministrativeResourceKind.Evaluation
                    "release-gate"
                    "replace"
                    "promote reviewed evaluation baseline"
                    2L
                    (Map.ofList [ "baseline_revision", "quality-7" ])

            let! result = client.ApplyAsync(change, CancellationToken.None)
            Assert.AreEqual(3L, result.Revision)
            Assert.AreEqual(1, transport.Requests.Length)
            Assert.AreEqual("promote reviewed evaluation baseline", transport.Requests.Head.Reason)

            let weak =
                principal
                    tenant
                    [ RuntimeAdministrationAuthorization.AdministratorRole ]
                    AuthenticationStrength.SingleFactor

            let weakClient = RuntimeAdministrationClient(weak, tenant, transport)

            let! _ =
                Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(fun () ->
                    weakClient.ApplyAsync(change, CancellationToken.None) :> Task)

            return ()
        }
