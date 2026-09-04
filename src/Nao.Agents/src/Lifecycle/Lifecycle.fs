namespace Nao.Agents

open System
open System.Threading.Tasks

/// Agent lifecycle states following a state-machine model
[<RequireQualifiedAccess>]
type LifecycleState =
    /// Agent has been created but not yet initialized
    | Created
    /// Agent is initialized and ready to accept work
    | Ready
    /// Agent is currently executing a task
    | Running
    /// Agent execution is paused (can be resumed)
    | Suspended
    /// Agent completed its task successfully
    | Completed
    /// Agent encountered a fatal error
    | Failed of error: string
    /// Agent was explicitly terminated
    | Terminated

/// Events emitted during lifecycle transitions
[<RequireQualifiedAccess>]
type LifecycleEvent =
    | Initialized of agentId: string * timestamp: DateTimeOffset
    | Started of agentId: string * input: string * timestamp: DateTimeOffset
    | Suspended of agentId: string * reason: string * timestamp: DateTimeOffset
    | Resumed of agentId: string * timestamp: DateTimeOffset
    | Completed of agentId: string * output: string * timestamp: DateTimeOffset
    | Failed of agentId: string * error: string * timestamp: DateTimeOffset
    | Terminated of agentId: string * reason: string * timestamp: DateTimeOffset

/// Functional hook that can intercept lifecycle transitions.
type LifecycleHook =
    { OnBeforeInit: string -> Task<Result<unit, string>>
      OnAfterInit: string -> Task<unit>
      OnBeforeStep: string -> string -> Task<Result<string, string>>
      OnAfterStep: string -> string -> Task<unit>
      OnCompleted: string -> string -> Task<unit>
      OnFailed: string -> exn -> Task<unit> }

[<RequireQualifiedAccess>]
module LifecycleHook =
    /// No-op lifecycle behavior suitable as a default or record-update base.
    let passthrough =
        { OnBeforeInit = (fun _ -> Task.FromResult(Ok()))
          OnAfterInit = (fun _ -> Task.FromResult(()))
          OnBeforeStep = (fun _ input -> Task.FromResult(Ok input))
          OnAfterStep = (fun _ _ -> Task.FromResult(()))
          OnCompleted = (fun _ _ -> Task.FromResult(()))
          OnFailed = (fun _ _ -> Task.FromResult(())) }

/// Manages agent lifecycle with hooks and state tracking
type AgentLifecycle =
    { State: LifecycleState
      Events: LifecycleEvent list
      Hooks: LifecycleHook list
      CreatedAt: DateTimeOffset }

module AgentLifecycle =

    let create () : AgentLifecycle =
        { State = LifecycleState.Created
          Events = []
          Hooks = []
          CreatedAt = DateTimeOffset.UtcNow }

    let withHooks (hooks: LifecycleHook list) (lc: AgentLifecycle) : AgentLifecycle = { lc with Hooks = hooks }

    let private transition (newState: LifecycleState) (event: LifecycleEvent) (lc: AgentLifecycle) : AgentLifecycle =
        { lc with
            State = newState
            Events = lc.Events @ [ event ] }

    let initializeAsync (agentId: string) (lc: AgentLifecycle) : Task<Result<AgentLifecycle, string>> =
        task {
            // Run pre-init hooks
            let mutable blocked = None

            for hook in lc.Hooks do
                if blocked.IsNone then
                    match! hook.OnBeforeInit agentId with
                    | Error msg -> blocked <- Some msg
                    | Ok() -> ()

            match blocked with
            | Some msg -> return Error msg
            | None ->
                let event = LifecycleEvent.Initialized(agentId, DateTimeOffset.UtcNow)
                let updated = lc |> transition LifecycleState.Ready event

                for hook in lc.Hooks do
                    do! hook.OnAfterInit agentId

                return Ok updated
        }

    let startAsync (agentId: string) (input: string) (lc: AgentLifecycle) : Task<AgentLifecycle> =
        task {
            let event = LifecycleEvent.Started(agentId, input, DateTimeOffset.UtcNow)
            return lc |> transition LifecycleState.Running event
        }

    let suspend (agentId: string) (reason: string) (lc: AgentLifecycle) : AgentLifecycle =
        let event = LifecycleEvent.Suspended(agentId, reason, DateTimeOffset.UtcNow)
        lc |> transition LifecycleState.Suspended event

    let resume (agentId: string) (lc: AgentLifecycle) : AgentLifecycle =
        let event = LifecycleEvent.Resumed(agentId, DateTimeOffset.UtcNow)
        lc |> transition LifecycleState.Running event

    let completeAsync (agentId: string) (output: string) (lc: AgentLifecycle) : Task<AgentLifecycle> =
        task {
            let event = LifecycleEvent.Completed(agentId, output, DateTimeOffset.UtcNow)
            let updated = lc |> transition LifecycleState.Completed event

            for hook in lc.Hooks do
                do! hook.OnCompleted agentId output

            return updated
        }

    let failAsync (agentId: string) (error: exn) (lc: AgentLifecycle) : Task<AgentLifecycle> =
        task {
            let event = LifecycleEvent.Failed(agentId, error.Message, DateTimeOffset.UtcNow)
            let updated = lc |> transition (LifecycleState.Failed error.Message) event

            for hook in lc.Hooks do
                do! hook.OnFailed agentId error

            return updated
        }

    let terminate (agentId: string) (reason: string) (lc: AgentLifecycle) : AgentLifecycle =
        let event = LifecycleEvent.Terminated(agentId, reason, DateTimeOffset.UtcNow)
        lc |> transition LifecycleState.Terminated event
