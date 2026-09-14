namespace Nao.Agents

open System.Threading
open System.Threading.Tasks

/// Whether nested execution may run without a harness dispatcher.
[<RequireQualifiedAccess>]
type ExecutionBoundary =
    | Unrestricted
    | HarnessRequired

/// Host services and identity scoped to one agent or tool execution.
/// Protected resource ownership must be derived from `Authorization`, not routing keys; context from one session must
/// never be retained or reused by another session. `GetArtifacts` and `GetGrantedResources` return
/// snapshots shared by every agent and tool that receives the same context value.
type AgentContext =
    { Correlation: CorrelationContext
      SessionKey: string
      TurnId: string
      ExecutionBoundary: ExecutionBoundary
      CancellationToken: CancellationToken
      NextToolCallKey: unit -> IdempotencyKey
      NextArtifactKey: unit -> IdempotencyKey
      GetArtifacts: unit -> Artifact list
      GetGrantedResources: unit -> ResourceAccess list
      RequestPermission: ResourceAccess -> string -> bool -> Task<bool>
      RequestModelEligibility: string -> Task<bool>
      PublishArtifact: Artifact -> Task
      Authorization: AuthorizationScope option }

[<RequireQualifiedAccess>]
module AgentContext =
    let requireAuthorization context =
        context.Authorization
        |> Option.defaultWith (fun () ->
            raise (
                System.UnauthorizedAccessException
                    "Protected operations require a host-authenticated authorization scope."
            ))
