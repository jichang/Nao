namespace Nao.Runtime.Orleans.Grains

open System
open System.Threading.Tasks
open Orleans
open Orleans.Concurrency

/// Lightweight snapshot of an async task, tracked in the parent session's grain state and
/// returned to clients. The authoritative copy lives in the owning `ISessionTaskGrain`.
[<GenerateSerializer>]
type TaskRef() =
    [<Id(0u)>] member val TaskId: string = "" with get, set
    /// Executor kind, e.g. "agent" | "document-conversion".
    [<Id(1u)>] member val Kind: string = "" with get, set
    [<Id(2u)>] member val Title: string = "" with get, set
    /// "pending" | "running" | "completed" | "failed" | "cancelled".
    [<Id(3u)>] member val Status: string = "pending" with get, set
    [<Id(4u)>] member val Progress: float = 0.0 with get, set
    [<Id(5u)>] member val Message: string = "" with get, set
    /// Key of the sub-session conversation this task drives ("" for non-conversation tasks).
    [<Id(6u)>] member val SubSessionKey: string = "" with get, set
    /// Ids of files produced by the task, stored in the parent session's file store.
    [<Id(7u)>] member val ResultFileIds: ResizeArray<string> = ResizeArray() with get, set
    [<Id(8u)>] member val Error: string = "" with get, set
    /// Turn that launched the task (so the UI can attach the chip to the right message).
    [<Id(9u)>] member val TurnId: string = "" with get, set
    [<Id(10u)>] member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(11u)>] member val UpdatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

/// Persistent state for a single async task grain.
[<GenerateSerializer>]
type SessionTaskState() =
    [<Id(0u)>] member val TaskId: string = "" with get, set
    [<Id(1u)>] member val ParentKey: string = "" with get, set
    [<Id(2u)>] member val Kind: string = "" with get, set
    [<Id(3u)>] member val Title: string = "" with get, set
    /// Serialized executor parameters — the replay data that makes a task resumable.
    [<Id(4u)>] member val ParamsJson: string = "" with get, set
    [<Id(5u)>] member val Status: string = "" with get, set
    [<Id(6u)>] member val Progress: float = 0.0 with get, set
    [<Id(7u)>] member val Message: string = "" with get, set
    [<Id(8u)>] member val SubSessionKey: string = "" with get, set
    [<Id(9u)>] member val ResultSummary: string = "" with get, set
    [<Id(10u)>] member val ResultFileIds: ResizeArray<string> = ResizeArray() with get, set
    [<Id(11u)>] member val Error: string = "" with get, set
    [<Id(12u)>] member val TurnId: string = "" with get, set
    [<Id(13u)>] member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(14u)>] member val StartedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(15u)>] member val CompletedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(16u)>] member val CancelRequested: bool = false with get, set

/// Arguments to start a task on its grain.
[<GenerateSerializer>]
type TaskStartSpec() =
    [<Id(0u)>] member val TaskId: string = "" with get, set
    [<Id(1u)>] member val ParentKey: string = "" with get, set
    [<Id(2u)>] member val Kind: string = "" with get, set
    [<Id(3u)>] member val Title: string = "" with get, set
    [<Id(4u)>] member val ParamsJson: string = "" with get, set
    [<Id(5u)>] member val TurnId: string = "" with get, set

/// Outcome returned by a task executor.
type TaskOutcome =
    { Summary: string
      ResultFileIds: string list }

/// Context handed to a task executor. These run in-process on the silo, so this type is
/// not Orleans-serialized. `Report progress message` pushes progress (0..1) updates.
type TaskExecutionContext =
    { ParentKey: string
      SubSessionKey: string
      TaskId: string
      TurnId: string
      ParamsJson: string
      GrainFactory: IGrainFactory
      Report: float -> string -> unit }

/// Pluggable executor for one task `Kind`. Implementations are registered in DI and
/// selected by kind. Work is kept behind a serializable params bag (not an in-process
/// closure) so a task can be replayed by its grain after a silo restart.
type ITaskExecutor =
    abstract member Kind: string
    abstract member ExecuteAsync: ctx: TaskExecutionContext -> Task<TaskOutcome>

/// Grain that owns and runs a single async task. Key = "userId/sessionId/taskId" — the
/// same string key as the sub-session `SessionGrain` it may drive (a different grain type).
type ISessionTaskGrain =
    inherit IGrainWithStringKey

    /// Persist the task spec, mark it running, and dispatch the work in the background.
    abstract member StartAsync: spec: TaskStartSpec -> Task<TaskRef>

    /// Execute the task body. Dispatched as a fire-and-forget self-call by `StartAsync`
    /// (and re-dispatched on activation to resume an interrupted task).
    abstract member RunAsync: unit -> Task

    /// Current task snapshot. Reentrant so it can be polled while the task runs.
    [<AlwaysInterleave>]
    abstract member GetStatusAsync: unit -> Task<TaskRef>

    /// Request best-effort cancellation. Reentrant so it can interrupt a running task.
    [<AlwaysInterleave>]
    abstract member CancelAsync: unit -> Task
