namespace Nao.Persistence

open System
open System.IO
open System.Data.Common
open Nao.Agents

[<RequireQualifiedAccess>]
type TieredEvent =
    | Store of TieredMemoryEntry
    | RecordAccess of keys: string list * asOf: DateTimeOffset
    | Promote of key: string * targetTier: MemoryTier
    | Evict of asOf: DateTimeOffset
    | DeleteOwner
    | DeleteExpired of before: DateTimeOffset

type TieredDocument =
    { Version: int
      Policy: TieredMemoryConfig
      Event: TieredEvent }

module PersistentTieredMemory =
    /// The host supplies a separate stream for each claim-free authorization partition key.
    let create
        (openStore: string -> EventStore)
        (config: TieredMemoryConfig)
        (embeddingProvider: EmbeddingProvider option)
        : TieredMemory =
        InMemoryTieredMemory.validateConfig config
        let sync = obj ()

        let load scope =
            let key = AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey
            let store = openStore key
            let inner = InMemoryTieredMemory.create config embeddingProvider

            let events =
                EventStream.loadCurrent
                    ("tiered/" + key)
                    1
                    FSharpJson.deserialize<TieredDocument>
                    (fun document -> document.Version)
                    (fun document ->
                        if document.Policy <> config then
                            raise (
                                InvalidDataException
                                    "Tiered-memory replay policy does not match the configured policy."
                            )

                        document.Event)
                    store

            for event in events do
                match event with
                | TieredEvent.Store entry -> inner.StoreAsync scope entry |> _.GetAwaiter().GetResult()
                | TieredEvent.RecordAccess(keys, asOf) ->
                    inner.RecordAccessAsync scope keys asOf |> _.GetAwaiter().GetResult()
                | TieredEvent.Promote(key, tier) -> inner.PromoteAsync scope key tier |> _.GetAwaiter().GetResult()
                | TieredEvent.Evict asOf -> inner.EvictAsync scope asOf |> _.GetAwaiter().GetResult() |> ignore
                | TieredEvent.DeleteOwner -> inner.DeleteOwnerAsync scope |> _.GetAwaiter().GetResult() |> ignore
                | TieredEvent.DeleteExpired before ->
                    inner.DeleteExpiredAsync scope before |> _.GetAwaiter().GetResult() |> ignore

            store, inner

        let read scope operation =
            task {
                return
                    lock sync (fun () ->
                        let _, inner = load scope
                        operation inner)
            }

        let mutate scope operation event =
            task {
                return
                    lock sync (fun () ->
                        let store, inner = load scope
                        let result = operation inner

                        let document: TieredDocument =
                            { Version = 1
                              Policy = config
                              Event = event }

                        store.Append(FSharpJson.serialize document)
                        result)
            }

        let protect operation =
            let failure error =
                Error(PlatformFailure.fromException PlatformFailureBoundary.Storage None error)

            task {
                try
                    return! operation ()
                with
                | :? IOException as error -> return failure error
                | :? InvalidDataException as error -> return failure error
                | :? DbException as error -> return failure error
                | :? UnauthorizedAccessException as error -> return failure error
            }

        { StoreAsync =
            fun scope entry ->
                mutate
                    scope
                    (fun inner -> inner.StoreAsync scope entry |> _.GetAwaiter().GetResult())
                    (TieredEvent.Store entry)
          RetrieveAsync =
            fun scope query count ->
                task {
                    let! inner = read scope id
                    return! inner.RetrieveAsync scope query count
                }
          RetrieveFromTierAsync =
            fun scope tier count ->
                read scope (fun inner -> inner.RetrieveFromTierAsync scope tier count |> _.GetAwaiter().GetResult())
          RecordAccessAsync =
            fun scope keys asOf ->
                mutate
                    scope
                    (fun inner -> inner.RecordAccessAsync scope keys asOf |> _.GetAwaiter().GetResult())
                    (TieredEvent.RecordAccess(keys, asOf))
          PromoteAsync =
            fun scope key tier ->
                mutate
                    scope
                    (fun inner -> inner.PromoteAsync scope key tier |> _.GetAwaiter().GetResult())
                    (TieredEvent.Promote(key, tier))
          EvictAsync =
            fun scope asOf ->
                mutate
                    scope
                    (fun inner -> inner.EvictAsync scope asOf |> _.GetAwaiter().GetResult())
                    (TieredEvent.Evict asOf)
          DeleteOwnerAsync =
            fun scope ->
                protect (fun () ->
                    mutate
                        scope
                        (fun inner -> inner.DeleteOwnerAsync scope |> _.GetAwaiter().GetResult())
                        TieredEvent.DeleteOwner)
          DeleteExpiredAsync =
            fun scope before ->
                protect (fun () ->
                    mutate
                        scope
                        (fun inner -> inner.DeleteExpiredAsync scope before |> _.GetAwaiter().GetResult())
                        (TieredEvent.DeleteExpired before)) }

module TieredMemories =
    let ado (factory: DbConnectionFactory) config embeddingProvider : TieredMemory =
        PersistentTieredMemory.create (fun key -> EventStore.db factory ("tiered/" + key)) config embeddingProvider

    let file (baseDir: string) config embeddingProvider : TieredMemory =
        PersistentTieredMemory.create
            (fun key -> EventStore.file (Path.Combine(baseDir, "tiered", key + ".jsonl")))
            config
            embeddingProvider
