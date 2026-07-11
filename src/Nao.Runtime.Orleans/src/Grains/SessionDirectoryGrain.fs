namespace Nao.Runtime.Orleans.Grains

open System
open System.Threading.Tasks
open Orleans
open Orleans.Runtime

/// A lightweight entry tracking a session in the directory
[<GenerateSerializer>]
type SessionDirectoryEntry() =
    [<Id(0u)>] member val SessionId: string = "" with get, set
    [<Id(1u)>] member val AgentName: string = "" with get, set
    [<Id(2u)>] member val Title: string = "" with get, set
    [<Id(3u)>] member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(4u)>] member val LastActiveAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(5u)>] member val IsActive: bool = true with get, set

/// Persistent state for the session directory grain
[<GenerateSerializer>]
type SessionDirectoryState() =
    [<Id(0u)>] member val Entries: ResizeArray<SessionDirectoryEntry> = ResizeArray() with get, set

/// Per-user session directory. Grain key = userId.
/// Tracks which sessions exist so they can be enumerated after restart.
type ISessionDirectoryGrain =
    inherit IGrainWithStringKey

    /// Register a new session in the directory
    abstract member RegisterAsync: entry: SessionDirectoryEntry -> Task

    /// Update the last-active timestamp and title for a session
    abstract member TouchAsync: sessionId: string -> title: string -> Task

    /// Mark a session as inactive (paused)
    abstract member MarkInactiveAsync: sessionId: string -> Task

    /// Mark a session as active (resumed)
    abstract member MarkActiveAsync: sessionId: string -> Task

    /// Remove a session from the directory (on destroy)
    abstract member RemoveAsync: sessionId: string -> Task

    /// List all sessions (active and inactive)
    abstract member ListAllAsync: unit -> Task<SessionDirectoryEntry array>

    /// List only active sessions
    abstract member ListActiveAsync: unit -> Task<SessionDirectoryEntry array>

/// Per-user session directory grain implementation
type SessionDirectoryGrain
    (
        [<PersistentState("sessionDirectory", "sessionStore")>] persistentState: IPersistentState<SessionDirectoryState>
    ) =
    inherit Grain()

    interface ISessionDirectoryGrain with
        member _.RegisterAsync(entry: SessionDirectoryEntry) =
            task {
                // Don't add duplicates
                let exists =
                    persistentState.State.Entries
                    |> Seq.exists (fun e -> e.SessionId = entry.SessionId)
                if not exists then
                    persistentState.State.Entries.Add(entry)
                    do! persistentState.WriteStateAsync()
            }

        member _.TouchAsync (sessionId: string) (title: string) =
            task {
                let entry =
                    persistentState.State.Entries
                    |> Seq.tryFind (fun e -> e.SessionId = sessionId)
                match entry with
                | Some e ->
                    e.LastActiveAt <- DateTimeOffset.UtcNow
                    if not (System.String.IsNullOrEmpty title) then
                        e.Title <- title
                    do! persistentState.WriteStateAsync()
                | None -> ()
            }

        member _.MarkInactiveAsync(sessionId: string) =
            task {
                let entry =
                    persistentState.State.Entries
                    |> Seq.tryFind (fun e -> e.SessionId = sessionId)
                match entry with
                | Some e ->
                    e.IsActive <- false
                    do! persistentState.WriteStateAsync()
                | None -> ()
            }

        member _.MarkActiveAsync(sessionId: string) =
            task {
                let entry =
                    persistentState.State.Entries
                    |> Seq.tryFind (fun e -> e.SessionId = sessionId)
                match entry with
                | Some e ->
                    e.IsActive <- true
                    e.LastActiveAt <- DateTimeOffset.UtcNow
                    do! persistentState.WriteStateAsync()
                | None -> ()
            }

        member _.RemoveAsync(sessionId: string) =
            task {
                let idx =
                    persistentState.State.Entries
                    |> Seq.tryFindIndex (fun e -> e.SessionId = sessionId)
                match idx with
                | Some i ->
                    persistentState.State.Entries.RemoveAt(i)
                    do! persistentState.WriteStateAsync()
                | None -> ()
            }

        member _.ListAllAsync() =
            persistentState.State.Entries
            |> Seq.sortByDescending (fun e -> e.LastActiveAt)
            |> Seq.toArray
            |> Task.FromResult

        member _.ListActiveAsync() =
            persistentState.State.Entries
            |> Seq.filter (fun e -> e.IsActive)
            |> Seq.sortByDescending (fun e -> e.LastActiveAt)
            |> Seq.toArray
            |> Task.FromResult
