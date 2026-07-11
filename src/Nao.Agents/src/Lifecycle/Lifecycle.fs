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
    | Initialized of agentId: AgentId * timestamp: DateTimeOffset
    | Started of agentId: AgentId * input: string * timestamp: DateTimeOffset
    | Suspended of agentId: AgentId * reason: string * timestamp: DateTimeOffset
    | Resumed of agentId: AgentId * timestamp: DateTimeOffset
    | Completed of agentId: AgentId * output: string * timestamp: DateTimeOffset
    | Failed of agentId: AgentId * error: string * timestamp: DateTimeOffset
    | Terminated of agentId: AgentId * reason: string * timestamp: DateTimeOffset

/// Hook that can intercept lifecycle transitions
type ILifecycleHook =
    /// Called before initialization — can prevent startup
    abstract member OnBeforeInit: AgentId -> Task<Result<unit, string>>
    /// Called after successful initialization
    abstract member OnAfterInit: AgentId -> Task<unit>
    /// Called before each execution step — can prevent or modify input
    abstract member OnBeforeStep: AgentId -> string -> Task<Result<string, string>>
    /// Called after each execution step
    abstract member OnAfterStep: AgentId -> string -> Task<unit>
    /// Called when the agent completes
    abstract member OnCompleted: AgentId -> string -> Task<unit>
    /// Called when the agent fails
    abstract member OnFailed: AgentId -> exn -> Task<unit>

/// Manages agent lifecycle with hooks and state tracking
type AgentLifecycle =
    { /// Current lifecycle state
      State: LifecycleState
      /// History of lifecycle events
      Events: LifecycleEvent list
      /// Registered lifecycle hooks
      Hooks: ILifecycleHook list
      /// When the lifecycle was created
      CreatedAt: DateTimeOffset }

module AgentLifecycle =

    let create () : AgentLifecycle =
        { State = LifecycleState.Created
          Events = []
          Hooks = []
          CreatedAt = DateTimeOffset.UtcNow }

    let withHooks (hooks: ILifecycleHook list) (lc: AgentLifecycle) : AgentLifecycle =
        { lc with Hooks = hooks }

    let private transition (newState: LifecycleState) (event: LifecycleEvent) (lc: AgentLifecycle) : AgentLifecycle =
        { lc with State = newState; Events = lc.Events @ [ event ] }

    let initializeAsync (agentId: AgentId) (lc: AgentLifecycle) : Task<Result<AgentLifecycle, string>> =
        task {
            // Run pre-init hooks
            let mutable blocked = None
            for hook in lc.Hooks do
                if blocked.IsNone then
                    match! hook.OnBeforeInit agentId with
                    | Error msg -> blocked <- Some msg
                    | Ok () -> ()

            match blocked with
            | Some msg -> return Error msg
            | None ->
                let event = LifecycleEvent.Initialized (agentId, DateTimeOffset.UtcNow)
                let updated = lc |> transition LifecycleState.Ready event
                for hook in lc.Hooks do
                    do! hook.OnAfterInit agentId
                return Ok updated
        }

    let startAsync (agentId: AgentId) (input: string) (lc: AgentLifecycle) : Task<AgentLifecycle> =
        task {
            let event = LifecycleEvent.Started (agentId, input, DateTimeOffset.UtcNow)
            return lc |> transition LifecycleState.Running event
        }

    let suspend (agentId: AgentId) (reason: string) (lc: AgentLifecycle) : AgentLifecycle =
        let event = LifecycleEvent.Suspended (agentId, reason, DateTimeOffset.UtcNow)
        lc |> transition LifecycleState.Suspended event

    let resume (agentId: AgentId) (lc: AgentLifecycle) : AgentLifecycle =
        let event = LifecycleEvent.Resumed (agentId, DateTimeOffset.UtcNow)
        lc |> transition LifecycleState.Running event

    let completeAsync (agentId: AgentId) (output: string) (lc: AgentLifecycle) : Task<AgentLifecycle> =
        task {
            let event = LifecycleEvent.Completed (agentId, output, DateTimeOffset.UtcNow)
            let updated = lc |> transition LifecycleState.Completed event
            for hook in lc.Hooks do
                do! hook.OnCompleted agentId output
            return updated
        }

    let failAsync (agentId: AgentId) (error: exn) (lc: AgentLifecycle) : Task<AgentLifecycle> =
        task {
            let event = LifecycleEvent.Failed (agentId, error.Message, DateTimeOffset.UtcNow)
            let updated = lc |> transition (LifecycleState.Failed error.Message) event
            for hook in lc.Hooks do
                do! hook.OnFailed agentId error
            return updated
        }

    let terminate (agentId: AgentId) (reason: string) (lc: AgentLifecycle) : AgentLifecycle =
        let event = LifecycleEvent.Terminated (agentId, reason, DateTimeOffset.UtcNow)
        lc |> transition LifecycleState.Terminated event

/// No-op lifecycle hook for default behavior
type PassthroughHook() =
    interface ILifecycleHook with
        member _.OnBeforeInit _ = Task.FromResult(Ok ())
        member _.OnAfterInit _ = Task.FromResult(())
        member _.OnBeforeStep _ input = Task.FromResult(Ok input)
        member _.OnAfterStep _ _ = Task.FromResult(())
        member _.OnCompleted _ _ = Task.FromResult(())
        member _.OnFailed _ _ = Task.FromResult(())
