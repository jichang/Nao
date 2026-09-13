namespace Nao.Persistence

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

module private SemanticOperations =
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

/// In-memory semantic memory factory.
module InMemorySemanticMemory =
    let create (embeddingProvider: EmbeddingProvider) : SemanticMemory =
        let store =
            System.Collections.Concurrent.ConcurrentDictionary<string, SemanticEntry list>()

        { StoreAsync =
            fun scope key content ->
                task {
                    let! embedding = embeddingProvider.EmbedAsync content

                    let entry =
                        { Key = key
                          Content = content
                          Embedding = embedding
                          Timestamp = DateTimeOffset.UtcNow
                          Tags = [] }

                    store.AddOrUpdate(
                        SemanticOperations.ownerKey scope,
                        [ entry ],
                        fun _ existing -> entry :: (existing |> List.filter (fun item -> item.Key <> key))
                    )
                    |> ignore
                }
          RetrieveAsync =
            fun scope query topK ->
                task {
                    let! queryEmbedding = embeddingProvider.EmbedAsync query

                    match store.TryGetValue(SemanticOperations.ownerKey scope) with
                    | true, entries ->
                        return
                            entries
                            |> List.map (fun entry ->
                                entry, SemanticSimilarity.cosineSimilarity queryEmbedding entry.Embedding)
                            |> List.sortByDescending snd
                            |> List.truncate topK
                            |> List.map fst
                    | false, _ -> return []
                }
          RemoveAsync =
            fun scope key ->
                task {
                    let ownerKey = SemanticOperations.ownerKey scope

                    match store.TryGetValue ownerKey with
                    | true, entries -> store.[ownerKey] <- entries |> List.filter (fun entry -> entry.Key <> key)
                    | false, _ -> ()
                }
          DeleteOwnerAsync =
            fun scope ->
                SemanticOperations.protect (fun () ->
                    match store.TryRemove(SemanticOperations.ownerKey scope) with
                    | true, entries -> Task.FromResult entries.Length
                    | false, _ -> Task.FromResult 0)
          DeleteExpiredAsync =
            fun scope before ->
                SemanticOperations.protect (fun () ->
                    let ownerKey = SemanticOperations.ownerKey scope

                    match store.TryGetValue ownerKey with
                    | true, entries ->
                        let retained = entries |> List.filter (fun entry -> entry.Timestamp >= before)
                        store.[ownerKey] <- retained
                        Task.FromResult(entries.Length - retained.Length)
                    | false, _ -> Task.FromResult 0) }

/// A simple bag-of-words embedding provider for testing (no external dependencies).
module SimpleEmbeddingProvider =
    let create () : EmbeddingProvider =
        let vocabulary = System.Collections.Concurrent.ConcurrentDictionary<string, int>()
        let mutable nextIndex = 0

        let getIndex word =
            vocabulary.GetOrAdd(
                word,
                fun _ ->
                    let index = nextIndex
                    nextIndex <- nextIndex + 1
                    index
            )

        { EmbedAsync =
            fun text ->
                let words =
                    text
                        .ToLowerInvariant()
                        .Split([| ' '; '.'; ','; '!'; '?'; '\n'; '\r'; '\t' |], StringSplitOptions.RemoveEmptyEntries)

                let wordCounts = System.Collections.Generic.Dictionary<int, float>()

                for word in words do
                    let index = getIndex word

                    match wordCounts.TryGetValue index with
                    | true, count -> wordCounts.[index] <- count + 1.0
                    | false, _ -> wordCounts.[index] <- 1.0

                let vector = Array.zeroCreate<float> (max nextIndex 1)

                for entry in wordCounts do
                    vector.[entry.Key] <- entry.Value

                Task.FromResult vector }

