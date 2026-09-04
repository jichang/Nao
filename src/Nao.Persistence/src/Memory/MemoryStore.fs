namespace Nao.Persistence

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

module private MemoryOperations =
    let private failure =
        PlatformFailure.fromException PlatformFailureBoundary.Storage None

    let protect owner operation =
        task {
            if String.IsNullOrWhiteSpace owner then
                return
                    Error(
                        PlatformFailure.create
                            PlatformErrorCategory.InvalidInput
                            "Memory owner cannot be blank."
                            false
                            None
                    )
            else
                try
                    let! count = operation ()
                    return Ok count
                with ex ->
                    return Error(failure ex)
        }

/// In-memory memory store for testing and simple scenarios.
module InMemoryStore =
    let create () : MemoryStore =
        let store =
            System.Collections.Concurrent.ConcurrentDictionary<string, MemoryEntry list>()

        let agentKey (agentId: string) = agentId

        { SaveAsync =
            fun (agentId: string) (entry: MemoryEntry) ->
                let key = agentKey agentId

                store.AddOrUpdate(
                    key,
                    [ entry ],
                    fun _ existing ->
                        let filtered = existing |> List.filter (fun e -> e.Key <> entry.Key)
                        entry :: filtered
                )
                |> ignore

                task { return () }
          RecallAsync =
            fun (agentId: string) (queryKey: string) ->
                let key = agentKey agentId

                match store.TryGetValue(key) with
                | true, entries ->
                    entries
                    |> List.filter (fun e -> e.Key.Contains(queryKey, StringComparison.OrdinalIgnoreCase))
                    |> Task.FromResult
                | false, _ -> Task.FromResult([])
          RecallAllAsync =
            fun (agentId: string) ->
                let key = agentKey agentId

                match store.TryGetValue(key) with
                | true, entries -> Task.FromResult(entries)
                | false, _ -> Task.FromResult([])
          ForgetAsync =
            fun (agentId: string) (entryKey: string) ->
                let key = agentKey agentId

                match store.TryGetValue(key) with
                | true, entries ->
                    let filtered = entries |> List.filter (fun e -> e.Key <> entryKey)
                    store.[key] <- filtered
                | false, _ -> ()

                task { return () }
          DeleteOwnerAsync =
            fun owner ->
                MemoryOperations.protect owner (fun () ->
                    match store.TryRemove(agentKey owner) with
                    | true, entries -> Task.FromResult entries.Length
                    | false, _ -> Task.FromResult 0)
          DeleteExpiredAsync =
            fun owner before ->
                MemoryOperations.protect owner (fun () ->
                    let key = agentKey owner

                    match store.TryGetValue key with
                    | true, entries ->
                        let retained = entries |> List.filter (fun entry -> entry.Timestamp >= before)
                        store.[key] <- retained
                        Task.FromResult(entries.Length - retained.Length)
                    | false, _ -> Task.FromResult 0) }

