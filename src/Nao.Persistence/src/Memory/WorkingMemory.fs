namespace Nao.Persistence

open System
open Nao.Agents

[<RequireQualifiedAccess>]
type WorkingMemoryEvent =
    | Set of WorkingMemoryItem
    | Focus of executionId: ExecutionId * key: string * boost: float
    | Decay of executionId: ExecutionId * asOf: DateTimeOffset
    | Pin of executionId: ExecutionId * key: string
    | Unpin of executionId: ExecutionId * key: string * asOf: DateTimeOffset
    | Remove of executionId: ExecutionId * key: string
    | DeleteOwner of executionId: ExecutionId
    | DeleteExpired of executionId: ExecutionId * before: DateTimeOffset

type WorkingMemoryDocument =
    { Version: int
      Event: WorkingMemoryEvent }

module PersistentWorkingMemory =
    let create context (store: EventStore) (config: WorkingMemoryConfig) : WorkingMemory =
        let inner = InMemoryWorkingMemory.create config

        let loadEvents () =
            EventStream.loadCurrent
                context
                1
                FSharpJson.deserialize<WorkingMemoryDocument>
                (fun document -> document.Version)
                (fun document -> document.Event)
                store

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

        do loadEvents () |> List.iter replay

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
                loadEvents () |> ignore
                do! operation ()
                append event
            }

        let deleteAfter operation event =
            task {
                loadEvents () |> ignore
                let! result = operation ()

                match result with
                | Ok _ -> append event
                | Error _ -> ()

                return result
            }

        let setAsync item =
            task {
                loadEvents () |> ignore
                let normalized = normalize item
                do! inner.SetAsync normalized
                append (WorkingMemoryEvent.Set normalized)
            }

        let decayAsync owner asOf =
            task {
                loadEvents () |> ignore
                let! count = inner.DecayAsync owner asOf
                append (WorkingMemoryEvent.Decay(owner, asOf))
                return count
            }

        { SetAsync = setAsync
          GetAsync = inner.GetAsync
          GetAllAsync = inner.GetAllAsync
          GetActiveAsync = inner.GetActiveAsync
          FocusAsync =
            fun owner key boost ->
                appendAfter (fun () -> inner.FocusAsync owner key boost) (WorkingMemoryEvent.Focus(owner, key, boost))
          DecayAsync = decayAsync
          PinAsync =
            fun owner key -> appendAfter (fun () -> inner.PinAsync owner key) (WorkingMemoryEvent.Pin(owner, key))
          UnpinAsync =
            fun owner key asOf ->
                appendAfter (fun () -> inner.UnpinAsync owner key asOf) (WorkingMemoryEvent.Unpin(owner, key, asOf))
          RemoveAsync =
            fun owner key -> appendAfter (fun () -> inner.RemoveAsync owner key) (WorkingMemoryEvent.Remove(owner, key))
          DeleteOwnerAsync =
            fun owner -> deleteAfter (fun () -> inner.DeleteOwnerAsync owner) (WorkingMemoryEvent.DeleteOwner owner)
          DeleteExpiredAsync =
            fun owner before ->
                deleteAfter
                    (fun () -> inner.DeleteExpiredAsync owner before)
                    (WorkingMemoryEvent.DeleteExpired(owner, before))
          RenderContextAsync = inner.RenderContextAsync }

module WorkingMemories =
    let ado (factory: DbConnectionFactory) (config: WorkingMemoryConfig) : WorkingMemory =
        PersistentWorkingMemory.create "working" (EventStore.db factory "working") config

    let file (baseDir: string) (config: WorkingMemoryConfig) : WorkingMemory =
        let path = System.IO.Path.Combine(baseDir, "working.jsonl")
        PersistentWorkingMemory.create path (EventStore.file path) config