/// ADO.NET-backed semantic memory. Embeddings are stored as JSON; similarity is
/// computed in-process so the implementation stays provider-agnostic.
module AdoSemanticMemory =
    let create (embeddingProvider: EmbeddingProvider) (factory: DbConnectionFactory) : SemanticMemory =
        let ensureAsync () =
            task {
                do!
                    AdoSchema.ensureVersionedTable
                        factory
                        "semantic-memory"
                        "nao_semantic"
                        "CREATE TABLE IF NOT EXISTS nao_semantic (owner_key TEXT NOT NULL, sem_key TEXT NOT NULL, sem_content TEXT NOT NULL, sem_embedding TEXT NOT NULL, sem_ts TEXT NOT NULL, sem_tags TEXT NOT NULL, PRIMARY KEY (owner_key, sem_key))"

                try
                    let! _ =
                        Ado.query
                            factory
                            "SELECT owner_key, sem_key, sem_content, sem_embedding, sem_ts, sem_tags FROM nao_semantic WHERE 1 = 0"
                            []
                            ignore

                    return ()
                with :? DbException as error ->
                    return
                        raise (
                            InvalidDataException(
                                "Semantic-memory table has an incompatible shape. Follow docs/migrations before accessing or mutating this database.",
                                error
                            )
                        )
            }

        let mapEntry (reader: DbDataReader) : SemanticEntry =
            let key = Ado.getString reader "sem_key"

            try
                { Key = key
                  Content = Ado.getString reader "sem_content"
                  Embedding = Json.floatsFromJson (Ado.getString reader "sem_embedding")
                  Timestamp = Time.fromIso (Ado.getString reader "sem_ts")
                  Tags = Json.tagsFromJson (Ado.getString reader "sem_tags") }
            with ex ->
                raise (
                    InvalidDataException(
                        sprintf "Semantic-memory row '%s' is invalid. Follow docs/migrations before writing." key,
                        ex
                    )
                )

        let loadAll scope =
            Ado.query
                factory
                "SELECT sem_key, sem_content, sem_embedding, sem_ts, sem_tags FROM nao_semantic WHERE owner_key = @a"
                [ "@a", box (SemanticOperations.ownerKey scope) ]
                mapEntry

        let validateAsync scope =
            task {
                do! ensureAsync ()
                let! _ = loadAll scope
                return ()
            }

        let removeAsync scope key =
            task {
                do! validateAsync scope

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "DELETE FROM nao_semantic WHERE owner_key = @a AND sem_key = @k"
                        [ "@a", box (SemanticOperations.ownerKey scope); "@k", box key ]

                return ()
            }

        { StoreAsync =
            fun scope key content ->
                task {
                    do! validateAsync scope
                    let! embedding = embeddingProvider.EmbedAsync content

                    do!
                        Ado.executeTransaction
                            factory
                            [ "DELETE FROM nao_semantic WHERE owner_key = @a AND sem_key = @k",
                              [ "@a", box (SemanticOperations.ownerKey scope); "@k", box key ]
                              "INSERT INTO nao_semantic (owner_key, sem_key, sem_content, sem_embedding, sem_ts, sem_tags) VALUES (@a, @k, @c, @e, @t, @g)",
                              [ "@a", box (SemanticOperations.ownerKey scope)
                                "@k", box key
                                "@c", box content
                                "@e", box (Json.floatsToJson embedding)
                                "@t", box (Time.toIso DateTimeOffset.UtcNow)
                                "@g", box (Json.tagsToJson []) ] ]
                }
          RetrieveAsync =
            fun scope query topK ->
                task {
                    do! ensureAsync ()
                    let! queryEmbedding = embeddingProvider.EmbedAsync query

                    let! entries = loadAll scope

                    return
                        entries
                        |> List.map (fun entry ->
                            entry, SemanticSimilarity.cosineSimilarity queryEmbedding entry.Embedding)
                        |> List.sortByDescending snd
                        |> List.truncate topK
                        |> List.map fst
                }
          RemoveAsync = removeAsync
          DeleteOwnerAsync =
            fun scope ->
                SemanticOperations.protect (fun () ->
                    task {
                        do! validateAsync scope

                        return!
                            Ado.executeNonQuery
                                factory
                                "DELETE FROM nao_semantic WHERE owner_key = @a"
                                [ "@a", box (SemanticOperations.ownerKey scope) ]
                    })
          DeleteExpiredAsync =
            fun scope before ->
                SemanticOperations.protect (fun () ->
                    task {
                        do! validateAsync scope

                        return!
                            Ado.executeNonQuery
                                factory
                                "DELETE FROM nao_semantic WHERE owner_key = @a AND sem_ts < @before"
                                [ "@a", box (SemanticOperations.ownerKey scope)
                                  "@before", box (Time.toIso before) ]
                    }) }

