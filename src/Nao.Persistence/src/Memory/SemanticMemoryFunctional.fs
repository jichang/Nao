namespace Nao.Persistence

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

module private SemanticOperations =
    let private failure =
        PlatformFailure.fromException PlatformFailureBoundary.Storage None

    let protect owner operation =
        task {
            if String.IsNullOrWhiteSpace owner then
                return
                    Error(
                        PlatformFailure.create
                            PlatformErrorCategory.InvalidInput
                            "Semantic memory owner cannot be blank."
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

/// In-memory semantic memory factory.
module InMemorySemanticMemory =
    let create (embeddingProvider: EmbeddingProvider) : SemanticMemory =
        let store =
            System.Collections.Concurrent.ConcurrentDictionary<string, SemanticEntry list>()

        { StoreAsync =
            fun agentId key content ->
                task {
                    let! embedding = embeddingProvider.EmbedAsync content

                    let entry =
                        { Key = key
                          Content = content
                          Embedding = embedding
                          Timestamp = DateTimeOffset.UtcNow
                          Tags = [] }

                    store.AddOrUpdate(
                        agentId,
                        [ entry ],
                        fun _ existing -> entry :: (existing |> List.filter (fun item -> item.Key <> key))
                    )
                    |> ignore
                }
          RetrieveAsync =
            fun agentId query topK ->
                task {
                    let! queryEmbedding = embeddingProvider.EmbedAsync query

                    match store.TryGetValue agentId with
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
            fun agentId key ->
                task {
                    match store.TryGetValue agentId with
                    | true, entries -> store.[agentId] <- entries |> List.filter (fun entry -> entry.Key <> key)
                    | false, _ -> ()
                }
          DeleteOwnerAsync =
            fun owner ->
                SemanticOperations.protect owner (fun () ->
                    match store.TryRemove owner with
                    | true, entries -> Task.FromResult entries.Length
                    | false, _ -> Task.FromResult 0)
          DeleteExpiredAsync =
            fun owner before ->
                SemanticOperations.protect owner (fun () ->
                    match store.TryGetValue owner with
                    | true, entries ->
                        let retained = entries |> List.filter (fun entry -> entry.Timestamp >= before)
                        store.[owner] <- retained
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
            Ado.executeNonQuery
                factory
                "CREATE TABLE IF NOT EXISTS nao_semantic (agent TEXT NOT NULL, sem_key TEXT NOT NULL, sem_content TEXT NOT NULL, sem_embedding TEXT NOT NULL, sem_ts TEXT NOT NULL, sem_tags TEXT NOT NULL, PRIMARY KEY (agent, sem_key))"
                []
            :> Task

        let mapEntry (reader: DbDataReader) : SemanticEntry =
            { Key = Ado.getString reader "sem_key"
              Content = Ado.getString reader "sem_content"
              Embedding = Json.floatsFromJson (Ado.getString reader "sem_embedding")
              Timestamp = Time.fromIso (Ado.getString reader "sem_ts")
              Tags = Json.tagsFromJson (Ado.getString reader "sem_tags") }

        { StoreAsync =
            fun agentId key content ->
                task {
                    do! ensureAsync ()
                    let! embedding = embeddingProvider.EmbedAsync content

                    do!
                        Ado.executeTransaction
                            factory
                            [ "DELETE FROM nao_semantic WHERE agent = @a AND sem_key = @k",
                              [ "@a", box agentId; "@k", box key ]
                              "INSERT INTO nao_semantic (agent, sem_key, sem_content, sem_embedding, sem_ts, sem_tags) VALUES (@a, @k, @c, @e, @t, @g)",
                              [ "@a", box agentId
                                "@k", box key
                                "@c", box content
                                "@e", box (Json.floatsToJson embedding)
                                "@t", box (Time.toIso DateTimeOffset.UtcNow)
                                "@g", box (Json.tagsToJson []) ] ]
                }
          RetrieveAsync =
            fun agentId query topK ->
                task {
                    do! ensureAsync ()
                    let! queryEmbedding = embeddingProvider.EmbedAsync query

                    let! entries =
                        Ado.query
                            factory
                            "SELECT sem_key, sem_content, sem_embedding, sem_ts, sem_tags FROM nao_semantic WHERE agent = @a"
                            [ "@a", box agentId ]
                            mapEntry

                    return
                        entries
                        |> List.map (fun entry ->
                            entry, SemanticSimilarity.cosineSimilarity queryEmbedding entry.Embedding)
                        |> List.sortByDescending snd
                        |> List.truncate topK
                        |> List.map fst
                }
          RemoveAsync =
            fun agentId key ->
                task {
                    do! ensureAsync ()

                    let! _ =
                        Ado.executeNonQuery
                            factory
                            "DELETE FROM nao_semantic WHERE agent = @a AND sem_key = @k"
                            [ "@a", box agentId; "@k", box key ]

                    return ()
                }
          DeleteOwnerAsync =
            fun owner ->
                SemanticOperations.protect owner (fun () ->
                    task {
                        do! ensureAsync ()

                        return!
                            Ado.executeNonQuery factory "DELETE FROM nao_semantic WHERE agent = @a" [ "@a", box owner ]
                    })
          DeleteExpiredAsync =
            fun owner before ->
                SemanticOperations.protect owner (fun () ->
                    task {
                        do! ensureAsync ()

                        return!
                            Ado.executeNonQuery
                                factory
                                "DELETE FROM nao_semantic WHERE agent = @a AND sem_ts < @before"
                                [ "@a", box owner; "@before", box (Time.toIso before) ]
                    }) }

/// FileSystem-backed semantic memory. One JSON document per agent.
module FileSemanticMemory =
    let create (embeddingProvider: EmbeddingProvider) (baseDir: string) : SemanticMemory =
        let sync = obj ()

        let agentFile agentId =
            Path.Combine(baseDir, sprintf "%s.json" (Sanitize.id agentId))

        let load agentId =
            FileJson.read<Dto.SemanticEntryDto list> (agentFile agentId) []

        let save agentId entries =
            FileJson.write (agentFile agentId) entries

        { StoreAsync =
            fun agentId key content ->
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
                            agentId
                            (Dto.toSemanticDto entry
                             :: (load agentId |> List.filter (fun item -> item.Key <> key))))
                }
          RetrieveAsync =
            fun agentId query topK ->
                task {
                    let! queryEmbedding = embeddingProvider.EmbedAsync query
                    let entries = lock sync (fun () -> load agentId |> List.map Dto.ofSemanticDto)

                    return
                        entries
                        |> List.map (fun entry ->
                            entry, SemanticSimilarity.cosineSimilarity queryEmbedding entry.Embedding)
                        |> List.sortByDescending snd
                        |> List.truncate topK
                        |> List.map fst
                }
          RemoveAsync =
            fun agentId key ->
                task { lock sync (fun () -> save agentId (load agentId |> List.filter (fun item -> item.Key <> key))) }
          DeleteOwnerAsync =
            fun owner ->
                SemanticOperations.protect owner (fun () ->
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
                SemanticOperations.protect owner (fun () ->
                    task {
                        return
                            lock sync (fun () ->
                                let entries = load owner

                                let retained =
                                    entries
                                    |> List.filter (Dto.ofSemanticDto >> fun entry -> entry.Timestamp >= before)

                                save owner retained
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
