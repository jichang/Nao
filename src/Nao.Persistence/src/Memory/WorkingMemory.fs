namespace Nao.Persistence

open Nao.Agents

/// Mutating events for working memory persistence.
/// (Attention boosts from GetAsync are read-side effects and are not persisted.)
[<RequireQualifiedAccess>]
type WorkingMemoryEvent =
    | Set of WorkingMemoryItem
    | Focus of key: string * boost: float
    | Decay
    | Pin of key: string
    | Unpin of key: string
    | Remove of key: string
    | Clear

/// Event-sourced working memory.
module PersistentWorkingMemory =
    let create (store: EventStore) (config: WorkingMemoryConfig) : WorkingMemory =
        let inner = InMemoryWorkingMemory.create config

        do
            for line in store.LoadAll() do
                match FSharpJson.deserialize<WorkingMemoryEvent> line with
                | WorkingMemoryEvent.Set item -> inner.SetAsync(item).GetAwaiter().GetResult()
                | WorkingMemoryEvent.Focus(k, b) -> (inner.FocusAsync k b).GetAwaiter().GetResult()
                | WorkingMemoryEvent.Decay -> inner.DecayAsync().GetAwaiter().GetResult() |> ignore
                | WorkingMemoryEvent.Pin k -> (inner.PinAsync k).GetAwaiter().GetResult()
                | WorkingMemoryEvent.Unpin k -> (inner.UnpinAsync k).GetAwaiter().GetResult()
                | WorkingMemoryEvent.Remove k -> (inner.RemoveAsync k).GetAwaiter().GetResult()
                | WorkingMemoryEvent.Clear -> inner.ClearAsync().GetAwaiter().GetResult()

        let append (e: WorkingMemoryEvent) = store.Append(FSharpJson.serialize e)

        { SetAsync = fun (item: WorkingMemoryItem) ->
            task {
                do! inner.SetAsync item
                append (WorkingMemoryEvent.Set item)
            }

          GetAsync = fun (key: string) -> inner.GetAsync key

          GetAllAsync = fun () -> inner.GetAllAsync()

          GetActiveAsync = fun (minAttention: float) -> inner.GetActiveAsync minAttention

          FocusAsync = fun (key: string) (boost: float) ->
            task {
                do! inner.FocusAsync key boost
                append (WorkingMemoryEvent.Focus(key, boost))
            }

          DecayAsync = fun () ->
            task {
                let! removed = inner.DecayAsync()
                append WorkingMemoryEvent.Decay
                return removed
            }

          PinAsync = fun (key: string) ->
            task {
                do! inner.PinAsync key
                append (WorkingMemoryEvent.Pin key)
            }

          UnpinAsync = fun (key: string) ->
            task {
                do! inner.UnpinAsync key
                append (WorkingMemoryEvent.Unpin key)
            }

          RemoveAsync = fun (key: string) ->
            task {
                do! inner.RemoveAsync key
                append (WorkingMemoryEvent.Remove key)
            }

          ClearAsync = fun () ->
            task {
                do! inner.ClearAsync()
                append WorkingMemoryEvent.Clear
            }

          RenderContextAsync = fun (topK: int) -> inner.RenderContextAsync topK }

/// Factory helpers for working memory persistence.
module WorkingMemories =
    /// ADO.NET-backed working memory over any provider supplied via the connection factory.
    let ado (factory: DbConnectionFactory) (config: WorkingMemoryConfig) : WorkingMemory =
        PersistentWorkingMemory.create (EventStore.db factory "working") config

    /// FileSystem-backed working memory rooted at the given directory.
    let file (baseDir: string) (config: WorkingMemoryConfig) : WorkingMemory =
        PersistentWorkingMemory.create (EventStore.file (System.IO.Path.Combine(baseDir, "working.jsonl"))) config
