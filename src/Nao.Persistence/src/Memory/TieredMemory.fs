namespace Nao.Persistence

open System
open Nao.Agents

[<RequireQualifiedAccess>]
type TieredEvent =
    | Store of TieredMemoryEntry
    | RecordAccess of owner: string * keys: string list * asOf: DateTimeOffset
    | Promote of owner: string * key: string * targetTier: MemoryTier
    | Evict of owner: string * asOf: DateTimeOffset
    | DeleteOwner of owner: string
    | DeleteExpired of owner: string * before: DateTimeOffset

type TieredDocument = { Version: int; Event: TieredEvent }

module PersistentTieredMemory =
    let create
        context
        (store: EventStore)
        (config: TieredMemoryConfig)
        (embeddingProvider: EmbeddingProvider option)
        : TieredMemory =
        let inner = InMemoryTieredMemory.create config embeddingProvider

        let loadEvents () =
            EventStream.loadCurrent
                context
                1
                FSharpJson.deserialize<TieredDocument>
                (fun document -> document.Version)
                (fun document -> document.Event)
                store

        let replay event =
            match event with
            | TieredEvent.Store entry -> inner.StoreAsync entry |> _.GetAwaiter().GetResult()
            | TieredEvent.RecordAccess(owner, keys, asOf) ->
                inner.RecordAccessAsync owner keys asOf |> _.GetAwaiter().GetResult()
            | TieredEvent.Promote(owner, key, targetTier) ->
                inner.PromoteAsync owner key targetTier |> _.GetAwaiter().GetResult()
            | TieredEvent.Evict(owner, asOf) -> inner.EvictAsync owner asOf |> _.GetAwaiter().GetResult() |> ignore
            | TieredEvent.DeleteOwner owner -> inner.DeleteOwnerAsync owner |> _.GetAwaiter().GetResult() |> ignore
            | TieredEvent.DeleteExpired(owner, before) ->
                inner.DeleteExpiredAsync owner before |> _.GetAwaiter().GetResult() |> ignore

        do loadEvents () |> List.iter replay

        let append event =
            store.Append(FSharpJson.serialize { Version = 1; Event = event })

        let appendAfter operation event =
            task {
                loadEvents () |> ignore
                do! operation ()
                append event
            }

        let countAfter operation event =
            task {
                loadEvents () |> ignore
                let! count = operation ()
                append event
                return count
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

        let storeAsync entry =
            appendAfter (fun () -> inner.StoreAsync entry) (TieredEvent.Store entry)

        let recordAccessAsync owner keys asOf =
            appendAfter
                (fun () -> inner.RecordAccessAsync owner keys asOf)
                (TieredEvent.RecordAccess(owner, keys, asOf))

        let promoteAsync owner key targetTier =
            appendAfter
                (fun () -> inner.PromoteAsync owner key targetTier)
                (TieredEvent.Promote(owner, key, targetTier))

        let evictAsync owner asOf =
            countAfter (fun () -> inner.EvictAsync owner asOf) (TieredEvent.Evict(owner, asOf))

        let deleteOwnerAsync owner =
            deleteAfter (fun () -> inner.DeleteOwnerAsync owner) (TieredEvent.DeleteOwner owner)

        let deleteExpiredAsync owner before =
            deleteAfter (fun () -> inner.DeleteExpiredAsync owner before) (TieredEvent.DeleteExpired(owner, before))

        { StoreAsync = storeAsync
          RetrieveAsync = inner.RetrieveAsync
          RetrieveFromTierAsync = inner.RetrieveFromTierAsync
          RecordAccessAsync = recordAccessAsync
          PromoteAsync = promoteAsync
          EvictAsync = evictAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module TieredMemories =
    let ado
        (factory: DbConnectionFactory)
        (config: TieredMemoryConfig)
        (embeddingProvider: EmbeddingProvider option)
        : TieredMemory =
        PersistentTieredMemory.create "tiered" (EventStore.db factory "tiered") config embeddingProvider

    let file
        (baseDir: string)
        (config: TieredMemoryConfig)
        (embeddingProvider: EmbeddingProvider option)
        : TieredMemory =
        let path = System.IO.Path.Combine(baseDir, "tiered.jsonl")
        PersistentTieredMemory.create path (EventStore.file path) config embeddingProvider
