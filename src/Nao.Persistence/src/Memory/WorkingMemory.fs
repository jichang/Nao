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

/// Event-sourced IWorkingMemory.
type PersistentWorkingMemory(store: IEventStore, config: WorkingMemoryConfig) =
    let inner = InMemoryWorkingMemory(config) :> IWorkingMemory

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

    interface IWorkingMemory with
        member _.SetAsync(item: WorkingMemoryItem) =
            task {
                do! inner.SetAsync item
                append (WorkingMemoryEvent.Set item)
            }

        member _.GetAsync(key: string) = inner.GetAsync key

        member _.GetAllAsync() = inner.GetAllAsync()

        member _.GetActiveAsync(minAttention: float) = inner.GetActiveAsync minAttention

        member _.FocusAsync (key: string) (boost: float) =
            task {
                do! inner.FocusAsync key boost
                append (WorkingMemoryEvent.Focus(key, boost))
            }

        member _.DecayAsync() =
            task {
                let! removed = inner.DecayAsync()
                append WorkingMemoryEvent.Decay
                return removed
            }

        member _.PinAsync(key: string) =
            task {
                do! inner.PinAsync key
                append (WorkingMemoryEvent.Pin key)
            }

        member _.UnpinAsync(key: string) =
            task {
                do! inner.UnpinAsync key
                append (WorkingMemoryEvent.Unpin key)
            }

        member _.RemoveAsync(key: string) =
            task {
                do! inner.RemoveAsync key
                append (WorkingMemoryEvent.Remove key)
            }

        member _.ClearAsync() =
            task {
                do! inner.ClearAsync()
                append WorkingMemoryEvent.Clear
            }

        member _.RenderContextAsync(topK: int) = inner.RenderContextAsync topK

/// Factory helpers for working memory persistence.
module WorkingMemories =
    /// ADO.NET-backed working memory over any provider supplied via the connection factory.
    let ado (factory: IDbConnectionFactory) (config: WorkingMemoryConfig) : IWorkingMemory =
        PersistentWorkingMemory(EventStore.db factory "working", config) :> IWorkingMemory

    /// FileSystem-backed working memory rooted at the given directory.
    let file (baseDir: string) (config: WorkingMemoryConfig) : IWorkingMemory =
        PersistentWorkingMemory(EventStore.file (System.IO.Path.Combine(baseDir, "working.jsonl")), config)
        :> IWorkingMemory
