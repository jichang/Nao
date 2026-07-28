namespace Nao.Persistence

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

/// In-memory semantic memory implementation
type InMemorySemanticMemory(embeddingProvider: IEmbeddingProvider) =
    let store = System.Collections.Concurrent.ConcurrentDictionary<string, SemanticEntry list>()

    let agentKey (agentId: string) = agentId

    interface ISemanticMemory with
        member _.StoreAsync (agentId: string) (key: string) (content: string) =
            task {
                let! embedding = embeddingProvider.EmbedAsync content
                let entry =
                    { Key = key
                      Content = content
                      Embedding = embedding
                      Timestamp = DateTimeOffset.UtcNow
                      Tags = [] }
                let storeKey = agentKey agentId
                store.AddOrUpdate(
                    storeKey,
                    [ entry ],
                    fun _ existing ->
                        let filtered = existing |> List.filter (fun e -> e.Key <> key)
                        entry :: filtered)
                |> ignore
            }

        member _.RetrieveAsync (agentId: string) (query: string) (topK: int) =
            task {
                let! queryEmbedding = embeddingProvider.EmbedAsync query
                let storeKey = agentKey agentId
                match store.TryGetValue(storeKey) with
                | true, entries ->
                    return
                        entries
                        |> List.map (fun e -> (e, SemanticSimilarity.cosineSimilarity queryEmbedding e.Embedding))
                        |> List.sortByDescending snd
                        |> List.truncate topK
                        |> List.map fst
                | false, _ -> return []
            }

        member _.RemoveAsync (agentId: string) (key: string) =
            let storeKey = agentKey agentId
            match store.TryGetValue(storeKey) with
            | true, entries ->
                store.[storeKey] <- entries |> List.filter (fun e -> e.Key <> key)
            | false, _ -> ()
            task { return () }

