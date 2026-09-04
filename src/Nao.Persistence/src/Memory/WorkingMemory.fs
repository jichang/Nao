namespace Nao.Persistence

open System
open Nao.Agents

[<RequireQualifiedAccess>]
type WorkingMemoryEvent =
    | Set of WorkingMemoryItem
    | Focus of executionId: string * key: string * boost: float
    | Decay of executionId: string * asOf: DateTimeOffset
    | Pin of executionId: string * key: string
    | Unpin of executionId: string * key: string * asOf: DateTimeOffset
    | Remove of executionId: string * key: string
    | DeleteOwner of executionId: string
    | DeleteExpired of executionId: string * before: DateTimeOffset

type WorkingMemoryDocument =
    { Version: int
      Event: WorkingMemoryEvent }

module PersistentWorkingMemory =
    let create (store: EventStore) (config: WorkingMemoryConfig) : WorkingMemory =
        let inner = InMemoryWorkingMemory.create config

        let replay event =
            match event with
            | WorkingMemoryEvent.Set item -> inner.SetAsync(item).GetAwaiter().GetResult()
            | WorkingMemoryEvent.Focus(owner, key, boost) ->
                inner.FocusAsync owner key boost |> _.GetAwaiter().GetResult()
            | WorkingMemoryEvent.Decay(owner, asOf) ->
                inner.DecayAsync owner asOf |> _.GetAwaiter().GetResult() |> ignore
            | WorkingMemoryEvent.Pin(owner, key) -> inner.PinAsync owner key |> _.GetAwaiter().GetResult()
            | WorkingMemoryEvent.Unpin(owner, key, asOf) ->
                inner.UnpinAsync owner key asOf |> _.GetAwaiter().GetResult()
            | WorkingMemoryEvent.Remove(owner, key) -> inner.RemoveAsync owner key |> _.GetAwaiter().GetResult()
            | WorkingMemoryEvent.DeleteOwner owner ->
                inner.DeleteOwnerAsync owner |> _.GetAwaiter().GetResult() |> ignore
            | WorkingMemoryEvent.DeleteExpired(owner, before) ->
                inner.DeleteExpiredAsync owner before |> _.GetAwaiter().GetResult() |> ignore

        do
            store.LoadAll()
            |> Seq.map FSharpJson.deserialize<WorkingMemoryDocument>
            |> Seq.iter (fun document ->
                if document.Version <> 1 then
                    invalidOp (sprintf "Unsupported working-memory document version: %d." document.Version)

                replay document.Event)

        let append event =
            store.Append(FSharpJson.serialize { Version = 1; Event = event })

        let normalize (item: WorkingMemoryItem) =
            if item.Pinned then
                { item with ExpiresAt = None }
            elif item.ExpiresAt.IsNone then
                { item with
                    ExpiresAt = Some(item.AddedAt + config.DefaultTtl) }
            else
                item

        let appendAfter operation event =
            task {
                do! operation
                append event
            }

        let deleteAfter operation event =
            task {
                let! result = operation

                match result with
                | Ok _ -> append event
                | Error _ -> ()

                return result
            }

        { SetAsync =
            fun item ->
                task {
                    let normalized = normalize item
                    do! inner.SetAsync normalized
                    append (WorkingMemoryEvent.Set normalized)
                }
          GetAsync = inner.GetAsync
          GetAllAsync = inner.GetAllAsync
          GetActiveAsync = inner.GetActiveAsync
          FocusAsync =
            fun owner key boost ->
                appendAfter (inner.FocusAsync owner key boost) (WorkingMemoryEvent.Focus(owner, key, boost))
          DecayAsync =
            fun owner asOf ->
                task {
                    let! count = inner.DecayAsync owner asOf
                    append (WorkingMemoryEvent.Decay(owner, asOf))
                    return count
                }
          PinAsync = fun owner key -> appendAfter (inner.PinAsync owner key) (WorkingMemoryEvent.Pin(owner, key))
          UnpinAsync =
            fun owner key asOf ->
                appendAfter (inner.UnpinAsync owner key asOf) (WorkingMemoryEvent.Unpin(owner, key, asOf))
          RemoveAsync =
            fun owner key -> appendAfter (inner.RemoveAsync owner key) (WorkingMemoryEvent.Remove(owner, key))
          DeleteOwnerAsync =
            fun owner -> deleteAfter (inner.DeleteOwnerAsync owner) (WorkingMemoryEvent.DeleteOwner owner)
          DeleteExpiredAsync =
            fun owner before ->
                deleteAfter (inner.DeleteExpiredAsync owner before) (WorkingMemoryEvent.DeleteExpired(owner, before))
          RenderContextAsync = inner.RenderContextAsync }

module WorkingMemories =
    let ado (factory: DbConnectionFactory) (config: WorkingMemoryConfig) : WorkingMemory =
        PersistentWorkingMemory.create (EventStore.db factory "working") config

    let file (baseDir: string) (config: WorkingMemoryConfig) : WorkingMemory =
        PersistentWorkingMemory.create (EventStore.file (System.IO.Path.Combine(baseDir, "working.jsonl"))) config
