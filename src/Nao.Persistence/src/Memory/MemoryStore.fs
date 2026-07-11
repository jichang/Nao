namespace Nao.Persistence

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

/// In-memory implementation of IMemoryStore for testing and simple scenarios
type InMemoryStore() =
    let store = System.Collections.Concurrent.ConcurrentDictionary<string, MemoryEntry list>()

    let agentKey (agentId: AgentId) = agentId.Name

    interface IMemoryStore with
        member _.SaveAsync (agentId: AgentId) (entry: MemoryEntry) =
            let key = agentKey agentId
            store.AddOrUpdate(
                key,
                [ entry ],
                fun _ existing ->
                    let filtered = existing |> List.filter (fun e -> e.Key <> entry.Key)
                    entry :: filtered)
            |> ignore
            task { return () }

        member _.RecallAsync (agentId: AgentId) (queryKey: string) =
            let key = agentKey agentId
            match store.TryGetValue(key) with
            | true, entries ->
                entries
                |> List.filter (fun e -> e.Key.Contains(queryKey, StringComparison.OrdinalIgnoreCase))
                |> Task.FromResult
            | false, _ -> Task.FromResult([])

        member _.RecallAllAsync (agentId: AgentId) =
            let key = agentKey agentId
            match store.TryGetValue(key) with
            | true, entries -> Task.FromResult(entries)
            | false, _ -> Task.FromResult([])

        member _.ForgetAsync (agentId: AgentId) (entryKey: string) =
            let key = agentKey agentId
            match store.TryGetValue(key) with
            | true, entries ->
                let filtered = entries |> List.filter (fun e -> e.Key <> entryKey)
                store.[key] <- filtered
            | false, _ -> ()
            task { return () }

        member _.ClearAsync (agentId: AgentId) =
            let key = agentKey agentId
            store.TryRemove(key) |> ignore
            task { return () }

/// ADO.NET-backed IMemoryStore. Provider-agnostic: works with any database
/// reachable through an IDbConnectionFactory.
type AdoMemoryStore(factory: IDbConnectionFactory) =

    let ensureAsync () =
        Ado.executeNonQuery
            factory
            "CREATE TABLE IF NOT EXISTS nao_memory (\
                agent TEXT NOT NULL, \
                mem_key TEXT NOT NULL, \
                mem_value TEXT NOT NULL, \
                mem_ts TEXT NOT NULL, \
                mem_tags TEXT NOT NULL, \
                PRIMARY KEY (agent, mem_key))"
            []
        :> Task

    let mapEntry (r: DbDataReader) : MemoryEntry =
        { Key = Ado.getString r "mem_key"
          Value = Ado.getString r "mem_value"
          Timestamp = Time.fromIso (Ado.getString r "mem_ts")
          Tags = Json.tagsFromJson (Ado.getString r "mem_tags") }

    let loadAll (agent: string) : Task<MemoryEntry list> =
        Ado.query
            factory
            "SELECT mem_key, mem_value, mem_ts, mem_tags FROM nao_memory WHERE agent = @a ORDER BY mem_ts DESC"
            [ "@a", box agent ]
            mapEntry

    interface IMemoryStore with
        member _.SaveAsync (agentId: AgentId) (entry: MemoryEntry) =
            task {
                do! ensureAsync ()
                let agent = agentId.Name
                do!
                    Ado.executeTransaction
                        factory
                        [ "DELETE FROM nao_memory WHERE agent = @a AND mem_key = @k",
                          [ "@a", box agent; "@k", box entry.Key ]
                          "INSERT INTO nao_memory (agent, mem_key, mem_value, mem_ts, mem_tags) VALUES (@a, @k, @v, @t, @g)",
                          [ "@a", box agent
                            "@k", box entry.Key
                            "@v", box entry.Value
                            "@t", box (Time.toIso entry.Timestamp)
                            "@g", box (Json.tagsToJson entry.Tags) ] ]
            }

        member _.RecallAsync (agentId: AgentId) (query: string) =
            task {
                do! ensureAsync ()
                let! all = loadAll agentId.Name
                return
                    all
                    |> List.filter (fun e -> e.Key.Contains(query, System.StringComparison.OrdinalIgnoreCase))
            }

        member _.RecallAllAsync(agentId: AgentId) =
            task {
                do! ensureAsync ()
                return! loadAll agentId.Name
            }

        member _.ForgetAsync (agentId: AgentId) (key: string) =
            task {
                do! ensureAsync ()
                let! _ =
                    Ado.executeNonQuery
                        factory
                        "DELETE FROM nao_memory WHERE agent = @a AND mem_key = @k"
                        [ "@a", box agentId.Name; "@k", box key ]
                return ()
            }

        member _.ClearAsync(agentId: AgentId) =
            task {
                do! ensureAsync ()
                let! _ =
                    Ado.executeNonQuery factory "DELETE FROM nao_memory WHERE agent = @a" [ "@a", box agentId.Name ]
                return ()
            }

/// FileSystem-backed IMemoryStore. One JSON document per agent under {baseDir}.
type FileMemoryStore(baseDir: string) =
    let sync = obj ()

    let agentFile (agentId: AgentId) =
        Path.Combine(baseDir, sprintf "%s.json" (Sanitize.id agentId.Name))

    let load (agentId: AgentId) : Dto.MemoryEntryDto list =
        FileJson.read<Dto.MemoryEntryDto list> (agentFile agentId) []

    let save (agentId: AgentId) (entries: Dto.MemoryEntryDto list) =
        FileJson.write (agentFile agentId) entries

    interface IMemoryStore with
        member _.SaveAsync (agentId: AgentId) (entry: MemoryEntry) =
            task {
                lock sync (fun () ->
                    let existing = load agentId |> List.filter (fun e -> e.Key <> entry.Key)
                    save agentId (Dto.toMemoryDto entry :: existing))
            }

        member _.RecallAsync (agentId: AgentId) (query: string) =
            task {
                let result =
                    lock sync (fun () ->
                        load agentId
                        |> List.map Dto.ofMemoryDto
                        |> List.filter (fun e -> e.Key.Contains(query, System.StringComparison.OrdinalIgnoreCase)))
                return result
            }

        member _.RecallAllAsync(agentId: AgentId) =
            task { return lock sync (fun () -> load agentId |> List.map Dto.ofMemoryDto) }

        member _.ForgetAsync (agentId: AgentId) (key: string) =
            task {
                lock sync (fun () ->
                    let remaining = load agentId |> List.filter (fun e -> e.Key <> key)
                    save agentId remaining)
            }

        member _.ClearAsync(agentId: AgentId) =
            task {
                lock sync (fun () ->
                    let file = agentFile agentId
                    if File.Exists file then File.Delete file)
            }

/// Factory helpers for memory store implementations.
module MemoryStores =
    /// ADO.NET-backed store over any provider supplied via the connection factory.
    let ado (factory: IDbConnectionFactory) : IMemoryStore = AdoMemoryStore(factory) :> IMemoryStore

    /// FileSystem-backed store rooted at the given directory.
    let file (baseDir: string) : IMemoryStore = FileMemoryStore(baseDir) :> IMemoryStore
