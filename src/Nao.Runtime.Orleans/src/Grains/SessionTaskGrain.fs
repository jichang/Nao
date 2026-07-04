namespace Nao.Runtime.Orleans.Grains

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Nao.Runtime.Orleans

/// Grain that owns and runs a single async task. Key = "userId/sessionId/taskId".
///
/// Lifecycle:
/// 1. `StartAsync` persists the spec, marks the task running, and fire-and-forget
///    dispatches `RunAsync` on this same grain (so the caller is never blocked).
/// 2. `RunAsync` resolves an `ITaskExecutor` by kind and runs the work, pushing progress
///    and the terminal status back to the parent session grain.
/// 3. On activation, an interrupted (`running`/`pending`) task is re-dispatched to resume.
///
/// The work is kept behind a serializable params bag (`ParamsJson`) rather than an
/// in-process closure, which is what makes resume-after-restart possible.
type SessionTaskGrain
    (
        [<PersistentState("taskState", "sessionStore")>] state: IPersistentState<SessionTaskState>,
        executors: IEnumerable<ITaskExecutor>,
        taskStore: ITaskStore,
        grainFactory: IGrainFactory
    ) =
    inherit Grain()

    /// Guards against double-dispatch of the work within a single activation.
    let mutable inFlight = false

    let parent () = grainFactory.GetGrain<ISessionGrain>(state.State.ParentKey)

    let toRef () : TaskRef =
        TaskRef(
            TaskId = state.State.TaskId,
            Kind = state.State.Kind,
            Title = state.State.Title,
            Status = state.State.Status,
            Progress = state.State.Progress,
            Message = state.State.Message,
            SubSessionKey = state.State.SubSessionKey,
            ResultFileIds = ResizeArray(state.State.ResultFileIds),
            Error = state.State.Error,
            TurnId = state.State.TurnId,
            CreatedAt = state.State.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow)

    /// Fire-and-forget push of the current snapshot to the parent's task list.
    let pushToParent () =
        try (parent().UpdateTaskStatusAsync(toRef())) |> ignore
        with _ -> ()

    /// Externalized, grain-independent snapshot for the on-disk task store.
    let toRecord () : TaskRecord =
        { TaskId = state.State.TaskId
          Sequence = 0
          ParentKey = state.State.ParentKey
          Kind = state.State.Kind
          Title = state.State.Title
          ParamsJson = state.State.ParamsJson
          Status = state.State.Status
          Progress = state.State.Progress
          Message = state.State.Message
          SubSessionKey = state.State.SubSessionKey
          ResultSummary = state.State.ResultSummary
          ResultFileIds = List.ofSeq state.State.ResultFileIds
          Error = state.State.Error
          TurnId = state.State.TurnId
          CreatedAt = state.State.CreatedAt
          StartedAt = state.State.StartedAt
          CompletedAt = state.State.CompletedAt
          CancelRequested = state.State.CancelRequested }

    /// Fire-and-forget mirror of the task's authoritative state to the on-disk store, keyed by
    /// the parent session so it lands under sessions/<parentKey>/tasks/. Best-effort: tracking
    /// must never block or fail the task itself.
    let persistRecord () =
        try
            if not (String.IsNullOrEmpty state.State.ParentKey) then
                taskStore.SaveAsync state.State.ParentKey (toRecord ()) |> ignore
        with _ -> ()

    /// Push the current snapshot to both the parent grain and the on-disk task store.
    let publish () =
        pushToParent ()
        persistRecord ()

    /// Execute the task body once. Safe to call repeatedly (guarded by `inFlight`).
    let runCore () : Task =
        task {
            if inFlight then () else
            inFlight <- true
            try
                state.State.Status <- "running"
                if state.State.StartedAt = DateTimeOffset.MinValue then
                    state.State.StartedAt <- DateTimeOffset.UtcNow
                do! state.WriteStateAsync()
                publish ()

                let report (p: float) (m: string) =
                    state.State.Progress <- max 0.0 (min 1.0 p)
                    state.State.Message <- m
                    publish ()

                try
                    if state.State.CancelRequested then
                        state.State.Status <- "cancelled"
                    else
                        match executors |> Seq.tryFind (fun e -> e.Kind = state.State.Kind) with
                        | None ->
                            state.State.Status <- "failed"
                            state.State.Error <- sprintf "No executor registered for task kind '%s'" state.State.Kind
                        | Some executor ->
                            let ctx =
                                { ParentKey = state.State.ParentKey
                                  SubSessionKey = state.State.SubSessionKey
                                  TaskId = state.State.TaskId
                                  TurnId = state.State.TurnId
                                  ParamsJson = state.State.ParamsJson
                                  GrainFactory = grainFactory
                                  Report = report }
                            let! outcome = executor.ExecuteAsync ctx
                            if state.State.CancelRequested then
                                state.State.Status <- "cancelled"
                            else
                                state.State.Status <- "completed"
                                state.State.Progress <- 1.0
                                state.State.ResultSummary <- outcome.Summary
                                state.State.ResultFileIds <- ResizeArray(outcome.ResultFileIds)
                with ex ->
                    state.State.Status <- "failed"
                    state.State.Error <- ex.Message

                state.State.CompletedAt <- DateTimeOffset.UtcNow
                do! state.WriteStateAsync()
                publish ()
            finally
                inFlight <- false
        }

    override this.OnActivateAsync(cancellationToken: CancellationToken) : Task =
        // Resume a task that was interrupted by a silo restart: re-dispatch the work on a
        // fresh turn (queued until activation completes). A completed/failed/cancelled task
        // is left as-is.
        if (state.State.Status = "running" || state.State.Status = "pending")
           && not (String.IsNullOrEmpty state.State.TaskId) then
            grainFactory.GetGrain<ISessionTaskGrain>(this.GetPrimaryKeyString()).RunAsync() |> ignore
        Task.CompletedTask

    interface ISessionTaskGrain with
        member this.StartAsync(spec: TaskStartSpec) : Task<TaskRef> =
            task {
                let key = this.GetPrimaryKeyString()
                state.State.TaskId <- (if String.IsNullOrEmpty spec.TaskId then key else spec.TaskId)
                state.State.ParentKey <- spec.ParentKey
                state.State.Kind <- spec.Kind
                state.State.Title <- spec.Title
                state.State.ParamsJson <- spec.ParamsJson
                state.State.TurnId <- spec.TurnId
                state.State.SubSessionKey <- key
                state.State.Status <- "running"
                if state.State.CreatedAt = DateTimeOffset.MinValue then
                    state.State.CreatedAt <- DateTimeOffset.UtcNow
                do! state.WriteStateAsync()
                // Dispatch the work without awaiting so the caller returns immediately.
                grainFactory.GetGrain<ISessionTaskGrain>(key).RunAsync() |> ignore
                publish ()
                return toRef ()
            }

        member _.RunAsync() : Task = runCore ()

        member _.GetStatusAsync() : Task<TaskRef> = Task.FromResult(toRef ())

        member _.CancelAsync() : Task =
            // Best-effort: flag cancellation (checked between executor stages) and reflect it
            // to the parent. In-flight harness work finishes but its result is discarded.
            state.State.CancelRequested <- true
            if state.State.Status = "pending" then
                state.State.Status <- "cancelled"
            publish ()
            Task.CompletedTask
