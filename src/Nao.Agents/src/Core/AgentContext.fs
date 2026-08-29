namespace Nao.Agents

open System.Threading.Tasks

/// Data produced during an agent or tool run for the host to publish outside the text response.
type AgentContextData =
    { /// Stable application-defined discriminator for consumers of the payload.
      Kind: string
      /// Media type describing `Payload`.
      ContentType: string
      /// Encoded payload. Its representation must match `ContentType`.
      Payload: string }

/// Host services and identity scoped to one agent or tool execution.
/// Session-owned resources must be derived from `SessionKey`; context from one session must
/// never be retained or reused by another session. `GetData` and `GetGrantedResources` return
/// snapshots shared by every agent and tool that receives the same context value.
type AgentContext = { SessionKey: string; TurnId: string; GetData: unit -> AgentContextData list; GetGrantedResources: unit -> ResourceAccess list; RequestPermission: ResourceAccess -> string -> bool -> Task<bool>; PublishData: AgentContextData -> Task }

[<RequireQualifiedAccess>]
module AgentContext =
    /// Permissive no-op context for isolated tests and hosts without permissions or publishing.
    /// Production hosts should supply session identity and real callbacks.
    let allowAll =
        { SessionKey = ""; TurnId = ""; GetData = (fun () -> []); GetGrantedResources = (fun () -> []); RequestPermission = (fun _ _ _ -> Task.FromResult true); PublishData = (fun _ -> Task.CompletedTask) }