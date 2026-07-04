namespace Nao.Runtime.Orleans

open System
open System.IO

/// Single source of truth for mapping a session grain key to its on-disk directory.
///
/// A session key is a '/'-separated path:
///   "userId/sessionId"                  → a primary (top-level) session
///   "userId/sessionId/taskId"           → the sub-session a task spawned
///   "userId/sessionId/taskId/taskId2"   → a sub-session of that sub-session (recursive)
///
/// The first two segments name the primary session folder ("userId_sessionId"). Every
/// further segment is a task id, so its sub-session nests beneath the task that started it:
///   sessions/userId_sessionId/tasks/<taskId>/sessions/<taskId>/
/// keeping a task's own metadata (tasks/<taskId>/meta.json) and the session it spawned
/// (tasks/<taskId>/sessions/...) together under one folder. Every storage subsystem
/// (conversations, tasks, files, observability, feedback) resolves paths through this
/// module so all of a session's data shares one parent folder, nested identically.
module SessionPaths =

    /// Make a single key segment safe to use as a folder name.
    let sanitizeSegment (s: string) =
        (if isNull s then "" else s)
        |> String.map (fun c -> if Char.IsLetterOrDigit c || c = '-' || c = '_' then c else '_')

    /// Split a session key into its sanitized, non-empty segments.
    let segments (sessionKey: string) =
        (if isNull sessionKey then "" else sessionKey).Split('/')
        |> Array.filter (fun s -> not (String.IsNullOrWhiteSpace s))
        |> Array.map sanitizeSegment

    /// Resolve a session key to its data directory under `sessionsRoot`, nesting each
    /// task-spawned sub-session beneath `tasks/<taskId>/sessions/`.
    let sessionDir (sessionsRoot: string) (sessionKey: string) =
        match segments sessionKey with
        | [||] -> sessionsRoot
        | segs ->
            let baseName = if segs.Length = 1 then segs.[0] else segs.[0] + "_" + segs.[1]
            let mutable dir = Path.Combine(sessionsRoot, baseName)
            for i in 2 .. segs.Length - 1 do
                dir <- Path.Combine(dir, "tasks", segs.[i], "sessions", segs.[i])
            dir
