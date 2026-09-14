namespace Nao.Agents.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type ConstitutionQuarantineTests() =

    let scope tenant user =
        SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse user) []
        |> fun principal -> AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let evidence access decision =
        { Id = Guid.NewGuid()
          Access = access
          Decision = decision
          Basis =
            if decision = PermissionDecision.Allow then
                PermissionDecisionBasis.HostApproval PermissionGrantScope.Operation
            else
                PermissionDecisionBasis.HostDenial
          PolicyVersions = Map [ PermissionPolicy.identity, PermissionPolicy.currentVersion ]
          Grants = []
          EvaluatedAt = DateTimeOffset.UtcNow }

    let request authorization executionId =
        let evaluation =
            { Id = Guid.NewGuid()
              ConstitutionName = "test"
              ConstitutionVersion = "1"
              RuleId = "quarantine"
              RuleCategory = RuleCategory.Safety
              RuleSeverity = RuleSeverity.Critical
              Scope = PolicyScopeKind.Platform
              Remediation = ConstitutionRemediation.Quarantine
              EvaluatorId = "tests.quarantine"
              EvaluatorVersion = "1"
              Outcome = ConstitutionEvaluationOutcome.Violated
              Evaluator = ConstitutionEvaluatorEvidence.Deterministic Map.empty
              OutputDigest = String.replicate 64 "0"
              EvaluatedAt = DateTimeOffset.UtcNow }

        { Authorization = authorization
          ExecutionId = executionId
          AgentId = "agent"
          Output = "sensitive quarantined output"
          Violations =
            [ { RuleId = "quarantine"
                RuleDescription = "Quarantine unsafe output"
                Severity = RuleSeverity.Critical
                Remediation = ConstitutionRemediation.Quarantine
                Evidence = evaluation } ]
          RequestedAt = DateTimeOffset.UtcNow }

    let auditLog (entries: ResizeArray<AuthorizationScope * AuditEntry>) =
        { RecordAsync =
            fun authorization entry ->
                entries.Add(authorization, entry)
                Task.FromResult()
          QueryAsync = fun _ _ _ -> Task.FromResult []
          QueryByExecutionAsync = fun _ _ -> Task.FromResult []
          GetDeniedCountAsync = fun _ _ _ -> Task.FromResult 0
          DeleteScopeAsync = fun _ -> Task.FromResult(Ok 0)
          DeleteExpiredAsync = fun _ _ -> Task.FromResult(Ok 0) }

    [<TestMethod>]
    member _.``Quarantined content requires explicit authorized and audited review``() =
        let ownerScope = scope "tenant" "owner"
        let executionId = ExecutionId.generate ()
        let store = ConstitutionQuarantineStore.inMemory ()

        ConstitutionQuarantineStore.quarantineAsync (request ownerScope executionId) store
        |> _.Wait()

        let auditEntries = ResizeArray<AuthorizationScope * AuditEntry>()
        let audit = auditLog auditEntries
        let mutable requestedAccess = None

        let deniedAuthorizer =
            ConstitutionQuarantineAuthorizer.create (fun authorization access _ ->
                Assert.AreEqual(ownerScope, authorization)
                requestedAccess <- Some access
                Task.FromResult(evidence access PermissionDecision.Deny))

        let denied =
            ConstitutionQuarantineStore.reviewAsync
                deniedAuthorizer
                audit
                ownerScope
                "reviewer"
                executionId
                CancellationToken.None
                store
            |> _.Result

        match denied with
        | ConstitutionQuarantineReview.Denied decision -> Assert.AreEqual(PermissionDecision.Deny, decision.Decision)
        | _ -> Assert.Fail "Denied review unexpectedly exposed quarantined content."

        let expectedAccess =
            ResourceAccess.administration
                $"constitution-quarantine/{ExecutionId.serialize executionId}"
                AdministrationAction.Read

        Assert.AreEqual(Some expectedAccess, requestedAccess)
        Assert.AreEqual(1, auditEntries.Count)
        Assert.IsFalse(auditEntries[0] |> snd |> _.Permitted)

        let allowedAuthorizer =
            ConstitutionQuarantineAuthorizer.create (fun _ access _ ->
                Task.FromResult(evidence access PermissionDecision.Allow))

        let authorized =
            ConstitutionQuarantineStore.reviewAsync
                allowedAuthorizer
                audit
                ownerScope
                "reviewer"
                executionId
                CancellationToken.None
                store
            |> _.Result

        match authorized with
        | ConstitutionQuarantineReview.Authorized(output, decision) ->
            Assert.AreEqual(PermissionDecision.Allow, decision.Decision)
            Assert.AreEqual("sensitive quarantined output", QuarantinedOutput.content output)
            Assert.AreEqual(QuarantinedOutput.currentVersion, QuarantinedOutput.version output)
            Assert.AreEqual(AuthorizationScope.identity ownerScope, QuarantinedOutput.owner output)
        | _ -> Assert.Fail "Explicitly authorized review did not return quarantined content."

        Assert.AreEqual(2, auditEntries.Count)
        Assert.IsTrue(auditEntries[1] |> snd |> _.Permitted)

    [<TestMethod>]
    member _.``Authorized review remains isolated by complete owner identity``() =
        let ownerScope = scope "tenant" "owner"
        let otherScope = scope "tenant" "other-user"
        let executionId = ExecutionId.generate ()
        let store = ConstitutionQuarantineStore.inMemory ()

        ConstitutionQuarantineStore.quarantineAsync (request ownerScope executionId) store
        |> _.Wait()

        let authorizer =
            ConstitutionQuarantineAuthorizer.create (fun _ access _ ->
                Task.FromResult(evidence access PermissionDecision.Allow))

        let reviewed =
            ConstitutionQuarantineStore.reviewAsync
                authorizer
                (auditLog (ResizeArray()))
                otherScope
                "reviewer"
                executionId
                CancellationToken.None
                store
            |> _.Result

        match reviewed with
        | ConstitutionQuarantineReview.NotFound _ -> ()
        | _ -> Assert.Fail "Authorization in another owner scope accessed quarantined content."

    [<TestMethod>]
    member _.``Mismatched authorization evidence never reaches quarantine storage``() =
        let ownerScope = scope "tenant" "owner"
        let executionId = ExecutionId.generate ()
        let store = ConstitutionQuarantineStore.inMemory ()

        ConstitutionQuarantineStore.quarantineAsync (request ownerScope executionId) store
        |> _.Wait()

        let authorizer =
            ConstitutionQuarantineAuthorizer.create (fun _ _ _ ->
                let wrongAccess =
                    ResourceAccess.administration "constitution-quarantine/other" AdministrationAction.Read

                Task.FromResult(evidence wrongAccess PermissionDecision.Allow))

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            ConstitutionQuarantineStore.reviewAsync
                authorizer
                (auditLog (ResizeArray()))
                ownerScope
                "reviewer"
                executionId
                CancellationToken.None
                store
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Audit failure prevents authorized quarantine access``() =
        let ownerScope = scope "tenant" "owner"
        let executionId = ExecutionId.generate ()
        let store = ConstitutionQuarantineStore.inMemory ()

        ConstitutionQuarantineStore.quarantineAsync (request ownerScope executionId) store
        |> _.Wait()

        let authorizer =
            ConstitutionQuarantineAuthorizer.create (fun _ access _ ->
                Task.FromResult(evidence access PermissionDecision.Allow))

        let failingAudit =
            { auditLog (ResizeArray()) with
                RecordAsync =
                    fun _ _ -> Task.FromException<unit>(InvalidOperationException("Audit persistence unavailable.")) }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            ConstitutionQuarantineStore.reviewAsync
                authorizer
                failingAudit
                ownerScope
                "reviewer"
                executionId
                CancellationToken.None
                store
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore
