namespace Nao.Persistence

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

module private MemoryOperations =
    let ownerKey = AuthorizationScope.identity >> AuthorizationIdentity.storageKey

    let private failure (error: exn) =
        PlatformFailure.fromException PlatformFailureBoundary.Storage None error

    let protect operation =
        task {
            try
                let! count = operation ()
                return Ok count
            with
            | :? IOException as error -> return Error(failure error)
            | :? InvalidDataException as error -> return Error(failure error)
            | :? DbException as error -> return Error(failure error)
            | :? UnauthorizedAccessException as error -> return Error(failure error)
        }

/// In-memory memory store for testing and simple scenarios.
module InMemoryStore =
    let create () : MemoryStore =
        let store = System.Collections.Generic.Dictionary<string, MemoryEntry list>()
        let sync = obj ()

        let entries key =
            match store.TryGetValue key with
            | true, entries -> entries
            | false, _ -> []

        { SaveAsync =
            fun scope (entry: MemoryEntry) ->
                let key = MemoryOperations.ownerKey scope

                lock sync (fun () -> store.[key] <- entry :: (entries key |> List.filter (fun e -> e.Key <> entry.Key)))

                task { return () }
          RecallAsync =
            fun scope (queryKey: string) ->
                lock sync (fun () ->
                    entries (MemoryOperations.ownerKey scope)
                    |> List.filter (fun e -> e.Key.Contains(queryKey, StringComparison.OrdinalIgnoreCase)))
                |> Task.FromResult
          RecallAllAsync =
            fun scope ->
                lock sync (fun () -> entries (MemoryOperations.ownerKey scope))
                |> Task.FromResult
          ForgetAsync =
            fun scope (entryKey: string) ->
                let key = MemoryOperations.ownerKey scope

                lock sync (fun () -> store.[key] <- entries key |> List.filter (fun e -> e.Key <> entryKey))

                task { return () }
          DeleteOwnerAsync =
            fun scope ->
                MemoryOperations.protect (fun () ->
                    lock sync (fun () ->
                        let key = MemoryOperations.ownerKey scope
                        let count = (entries key).Length
                        store.Remove key |> ignore
                        count)
                    |> Task.FromResult)
          DeleteExpiredAsync =
            fun scope before ->
                MemoryOperations.protect (fun () ->
                    lock sync (fun () ->
                        let key = MemoryOperations.ownerKey scope
                        let existing = entries key
                        let retained = existing |> List.filter (fun entry -> entry.Timestamp >= before)
                        store.[key] <- retained
                        existing.Length - retained.Length)
                    |> Task.FromResult) }

