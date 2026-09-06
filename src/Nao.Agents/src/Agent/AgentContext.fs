namespace Nao.Agents

open System.Threading
open System.Threading.Tasks

/// Whether nested execution may run without a harness dispatcher.
[<RequireQualifiedAccess>]
type ExecutionBoundary =
    | Unrestricted
    | HarnessRequired

/// Host services and identity scoped to one agent or tool execution.
/// Session-owned resources must be derived from `SessionKey`; context from one session must
/// never be retained or reused by another session. `GetArtifacts` and `GetGrantedResources` return
/// snapshots shared by every agent and tool that receives the same context value.
type AgentContext =
    { Correlation: CorrelationContext
      SessionKey: string
      TurnId: string
      ExecutionBoundary: ExecutionBoundary
      CancellationToken: CancellationToken
      GetArtifacts: unit -> Artifact list
      GetGrantedResources: unit -> ResourceAccess list
      RequestPermission: ResourceAccess -> string -> bool -> Task<bool>
      PublishArtifact: Artifact -> Task }

[<RequireQualifiedAccess>]
module AgentContext =
    /// Permissive no-op context for isolated tests and hosts without permissions or publishing.
    let unrestrictedForTests () =
        { Correlation = CorrelationContext.root ()
          SessionKey = ""
          TurnId = ""
          ExecutionBoundary = ExecutionBoundary.Unrestricted
          CancellationToken = CancellationToken.None
          GetArtifacts = (fun () -> [])
          GetGrantedResources = (fun () -> [])
          RequestPermission = (fun _ _ _ -> Task.FromResult true)
          PublishArtifact = (fun _ -> Task.CompletedTask) }
