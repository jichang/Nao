namespace Nao.Persistence

open System
open System.IO
open System.Data.Common
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type EpisodicEvent =
    | Record of episode: Episode * embedding: float array
    | Link of fromId: string * toId: string
    | ForgetBelow of importanceThreshold: float
    | DeleteOwner
    | DeleteExpired of before: DateTimeOffset

type EpisodicDocument = { Version: int; Event: EpisodicEvent }

module PersistentEpisodicMemory =
    /// The host supplies a separate stream for each claim-free authorization partition key.
    let create (openStore: string -> EventStore) (embeddingProvider: EmbeddingProvider option) : EpisodicMemory =
        let sync = obj ()

        let load scope =
            let key = AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey
            let store = openStore key
            let projection = EpisodicProjection()

            let events =
                EventStream.loadCurrent
                    ("episodic/" + key)
                    1
                    FSharpJson.deserialize<EpisodicDocument>
                    (fun document -> document.Version)
                    (fun document -> document.Event)
                    store

            for event in events do
                match event with
                | EpisodicEvent.Record(episode, embedding) -> projection.Record scope episode embedding
                | EpisodicEvent.Link(fromId, toId) -> projection.Link scope fromId toId
                | EpisodicEvent.ForgetBelow threshold ->
                    projection.Delete scope (fun episode -> episode.Importance < threshold)
                    |> ignore
                | EpisodicEvent.DeleteOwner -> projection.Delete scope (fun _ -> true) |> ignore
                | EpisodicEvent.DeleteExpired before ->
                    projection.Delete scope (fun episode -> episode.Timestamp < before) |> ignore

            store, projection

        let read scope operation =
            task {
                return
                    lock sync (fun () ->
                        let _, projection = load scope
                        operation projection)
            }

        let mutate scope operation event =
            task {
                return
                    lock sync (fun () ->
                        let store, projection = load scope
                        let result = operation projection
                        let document: EpisodicDocument = { Version = 1; Event = event }
                        store.Append(FSharpJson.serialize document)
                        result)
            }

        let protect operation =
            let failure error =
                Error(PlatformFailure.fromException PlatformFailureBoundary.Storage None error)

            task {
                try
                    let! count = operation ()
                    return Ok count
                with
                | :? IOException as error -> return failure error
                | :? InvalidDataException as error -> return failure error
                | :? DbException as error -> return failure error
                | :? UnauthorizedAccessException as error -> return failure error
            }

        { RecordAsync =
            fun scope episode ->
                task {
                    do! read scope (fun _ -> ())
                    let! embedding = EpisodicEmbedding.compute embeddingProvider (EpisodicEmbedding.episodeText episode)

                    return!
                        mutate
                            scope
                            (fun projection -> projection.Record scope episode embedding)
                            (EpisodicEvent.Record(episode, embedding))
                }
          QueryAsync =
            fun scope query ->
                task {
                    match query with
                    | EpisodeQuery.BySimilarity(text, _) ->
                        do! read scope (fun _ -> ())
                        let! embedding = EpisodicEmbedding.compute embeddingProvider text
                        return! read scope (fun projection -> projection.Query scope query embedding)
                    | _ -> return! read scope (fun projection -> projection.Query scope query [||])
                }
          LinkAsync =
            fun scope fromId toId ->
                mutate scope (fun projection -> projection.Link scope fromId toId) (EpisodicEvent.Link(fromId, toId))
          GetChainAsync = fun scope id -> read scope (fun projection -> projection.GetChain scope id)
          SynthesizeAsync =
            fun scope text ->
                task {
                    do! read scope (fun _ -> ())
                    let! embedding = EpisodicEmbedding.compute embeddingProvider text
                    return! read scope (fun projection -> projection.Synthesize scope embedding)
                }
          ForgetBelowAsync =
            fun scope threshold ->
                mutate
                    scope
                    (fun projection -> projection.Delete scope (fun episode -> episode.Importance < threshold))
                    (EpisodicEvent.ForgetBelow threshold)
          DeleteOwnerAsync =
            fun scope ->
                protect (fun () ->
                    mutate scope (fun projection -> projection.Delete scope (fun _ -> true)) EpisodicEvent.DeleteOwner)
          DeleteExpiredAsync =
            fun scope before ->
                protect (fun () ->
                    mutate
                        scope
                        (fun projection -> projection.Delete scope (fun episode -> episode.Timestamp < before))
                        (EpisodicEvent.DeleteExpired before)) }

module EpisodicMemories =
    let ado (factory: DbConnectionFactory) embeddingProvider : EpisodicMemory =
        PersistentEpisodicMemory.create (fun key -> EventStore.db factory ("episodic/" + key)) embeddingProvider

    let file (baseDir: string) embeddingProvider : EpisodicMemory =
        PersistentEpisodicMemory.create
            (fun key -> EventStore.file (Path.Combine(baseDir, "episodic", key + ".jsonl")))
            embeddingProvider