/// A simple bag-of-words embedding provider for testing (no external dependencies)
type SimpleEmbeddingProvider() =
    let vocabulary = System.Collections.Concurrent.ConcurrentDictionary<string, int>()
    let mutable nextIndex = 0

    let getIndex (word: string) =
        vocabulary.GetOrAdd(word, fun _ ->
            let idx = nextIndex
            nextIndex <- nextIndex + 1
            idx)

    interface IEmbeddingProvider with
        member _.EmbedAsync (text: string) =
            let words =
                text.ToLowerInvariant().Split([| ' '; '.'; ','; '!'; '?'; '\n'; '\r'; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            // Build a sparse vector using word frequencies
            let wordCounts = System.Collections.Generic.Dictionary<int, float>()
            for word in words do
                let idx = getIndex word
                match wordCounts.TryGetValue(idx) with
                | true, count -> wordCounts.[idx] <- count + 1.0
                | false, _ -> wordCounts.[idx] <- 1.0

            // Create a dense vector up to current vocabulary size
            let size = max nextIndex 1
            let vector = Array.zeroCreate<float> size
            for kvp in wordCounts do
                if kvp.Key < size then
                    vector.[kvp.Key] <- kvp.Value
            Task.FromResult(vector)

/// ADO.NET-backed ISemanticMemory. Embeddings are stored as JSON; similarity is
/// computed in-process so the implementation stays provider-agnostic.
type AdoSemanticMemory(embeddingProvider: IEmbeddingProvider, factory: IDbConnectionFactory) =

    let ensureAsync () =
        Ado.executeNonQuery
            factory
            "CREATE TABLE IF NOT EXISTS nao_semantic (\
                agent TEXT NOT NULL, \
                sem_key TEXT NOT NULL, \
                sem_content TEXT NOT NULL, \
                sem_embedding TEXT NOT NULL, \
                sem_ts TEXT NOT NULL, \
                sem_tags TEXT NOT NULL, \
                PRIMARY KEY (agent, sem_key))"
            []
        :> Task

    let mapEntry (r: DbDataReader) : SemanticEntry =
        { Key = Ado.getString r "sem_key"
          Content = Ado.getString r "sem_content"
          Embedding = Json.floatsFromJson (Ado.getString r "sem_embedding")
          Timestamp = Time.fromIso (Ado.getString r "sem_ts")
          Tags = Json.tagsFromJson (Ado.getString r "sem_tags") }

    interface ISemanticMemory with
        member _.StoreAsync (agentId: string) (key: string) (content: string) =
            task {
                do! ensureAsync ()
                let! embedding = embeddingProvider.EmbedAsync content
                let agent = agentId
                do!
                    Ado.executeTransaction
                        factory
                        [ "DELETE FROM nao_semantic WHERE agent = @a AND sem_key = @k",
                          [ "@a", box agent; "@k", box key ]
                          "INSERT INTO nao_semantic (agent, sem_key, sem_content, sem_embedding, sem_ts, sem_tags) \
                                VALUES (@a, @k, @c, @e, @t, @g)",
                          [ "@a", box agent
                            "@k", box key
                            "@c", box content
                            "@e", box (Json.floatsToJson embedding)
                            "@t", box (Time.toIso System.DateTimeOffset.UtcNow)
                            "@g", box (Json.tagsToJson []) ] ]
            }

        member _.RetrieveAsync (agentId: string) (query: string) (topK: int) =
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
                    |> List.map (fun e -> e, SemanticSimilarity.cosineSimilarity queryEmbedding e.Embedding)
                    |> List.sortByDescending snd
                    |> List.truncate topK
                    |> List.map fst
            }

        member _.RemoveAsync (agentId: string) (key: string) =
            task {
                do! ensureAsync ()
                let! _ =
                    Ado.executeNonQuery
                        factory
                        "DELETE FROM nao_semantic WHERE agent = @a AND sem_key = @k"
                        [ "@a", box agentId; "@k", box key ]
                return ()
            }

/// FileSystem-backed ISemanticMemory. One JSON document per agent.
type FileSemanticMemory(embeddingProvider: IEmbeddingProvider, baseDir: string) =
    let sync = obj ()

    let agentFile (agentId: string) =
        Path.Combine(baseDir, sprintf "%s.json" (Sanitize.id agentId))

    let load (agentId: string) : Dto.SemanticEntryDto list =
        FileJson.read<Dto.SemanticEntryDto list> (agentFile agentId) []

    let save (agentId: string) (entries: Dto.SemanticEntryDto list) =
        FileJson.write (agentFile agentId) entries

    interface ISemanticMemory with
        member _.StoreAsync (agentId: string) (key: string) (content: string) =
            task {
                let! embedding = embeddingProvider.EmbedAsync content
                let entry: SemanticEntry =
                    { Key = key
                      Content = content
                      Embedding = embedding
                      Timestamp = System.DateTimeOffset.UtcNow
                      Tags = [] }
                lock sync (fun () ->
                    let existing = load agentId |> List.filter (fun e -> e.Key <> key)
                    save agentId (Dto.toSemanticDto entry :: existing))
            }

        member _.RetrieveAsync (agentId: string) (query: string) (topK: int) =
            task {
                let! queryEmbedding = embeddingProvider.EmbedAsync query
                let entries = lock sync (fun () -> load agentId |> List.map Dto.ofSemanticDto)
                return
                    entries
                    |> List.map (fun e -> e, SemanticSimilarity.cosineSimilarity queryEmbedding e.Embedding)
                    |> List.sortByDescending snd
                    |> List.truncate topK
                    |> List.map fst
            }

        member _.RemoveAsync (agentId: string) (key: string) =
            task {
                lock sync (fun () ->
                    let remaining = load agentId |> List.filter (fun e -> e.Key <> key)
                    save agentId remaining)
            }

/// Factory helpers for semantic memory implementations.
module SemanticMemories =
    /// ADO.NET-backed semantic memory over any provider supplied via the connection factory.
    let ado (embeddingProvider: IEmbeddingProvider) (factory: IDbConnectionFactory) : ISemanticMemory =
        AdoSemanticMemory(embeddingProvider, factory) :> ISemanticMemory

    /// FileSystem-backed semantic memory rooted at the given directory.
    let file (embeddingProvider: IEmbeddingProvider) (baseDir: string) : ISemanticMemory =
        FileSemanticMemory(embeddingProvider, baseDir) :> ISemanticMemory