/// ADO.NET-backed memory store. Provider-agnostic: works with any database
/// reachable through a DbConnectionFactory.
module AdoMemoryStore =
    let create (factory: DbConnectionFactory) : MemoryStore =
        let ensureAsync () =
            AdoSchema.ensureVersionedTable
                factory
                "memory"
                "nao_memory"
                "CREATE TABLE IF NOT EXISTS nao_memory (agent TEXT NOT NULL, mem_key TEXT NOT NULL, mem_value TEXT NOT NULL, mem_ts TEXT NOT NULL, mem_tags TEXT NOT NULL, PRIMARY KEY (agent, mem_key))"

        let mapEntry (r: DbDataReader) : MemoryEntry =
            let key = Ado.getString r "mem_key"

            try
                { Key = key
                  Value = Ado.getString r "mem_value"
                  Timestamp = Time.fromIso (Ado.getString r "mem_ts")
                  Tags = Json.tagsFromJson (Ado.getString r "mem_tags") }
            with ex ->
                raise (
                    InvalidDataException(
                        sprintf "Memory row '%s' is invalid. Follow docs/migrations before writing." key,
                        ex
                    )
                )

        let loadAll (agent: string) : Task<MemoryEntry list> =
            Ado.query
                factory
                "SELECT mem_key, mem_value, mem_ts, mem_tags FROM nao_memory WHERE agent = @a ORDER BY mem_ts DESC"
                [ "@a", box agent ]
                mapEntry

        let validateAsync () =
            task {
                do! ensureAsync ()

                let! _ = Ado.query factory "SELECT mem_key, mem_value, mem_ts, mem_tags FROM nao_memory" [] mapEntry

                return ()
            }

        let forgetAsync agentId key =
            task {
                do! validateAsync ()

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "DELETE FROM nao_memory WHERE agent = @a AND mem_key = @k"
                        [ "@a", box agentId; "@k", box key ]

                return ()
            }

        { SaveAsync =
            fun (agentId: string) (entry: MemoryEntry) ->
                task {
                    do! validateAsync ()

                    do!
                        Ado.executeTransaction
                            factory
                            [ "DELETE FROM nao_memory WHERE agent = @a AND mem_key = @k",
                              [ "@a", box agentId; "@k", box entry.Key ]
                              "INSERT INTO nao_memory (agent, mem_key, mem_value, mem_ts, mem_tags) VALUES (@a, @k, @v, @t, @g)",
                              [ "@a", box agentId
                                "@k", box entry.Key
                                "@v", box entry.Value
                                "@t", box (Time.toIso entry.Timestamp)
                                "@g", box (Json.tagsToJson entry.Tags) ] ]
                }
          RecallAsync =
            fun agentId query ->
                task {
                    do! ensureAsync ()
                    let! all = loadAll agentId

                    return
                        all
                        |> List.filter (fun e -> e.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
                }
          RecallAllAsync =
            fun agentId ->
                task {
                    do! ensureAsync ()
                    return! loadAll agentId
                }
          ForgetAsync = forgetAsync
          DeleteOwnerAsync =
            fun owner ->
                MemoryOperations.protect owner (fun () ->
                    task {
                        do! validateAsync ()

                        return!
                            Ado.executeNonQuery factory "DELETE FROM nao_memory WHERE agent = @a" [ "@a", box owner ]
                    })
          DeleteExpiredAsync =
            fun owner before ->
                MemoryOperations.protect owner (fun () ->
                    task {
                        do! validateAsync ()

                        return!
                            Ado.executeNonQuery
                                factory
                                "DELETE FROM nao_memory WHERE agent = @a AND mem_ts < @before"
                                [ "@a", box owner; "@before", box (Time.toIso before) ]
                    }) }

/// FileSystem-backed memory store. One JSON document per agent under {baseDir}.
module FileMemoryStore =
    [<Literal>]
    let private CurrentSchemaVersion = 1

    let create (baseDir: string) : MemoryStore =
        let sync = obj ()

        let agentFile agentId =
            Path.Combine(baseDir, sprintf "%s.json" (Sanitize.id agentId))

        let load agentId : Dto.MemoryEntryDto list =
            VersionedFileJson.read<Dto.MemoryEntryDto list>
                "Memory document"
                CurrentSchemaVersion
                (agentFile agentId)
                []

        let save agentId entries =
            VersionedFileJson.write CurrentSchemaVersion (agentFile agentId) entries

        { SaveAsync =
            fun (agentId: string) (entry: MemoryEntry) ->
                task {
                    lock sync (fun () ->
                        save
                            agentId
                            (Dto.toMemoryDto entry
                             :: (load agentId |> List.filter (fun e -> e.Key <> entry.Key))))
                }
          RecallAsync =
            fun agentId query ->
                task {
                    return
                        lock sync (fun () ->
                            load agentId
                            |> List.map Dto.ofMemoryDto
                            |> List.filter (fun e -> e.Key.Contains(query, StringComparison.OrdinalIgnoreCase)))
                }
          RecallAllAsync = fun agentId -> task { return lock sync (fun () -> load agentId |> List.map Dto.ofMemoryDto) }
          ForgetAsync =
            fun agentId key ->
                task { lock sync (fun () -> save agentId (load agentId |> List.filter (fun e -> e.Key <> key))) }
          DeleteOwnerAsync =
            fun owner ->
                MemoryOperations.protect owner (fun () ->
                    task {
                        return
                            lock sync (fun () ->
                                let entries = load owner
                                let file = agentFile owner

                                if File.Exists file then
                                    File.Delete file

                                entries.Length)
                    })
          DeleteExpiredAsync =
            fun owner before ->
                MemoryOperations.protect owner (fun () ->
                    task {
                        return
                            lock sync (fun () ->
                                let entries = load owner

                                let retained =
                                    entries
                                    |> List.filter (Dto.ofMemoryDto >> fun entry -> entry.Timestamp >= before)

                                save owner retained
                                entries.Length - retained.Length)
                    }) }

/// Factory helpers for memory store implementations.
module MemoryStores =
    /// ADO.NET-backed store over any provider supplied via the connection factory.
    let ado (factory: DbConnectionFactory) : MemoryStore = AdoMemoryStore.create factory

    /// FileSystem-backed store rooted at the given directory.
    let file (baseDir: string) : MemoryStore = FileMemoryStore.create baseDir