/// FileSystem-backed semantic memory. One JSON document per authorization identity.
module FileSemanticMemory =
    [<Literal>]
    let private CurrentSchemaVersion = 1

    let create (embeddingProvider: EmbeddingProvider) (baseDir: string) : SemanticMemory =
        let sync = obj ()

        let scopeFile scope =
            Path.Combine(baseDir, sprintf "%s.json" (SemanticOperations.ownerKey scope))

        let load scope =
            VersionedFileJson.read<Dto.SemanticEntryDto list>
                "Semantic memory document"
                CurrentSchemaVersion
                (scopeFile scope)
                []

        let save scope entries =
            VersionedFileJson.write CurrentSchemaVersion (scopeFile scope) entries

        { StoreAsync =
            fun scope key content ->
                task {
                    let! embedding = embeddingProvider.EmbedAsync content

                    let entry: SemanticEntry =
                        { Key = key
                          Content = content
                          Embedding = embedding
                          Timestamp = DateTimeOffset.UtcNow
                          Tags = [] }

                    lock sync (fun () ->
                        save
                            scope
                            (Dto.toSemanticDto entry
                             :: (load scope |> List.filter (fun item -> item.Key <> key))))
                }
          RetrieveAsync =
            fun scope query topK ->
                task {
                    let! queryEmbedding = embeddingProvider.EmbedAsync query
                    let entries = lock sync (fun () -> load scope |> List.map Dto.ofSemanticDto)

                    return
                        entries
                        |> List.map (fun entry ->
                            entry, SemanticSimilarity.cosineSimilarity queryEmbedding entry.Embedding)
                        |> List.sortByDescending snd
                        |> List.truncate topK
                        |> List.map fst
                }
          RemoveAsync =
            fun scope key ->
                task { lock sync (fun () -> save scope (load scope |> List.filter (fun item -> item.Key <> key))) }
          DeleteOwnerAsync =
            fun scope ->
                SemanticOperations.protect (fun () ->
                    task {
                        return
                            lock sync (fun () ->
                                let entries = load scope
                                let file = scopeFile scope

                                if File.Exists file then
                                    File.Delete file

                                entries.Length)
                    })
          DeleteExpiredAsync =
            fun scope before ->
                SemanticOperations.protect (fun () ->
                    task {
                        return
                            lock sync (fun () ->
                                let entries = load scope

                                let retained =
                                    entries
                                    |> List.filter (Dto.ofSemanticDto >> fun entry -> entry.Timestamp >= before)

                                save scope retained
                                entries.Length - retained.Length)
                    }) }

/// Factory helpers for semantic memory implementations.
module SemanticMemories =
    /// In-memory semantic memory.
    let inMemory (embeddingProvider: EmbeddingProvider) : SemanticMemory =
        InMemorySemanticMemory.create embeddingProvider

    /// ADO.NET-backed semantic memory over any provider supplied via the connection factory.
    let ado (embeddingProvider: EmbeddingProvider) (factory: DbConnectionFactory) : SemanticMemory =
        AdoSemanticMemory.create embeddingProvider factory

    /// FileSystem-backed semantic memory rooted at the given directory.
    let file (embeddingProvider: EmbeddingProvider) (baseDir: string) : SemanticMemory =
        FileSemanticMemory.create embeddingProvider baseDir
