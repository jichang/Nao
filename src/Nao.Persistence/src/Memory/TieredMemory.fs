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
        (store: EventStore)
        (config: TieredMemoryConfig)
        (embeddingProvider: EmbeddingProvider option)
        : TieredMemory =
        let inner = InMemoryTieredMemory.create config embeddingProvider

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

        do
            store.LoadAll()
            |> Seq.map FSharpJson.deserialize<TieredDocument>
            |> Seq.iter (fun document ->
                if document.Version <> 1 then
                    invalidOp (sprintf "Unsupported tiered-memory document version: %d." document.Version)

                replay document.Event)

        let append event =
            store.Append(FSharpJson.serialize { Version = 1; Event = event })

        let appendAfter operation event =
            task {
                do! operation
                append event
            }

        let countAfter operation event =
            task {
                let! count = operation
                append event
                return count
            }

        let deleteAfter operation event =
            task {
                let! result = operation

                match result with
                | Ok _ -> append event
                | Error _ -> ()

                return result
            }

        { StoreAsync = fun entry -> appendAfter (inner.StoreAsync entry) (TieredEvent.Store entry)
          RetrieveAsync = inner.RetrieveAsync
          RetrieveFromTierAsync = inner.RetrieveFromTierAsync
          RecordAccessAsync =
            fun owner keys asOf ->
                appendAfter (inner.RecordAccessAsync owner keys asOf) (TieredEvent.RecordAccess(owner, keys, asOf))
          PromoteAsync =
            fun owner key targetTier ->
                appendAfter (inner.PromoteAsync owner key targetTier) (TieredEvent.Promote(owner, key, targetTier))
          EvictAsync = fun owner asOf -> countAfter (inner.EvictAsync owner asOf) (TieredEvent.Evict(owner, asOf))
          DeleteOwnerAsync = fun owner -> deleteAfter (inner.DeleteOwnerAsync owner) (TieredEvent.DeleteOwner owner)
          DeleteExpiredAsync =
            fun owner before ->
                deleteAfter (inner.DeleteExpiredAsync owner before) (TieredEvent.DeleteExpired(owner, before)) }

module TieredMemories =
    let ado
        (factory: DbConnectionFactory)
        (config: TieredMemoryConfig)
        (embeddingProvider: EmbeddingProvider option)
        : TieredMemory =
        PersistentTieredMemory.create (EventStore.db factory "tiered") config embeddingProvider

    let file
        (baseDir: string)
        (config: TieredMemoryConfig)
        (embeddingProvider: EmbeddingProvider option)
        : TieredMemory =
        PersistentTieredMemory.create
            (EventStore.file (System.IO.Path.Combine(baseDir, "tiered.jsonl")))
            config
            embeddingProvider
