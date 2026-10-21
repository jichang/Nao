namespace Nao.Runtime.Orleans

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type AdministrativeResourceKind =
    | Provider
    | Workspace
    | Policy
    | Quota
    | Session
    | Health
    | Model
    | Tool
    | Agent
    | Connector
    | Tenant

[<RequireQualifiedAccess>]
module AdministrativeResourceKind =
    let serialize =
        function
        | AdministrativeResourceKind.Provider -> "provider"
        | AdministrativeResourceKind.Workspace -> "workspace"
        | AdministrativeResourceKind.Policy -> "policy"
        | AdministrativeResourceKind.Quota -> "quota"
        | AdministrativeResourceKind.Session -> "session"
        | AdministrativeResourceKind.Health -> "health"
        | AdministrativeResourceKind.Model -> "model"
        | AdministrativeResourceKind.Tool -> "tool"
        | AdministrativeResourceKind.Agent -> "agent"
        | AdministrativeResourceKind.Connector -> "connector"
        | AdministrativeResourceKind.Tenant -> "tenant"

    let parse value =
        match value with
        | "provider" -> AdministrativeResourceKind.Provider
        | "workspace" -> AdministrativeResourceKind.Workspace
        | "policy" -> AdministrativeResourceKind.Policy
        | "quota" -> AdministrativeResourceKind.Quota
        | "session" -> AdministrativeResourceKind.Session
        | "health" -> AdministrativeResourceKind.Health
        | "model" -> AdministrativeResourceKind.Model
        | "tool" -> AdministrativeResourceKind.Tool
        | "agent" -> AdministrativeResourceKind.Agent
        | "connector" -> AdministrativeResourceKind.Connector
        | "tenant" -> AdministrativeResourceKind.Tenant
        | _ -> invalidArg (nameof value) $"Unknown administrative resource kind '{value}'."

    let supportsEmergencyDisablement =
        function
        | AdministrativeResourceKind.Model
        | AdministrativeResourceKind.Tool
        | AdministrativeResourceKind.Agent
        | AdministrativeResourceKind.Connector
        | AdministrativeResourceKind.Tenant -> true
        | _ -> false

type AdministrativeImpact =
    { Summary: string
      AffectedResourceIds: string list
      Warnings: string list }

type AdministrativeExecution =
    { OperationId: Guid
      TenantId: TenantId
      ResourceKind: AdministrativeResourceKind
      ResourceId: string
      Action: string
      Parameters: Map<string, string>
      TargetRevision: int64 }

type IRuntimeAdministrationExecutor =
    abstract member PreviewAsync:
        tenantId: TenantId *
        resourceKind: AdministrativeResourceKind *
        resourceId: string *
        action: string *
        parameters: Map<string, string> *
        cancellationToken: CancellationToken ->
            Task<AdministrativeImpact>

    /// Implementations must treat OperationId as an idempotency key.
    abstract member ApplyAsync: execution: AdministrativeExecution * cancellationToken: CancellationToken -> Task

    /// Completes only after every silo-facing cache or runtime observer has acknowledged TargetRevision.
    abstract member PropagateAsync: execution: AdministrativeExecution * cancellationToken: CancellationToken -> Task

type AdministrationAuditEvidence =
    { Id: Guid
      OperationId: Guid
      Timestamp: DateTimeOffset
      TenantId: TenantId
      ActorId: UserId
      ResourceKind: AdministrativeResourceKind
      ResourceId: string
      Action: string
      Revision: int64
      ParameterNames: string list
      ImpactSummary: string
      AffectedResourceCount: int
      WarningCount: int }

type IRuntimeAdministrationAuditStore =
    /// Implementations must record Id idempotently in durable storage.
    abstract member RecordAsync: evidence: AdministrationAuditEvidence -> Task

[<RequireQualifiedAccess>]
module RuntimeAdministrationAuthorization =
    [<Literal>]
    let AdministratorRole = "nao.control.admin"

    [<Literal>]
    let EmergencyRole = "nao.control.emergency"

    let resourceRole resourceKind =
        $"nao.control.{AdministrativeResourceKind.serialize resourceKind}.manage"

    let authorize tenantId resourceKind action principal =
        if SecurityPrincipal.tenantId principal <> tenantId then
            raise (UnauthorizedAccessException "Control-plane access cannot cross tenant boundaries.")

        if SecurityPrincipal.impersonation principal |> Option.isSome then
            raise (UnauthorizedAccessException "Control-plane mutations cannot use impersonated authority.")

        match SecurityPrincipal.authenticationStrength principal with
        | AuthenticationStrength.MultiFactor
        | AuthenticationStrength.PhishingResistant -> ()
        | _ -> raise (UnauthorizedAccessException "Control-plane mutation requires multi-factor authentication.")

        let roles = SecurityPrincipal.roles principal

        if
            not (
                Set.contains AdministratorRole roles
                || Set.contains (resourceRole resourceKind) roles
            )
        then
            raise (
                UnauthorizedAccessException(
                    $"Control-plane access requires role '{resourceRole resourceKind}' or '{AdministratorRole}'."
                )
            )

        if
            (action = "disable" || action = "enable")
            && AdministrativeResourceKind.supportsEmergencyDisablement resourceKind
            && not (Set.contains EmergencyRole roles)
        then
            raise (UnauthorizedAccessException $"Emergency changes require role '{EmergencyRole}'.")

[<RequireQualifiedAccess>]
module RuntimeAdministrationValidation =
    let canonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"{name} must be canonical and non-blank."

    let validate resourceKind resourceId action (parameters: Map<string, string>) =
        canonical (nameof resourceId) resourceId
        canonical (nameof action) action

        parameters
        |> Map.iter (fun name value ->
            canonical "Administrative parameter name" name

            if isNull value then
                invalidArg (nameof parameters) "Administrative parameter values cannot be null."

            let normalizedName = name.ToLowerInvariant()

            if
                normalizedName = "secret"
                || normalizedName = "token"
                || normalizedName = "password"
                || normalizedName = "api_key"
                || normalizedName = "apikey"
            then
                invalidArg
                    (nameof parameters)
                    $"Administrative parameter '{name}' cannot contain secret material; use a durable secret reference.")

        if action = "disable" || action = "enable" then
            if not (AdministrativeResourceKind.supportsEmergencyDisablement resourceKind) then
                invalidArg
                    (nameof action)
                    $"Resource kind '{AdministrativeResourceKind.serialize resourceKind}' cannot be emergency-disabled."
        elif AdministrativeResourceKind.supportsEmergencyDisablement resourceKind then
            invalidArg (nameof action) "Emergency resources support only 'disable' and 'enable' actions."

    let fingerprint
        (operationId: Guid)
        resourceKind
        resourceId
        action
        expectedRevision
        (parameters: Map<string, string>)
        =
        let canonicalParameters =
            parameters
            |> Map.toList
            |> List.map (fun (name, value) -> $"{name.Length}:{name}{value.Length}:{value}")
            |> String.concat "|"

        String.concat
            "\n"
            [ operationId.ToString("D")
              AdministrativeResourceKind.serialize resourceKind
              resourceId
              action
              string expectedRevision
              canonicalParameters ]
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()
