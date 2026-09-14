namespace Nao.Agents

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

type QuarantinedOutput =
    private
        { Version: int
          Owner: AuthorizationIdentity
          ExecutionId: ExecutionId
          AgentId: string
          Output: string
          Violations: ConstitutionViolation list
          QuarantinedAt: DateTimeOffset }

type ConstitutionQuarantineStore =
    private
        { SaveAsync: AuthorizationScope -> QuarantinedOutput -> Task
          TryGetAsync: AuthorizationScope -> ExecutionId -> Task<QuarantinedOutput option> }

type ConstitutionQuarantineAuthorizer =
    private
        { AuthorizeAsync: AuthorizationScope -> ResourceAccess -> CancellationToken -> Task<PermissionDecisionEvidence> }

[<RequireQualifiedAccess>]
type ConstitutionQuarantineReview =
    | Authorized of output: QuarantinedOutput * evidence: PermissionDecisionEvidence
    | Denied of evidence: PermissionDecisionEvidence
    | NotFound of evidence: PermissionDecisionEvidence

[<RequireQualifiedAccess>]
module QuarantinedOutput =

    let currentVersion = 1
    let version output = output.Version
    let owner output = output.Owner
    let executionId output = output.ExecutionId
    let agentId output = output.AgentId
    let content output = output.Output
    let violations output = output.Violations
    let quarantinedAt output = output.QuarantinedAt

[<RequireQualifiedAccess>]
module ConstitutionQuarantineAuthorizer =

    let create authorizeAsync = { AuthorizeAsync = authorizeAsync }

[<RequireQualifiedAccess>]
module ConstitutionQuarantineStore =

    let private partition authorization =
        authorization |> AuthorizationScope.identity |> AuthorizationIdentity.storageKey

    let private resource executionId =
        ResourceAccess.administration
            $"constitution-quarantine/{ExecutionId.serialize executionId}"
            AdministrationAction.Read

    let create saveAsync tryGetAsync =
        { SaveAsync = saveAsync
          TryGetAsync = tryGetAsync }

    let inMemory () =
        let records = ConcurrentDictionary<string * ExecutionId, QuarantinedOutput>()

        create
            (fun authorization output ->
                AuthorizationScope.requireOwner output.Owner authorization
                let key = partition authorization, output.ExecutionId

                if records.TryAdd(key, output) then
                    Task.CompletedTask
                else
                    Task.FromException(
                        InvalidOperationException(
                            $"Execution '{ExecutionId.serialize output.ExecutionId}' already has quarantined output."
                        )
                    ))
            (fun authorization executionId ->
                match records.TryGetValue((partition authorization, executionId)) with
                | true, output -> Task.FromResult(Some output)
                | false, _ -> Task.FromResult None)

    let quarantineAsync (request: ConstitutionRemediationRequest) store =
        if
            Constitution.requiredRemediation request.Violations
            <> ConstitutionRemediation.Quarantine
        then
            invalidArg
                (nameof request)
                "A quarantine record requires at least one violation whose effective remediation is quarantine."

        let output =
            { Version = QuarantinedOutput.currentVersion
              Owner = AuthorizationScope.identity request.Authorization
              ExecutionId = request.ExecutionId
              AgentId = request.AgentId
              Output = request.Output
              Violations = request.Violations
              QuarantinedAt = request.RequestedAt }

        store.SaveAsync request.Authorization output

    let reviewAsync
        authorizer
        audit
        authorization
        reviewerAgentId
        executionId
        (cancellationToken: CancellationToken)
        store
        =
        task {
            if
                String.IsNullOrWhiteSpace reviewerAgentId
                || reviewerAgentId <> reviewerAgentId.Trim()
            then
                invalidArg
                    (nameof reviewerAgentId)
                    "Quarantine reviewer identity must be non-blank and have no surrounding whitespace."

            cancellationToken.ThrowIfCancellationRequested()
            let access = resource executionId

            let! evidence =
                authorizer.AuthorizeAsync authorization access cancellationToken
                |> _.WaitAsync(cancellationToken)

            if evidence.Access <> access then
                invalidOp "Quarantine authorizer returned evidence for a different resource access."

            do!
                audit.RecordAsync
                    authorization
                    (AuditLog.permissionDecision reviewerAgentId evidence (Some executionId))
                |> _.WaitAsync(cancellationToken)

            if evidence.Decision <> PermissionDecision.Allow then
                return ConstitutionQuarantineReview.Denied evidence
            else
                let! output = store.TryGetAsync authorization executionId |> _.WaitAsync(cancellationToken)

                return
                    match output with
                    | Some output ->
                        if output.Version <> QuarantinedOutput.currentVersion then
                            invalidOp $"Unsupported quarantined output version {output.Version}."

                        AuthorizationScope.requireOwner output.Owner authorization

                        if output.ExecutionId <> executionId then
                            invalidOp "Quarantine storage returned output for a different execution."

                        ConstitutionQuarantineReview.Authorized(output, evidence)
                    | None -> ConstitutionQuarantineReview.NotFound evidence
        }
