namespace Nao.Runtime.Orleans

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks

/// On-disk, grain-independent record of an async task. This is the externalized copy of a
/// task's authoritative state, written by the owning task grain so the full task history is
/// readable straight from the filesystem (for tracking/debugging) without activating a grain.
type TaskRecord =
    { TaskId: string
      /// Per-session insertion order, assigned by the store (1-based, monotonic, stable).
      Sequence: int
      /// Parent session key ("userId/sessionId") this task belongs to.
      ParentKey: string
      Kind: string
      Title: string
      /// Serialized executor parameters.
      ParamsJson: string
      Status: string
      Progress: float
      Message: string
      SubSessionKey: string
      ResultSummary: string
      ResultFileIds: string list
      Error: string
      TurnId: string
      CreatedAt: DateTimeOffset
      StartedAt: DateTimeOffset
      CompletedAt: DateTimeOffset
      CancelRequested: bool }

/// Pluggable store for externalized task state, grouped by parent session key.
type ITaskStore =
    /// Insert or replace a task record (rewrites its per-task file and the session index).
    /// The record's `Sequence` is assigned/preserved by the store.
    abstract member SaveAsync: sessionKey: string -> record: TaskRecord -> Task

    /// Load a single task record by id.
    abstract member GetAsync: sessionKey: string -> taskId: string -> Task<TaskRecord option>

    /// List all task records for a session, ordered by Sequence ascending.
    abstract member ListAsync: sessionKey: string -> Task<TaskRecord array>

/// File-based task store.
/// Layout:
///   {baseDir}/
///     {sessionDir}/
///       tasks.json            — index: all task records, ordered by Sequence
///       tasks/
///         {taskId}/
///           meta.json         — one task's full record
///           sessions/         — the sub-session(s) this task spawned (written by their grains)
///
/// {baseDir} is the shared `sessions/` root. Paths resolve through `SessionPaths.sessionDir`
/// so a task's metadata and the sub-session it starts nest together under tasks/<taskId>/,
/// and everything for a session shares one parent folder.
type FileTaskStore(baseDir: string) =

    let gate = obj ()

    let jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = true)
        opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        opts

    let sessionDir (sessionKey: string) = SessionPaths.sessionDir baseDir sessionKey
    let tasksDir (sessionKey: string) = Path.Combine(sessionDir sessionKey, "tasks")
    let indexPath (sessionKey: string) = Path.Combine(sessionDir sessionKey, "tasks.json")
    let taskDir (sessionKey: string) (taskId: string) = Path.Combine(tasksDir sessionKey, SessionPaths.sanitizeSegment taskId)
    let taskPath (sessionKey: string) (taskId: string) = Path.Combine(taskDir sessionKey taskId, "meta.json")

    let readIndex (sessionKey: string) : TaskRecord list =
        let path = indexPath sessionKey
        if File.Exists path then
            try
                match JsonSerializer.Deserialize<TaskRecord array>(File.ReadAllText path, jsonOptions) with
                | null -> []
                | arr -> List.ofArray arr
            with _ -> []
        else []

    let writeIndex (sessionKey: string) (records: TaskRecord list) =
        let ordered = records |> List.sortBy (fun r -> r.Sequence)
        Directory.CreateDirectory(sessionDir sessionKey) |> ignore
        File.WriteAllText(indexPath sessionKey, JsonSerializer.Serialize(List.toArray ordered, jsonOptions))

    interface ITaskStore with
        member _.SaveAsync (sessionKey: string) (record: TaskRecord) : Task =
            lock gate (fun () ->
                let existing = readIndex sessionKey
                // Preserve an existing task's sequence; assign the next one for a new task so
                // ordering on disk is stable across updates.
                let sequence =
                    match existing |> List.tryFind (fun r -> r.TaskId = record.TaskId) with
                    | Some prior when prior.Sequence > 0 -> prior.Sequence
                    | _ ->
                        match existing with
                        | [] -> 1
                        | rs -> (rs |> List.map (fun r -> r.Sequence) |> List.max) + 1
                let record = { record with Sequence = sequence }
                Directory.CreateDirectory(taskDir sessionKey record.TaskId) |> ignore
                File.WriteAllText(taskPath sessionKey record.TaskId, JsonSerializer.Serialize(record, jsonOptions))
                let merged =
                    (existing |> List.filter (fun r -> r.TaskId <> record.TaskId)) @ [ record ]
                writeIndex sessionKey merged
                Task.CompletedTask)

        member _.GetAsync (sessionKey: string) (taskId: string) : Task<TaskRecord option> =
            lock gate (fun () ->
                let path = taskPath sessionKey taskId
                let result =
                    if File.Exists path then
                        try Some(JsonSerializer.Deserialize<TaskRecord>(File.ReadAllText path, jsonOptions))
                        with _ -> None
                    else None
                Task.FromResult result)

        member _.ListAsync(sessionKey: string) : Task<TaskRecord array> =
            lock gate (fun () ->
                readIndex sessionKey
                |> List.sortBy (fun r -> r.Sequence)
                |> List.toArray
                |> Task.FromResult)