/// ADO.NET-backed memory store. Provider-agnostic: works with any database
/// reachable through a DbConnectionFactory.
module AdoMemoryStore =
    let create (factory: DbConnectionFactory) : MemoryStore =
        let ensureAsync () =
            task {
                do!
                    AdoSchema.ensureVersionedTable
                        factory
                        "memory"
                        "nao_memory"
                        "CREATE TABLE IF NOT EXISTS nao_memory (owner_key TEXT NOT NULL, mem_key TEXT NOT NULL, mem_value TEXT NOT NULL, mem_ts TEXT NOT NULL, mem_tags TEXT NOT NULL, PRIMARY KEY (owner_key, mem_key))"

                try
                    let! _ =
                        Ado.query
                            factory
                            "SELECT owner_key, mem_key, mem_value, mem_ts, mem_tags FROM nao_memory WHERE 1 = 0"
                            []
                            ignore

                    return ()
                with :? DbException as ex ->
                    return
                        raise (
                            InvalidDataException(
                                "Memory table has an incompatible shape. Follow docs/migrations before accessing or mutating this database.",
                                ex
                            )
                        )
            }

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

        let loadAll scope : Task<MemoryEntry list> =
            Ado.query
                factory
                "SELECT mem_key, mem_value, mem_ts, mem_tags FROM nao_memory WHERE owner_key = @a ORDER BY mem_ts DESC"
                [ "@a", box (MemoryOperations.ownerKey scope) ]
                mapEntry

        let validateAsync scope =
            task {
                do! ensureAsync ()
                let! _ = loadAll scope
                return ()
            }

        let forgetAsync scope key =
            task {
                do! validateAsync scope

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "DELETE FROM nao_memory WHERE owner_key = @a AND mem_key = @k"
                        [ "@a", box (MemoryOperations.ownerKey scope); "@k", box key ]

                return ()
            }

        { SaveAsync =
            fun scope (entry: MemoryEntry) ->
                task {
                    do! validateAsync scope

                    do!
                        Ado.executeTransaction
                            factory
                            [ "DELETE FROM nao_memory WHERE owner_key = @a AND mem_key = @k",
                              [ "@a", box (MemoryOperations.ownerKey scope); "@k", box entry.Key ]
                              "INSERT INTO nao_memory (owner_key, mem_key, mem_value, mem_ts, mem_tags) VALUES (@a, @k, @v, @t, @g)",
                              [ "@a", box (MemoryOperations.ownerKey scope)
                                "@k", box entry.Key
                                "@v", box entry.Value
                                "@t", box (Time.toIso entry.Timestamp)
                                "@g", box (Json.tagsToJson entry.Tags) ] ]
                }
          RecallAsync =
            fun scope query ->
                task {
                    do! ensureAsync ()
                    let! all = loadAll scope

                    return
                        all
                        |> List.filter (fun e -> e.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
                }
          RecallAllAsync =
            fun scope ->
                task {
                    do! ensureAsync ()
                    return! loadAll scope
                }
          ForgetAsync = forgetAsync
          DeleteOwnerAsync =
            fun owner ->
                MemoryOperations.protect (fun () ->
                    task {
                        do! validateAsync owner

                        return!
                            Ado.executeNonQuery
                                factory
                                "DELETE FROM nao_memory WHERE owner_key = @a"
                                [ "@a", box (MemoryOperations.ownerKey owner) ]
                    })
          DeleteExpiredAsync =
            fun owner before ->
                MemoryOperations.protect (fun () ->
                    task {
                        do! validateAsync owner

                        return!
                            Ado.executeNonQuery
                                factory
                                "DELETE FROM nao_memory WHERE owner_key = @a AND mem_ts < @before"
                                [ "@a", box (MemoryOperations.ownerKey owner)
                                  "@before", box (Time.toIso before) ]
                    }) }

/// FileSystem-backed memory store. One JSON document per authorization identity under {baseDir}.
module FileMemoryStore =
    [<Literal>]
    let private CurrentSchemaVersion = 1

    let create (baseDir: string) : MemoryStore =
        let sync = obj ()

        let scopeFile scope =
            Path.Combine(baseDir, sprintf "%s.json" (MemoryOperations.ownerKey scope))

        let load scope : Dto.MemoryEntryDto list =
            VersionedFileJson.read<Dto.MemoryEntryDto list> "Memory document" CurrentSchemaVersion (scopeFile scope) []

        let save scope entries =
            VersionedFileJson.write CurrentSchemaVersion (scopeFile scope) entries

        { SaveAsync =
            fun scope (entry: MemoryEntry) ->
                task {
                    lock sync (fun () ->
                        save
                            scope
                            (Dto.toMemoryDto entry
                             :: (load scope |> List.filter (fun e -> e.Key <> entry.Key))))
                }
          RecallAsync =
            fun scope query ->
                task {
                    return
                        lock sync (fun () ->
                            load scope
                            |> List.map Dto.ofMemoryDto
                            |> List.filter (fun e -> e.Key.Contains(query, StringComparison.OrdinalIgnoreCase)))
                }
          RecallAllAsync = fun scope -> task { return lock sync (fun () -> load scope |> List.map Dto.ofMemoryDto) }
          ForgetAsync =
            fun scope key ->
                task { lock sync (fun () -> save scope (load scope |> List.filter (fun e -> e.Key <> key))) }
          DeleteOwnerAsync =
            fun owner ->
                MemoryOperations.protect (fun () ->
                    task {
                        return
                            lock sync (fun () ->
                                let entries = load owner
                                let file = scopeFile owner

                                if File.Exists file then
                                    File.Delete file

                                entries.Length)
                    })
          DeleteExpiredAsync =
            fun owner before ->
                MemoryOperations.protect (fun () ->
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
