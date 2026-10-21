namespace Nao.Runtime.Orleans.Grains

open System
open System.Threading.Tasks
open Nao.Agents
open Nao.Runtime.Orleans
open Orleans
open Orleans.Runtime

/// A lightweight entry tracking a session in the directory
[<GenerateSerializer>]
type SessionDirectoryEntry() =
    [<Id(0u)>]
    member val SessionId: string = "" with get, set

    [<Id(1u)>]
    member val AgentName: string = "" with get, set

    [<Id(2u)>]
    member val Title: string = "" with get, set

    [<Id(3u)>]
    member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

    [<Id(4u)>]
    member val LastActiveAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

    [<Id(5u)>]
    member val IsActive: bool = true with get, set

    [<Id(6u)>]
    member val TenantId: string = "" with get, set

    [<Id(7u)>]
    member val WorkspaceId: string = "" with get, set

    [<Id(8u)>]
    member val UserId: string = "" with get, set

/// Persistent state for the session directory grain
[<GenerateSerializer>]
type SessionDirectoryState() =
    [<Id(0u)>]
    member val Entries: ResizeArray<SessionDirectoryEntry> = ResizeArray() with get, set

    [<Id(1u)>]
    member val SchemaVersion: int = 0 with get, set

    [<Id(2u)>]
    member val TenantId: string = "" with get, set

    [<Id(3u)>]
    member val WorkspaceId: string = "" with get, set

    [<Id(4u)>]
    member val UserId: string = "" with get, set

/// Tenant/workspace/user-scoped session directory.
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
        [<PersistentState("sessionDirectory", "sessionStore")>] persistentState: IPersistentState<SessionDirectoryState>,
        principalAccessor: System.Func<SecurityPrincipal>
    ) as this =
    inherit Grain()

    let address key =
        RuntimeAddress.tryParseDirectoryKey key
        |> Option.defaultWith (fun () ->
            raise (System.UnauthorizedAccessException "Invalid tenant/workspace/user session-directory key."))

    let requireAddress key =
        let value = address key

        if not (RuntimeAddress.principalMatchesDirectory (principalAccessor.Invoke()) value) then
            raise (
                System.UnauthorizedAccessException "The authenticated principal is not authorized for this directory."
            )

        if
            persistentState.RecordExists
            && not (
                PersistedOwnerScope.matchesDirectory
                    value
                    { TenantId = persistentState.State.TenantId
                      WorkspaceId = persistentState.State.WorkspaceId
                      UserId = persistentState.State.UserId
                      SessionId = None }
            )
        then
            raise (System.UnauthorizedAccessException "Persisted directory owner does not match the grain address.")

        if persistentState.RecordExists then
            let owner = PersistedOwnerScope.directory value

            let entriesMatch =
                persistentState.State.Entries
                |> Seq.forall (fun entry ->
                    entry.TenantId = owner.TenantId
                    && entry.WorkspaceId = owner.WorkspaceId
                    && entry.UserId = owner.UserId
                    && (SessionId.tryParse entry.SessionId |> Option.isSome))

            if not entriesMatch then
                raise (
                    System.UnauthorizedAccessException
                        "A persisted session-directory entry does not match the directory owner."
                )

        value

    let requireCurrentAddress () =
        requireAddress (this.GetPrimaryKeyString())

    override this.OnActivateAsync(cancellationToken) =
        GrainStateVersion.prepare
            GrainStateVersion.DirectoryCurrent
            "Session directory state"
            persistentState.RecordExists
            persistentState.State.SchemaVersion
            (fun version -> persistentState.State.SchemaVersion <- version)

        base.OnActivateAsync(cancellationToken)

    interface ISessionDirectoryGrain with
        member _.RegisterAsync(entry: SessionDirectoryEntry) =
            task {
                let directoryAddress = requireCurrentAddress ()
                let owner = PersistedOwnerScope.directory directoryAddress

                if
                    entry.TenantId <> owner.TenantId
                    || entry.WorkspaceId <> owner.WorkspaceId
                    || entry.UserId <> owner.UserId
                    || SessionId.tryParse entry.SessionId |> Option.isNone
                then
                    raise (System.UnauthorizedAccessException "Session directory entry owner is invalid.")

                if not persistentState.RecordExists then
                    persistentState.State.TenantId <- owner.TenantId
                    persistentState.State.WorkspaceId <- owner.WorkspaceId
                    persistentState.State.UserId <- owner.UserId

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
                requireCurrentAddress () |> ignore

                let entry =
                    persistentState.State.Entries |> Seq.tryFind (fun e -> e.SessionId = sessionId)

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
                requireCurrentAddress () |> ignore

                let entry =
                    persistentState.State.Entries |> Seq.tryFind (fun e -> e.SessionId = sessionId)

                match entry with
                | Some e ->
                    e.IsActive <- false
                    do! persistentState.WriteStateAsync()
                | None -> ()
            }

        member _.MarkActiveAsync(sessionId: string) =
            task {
                requireCurrentAddress () |> ignore

                let entry =
                    persistentState.State.Entries |> Seq.tryFind (fun e -> e.SessionId = sessionId)

                match entry with
                | Some e ->
                    e.IsActive <- true
                    e.LastActiveAt <- DateTimeOffset.UtcNow
                    do! persistentState.WriteStateAsync()
                | None -> ()
            }

        member _.RemoveAsync(sessionId: string) =
            task {
                requireCurrentAddress () |> ignore

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
            requireCurrentAddress () |> ignore

            persistentState.State.Entries
            |> Seq.sortByDescending (fun e -> e.LastActiveAt)
            |> Seq.toArray
            |> Task.FromResult

        member _.ListActiveAsync() =
            requireCurrentAddress () |> ignore

            persistentState.State.Entries
            |> Seq.filter (fun e -> e.IsActive)
            |> Seq.sortByDescending (fun e -> e.LastActiveAt)
            |> Seq.toArray
            |> Task.FromResult
