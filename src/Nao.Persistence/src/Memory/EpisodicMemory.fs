namespace Nao.Persistence

open System
open Nao.Agents

[<RequireQualifiedAccess>]
type EpisodicEvent =
    | Record of Episode
    | Link of owner: string * fromId: string * toId: string
    | ForgetBelow of owner: string * importanceThreshold: float
    | DeleteOwner of owner: string
    | DeleteExpired of owner: string * before: DateTimeOffset

type EpisodicDocument = { Version: int; Event: EpisodicEvent }

module PersistentEpisodicMemory =
    let create context (store: EventStore) (embeddingProvider: EmbeddingProvider option) : EpisodicMemory =
        let inner = InMemoryEpisodicMemory.create embeddingProvider

        let loadEvents () =
            EventStream.loadCurrent
                context
                1
                FSharpJson.deserialize<EpisodicDocument>
                (fun document -> document.Version)
                (fun document -> document.Event)
                store

        let replay event =
            match event with
            | EpisodicEvent.Record episode -> inner.RecordAsync episode |> _.GetAwaiter().GetResult()
            | EpisodicEvent.Link(owner, fromId, toId) -> inner.LinkAsync owner fromId toId |> _.GetAwaiter().GetResult()
            | EpisodicEvent.ForgetBelow(owner, threshold) ->
                inner.ForgetBelowAsync owner threshold |> _.GetAwaiter().GetResult() |> ignore
            | EpisodicEvent.DeleteOwner owner -> inner.DeleteOwnerAsync owner |> _.GetAwaiter().GetResult() |> ignore
            | EpisodicEvent.DeleteExpired(owner, before) ->
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

        let linkAsync owner fromId toId =
            appendAfter (fun () -> inner.LinkAsync owner fromId toId) (EpisodicEvent.Link(owner, fromId, toId))

        let forgetBelowAsync owner threshold =
            countAfter (fun () -> inner.ForgetBelowAsync owner threshold) (EpisodicEvent.ForgetBelow(owner, threshold))

        let deleteOwnerAsync owner =
            deleteAfter (fun () -> inner.DeleteOwnerAsync owner) (EpisodicEvent.DeleteOwner owner)

        let deleteExpiredAsync owner before =
            deleteAfter (fun () -> inner.DeleteExpiredAsync owner before) (EpisodicEvent.DeleteExpired(owner, before))

        { RecordAsync = fun episode -> appendAfter (fun () -> inner.RecordAsync episode) (EpisodicEvent.Record episode)
          QueryAsync = inner.QueryAsync
          LinkAsync = linkAsync
          GetChainAsync = inner.GetChainAsync
          SynthesizeAsync = inner.SynthesizeAsync
          ForgetBelowAsync = forgetBelowAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module EpisodicMemories =
    let ado (factory: DbConnectionFactory) (embeddingProvider: EmbeddingProvider option) : EpisodicMemory =
        PersistentEpisodicMemory.create "episodic" (EventStore.db factory "episodic") embeddingProvider

    let file (baseDir: string) (embeddingProvider: EmbeddingProvider option) : EpisodicMemory =
        let path = System.IO.Path.Combine(baseDir, "episodic.jsonl")
        PersistentEpisodicMemory.create path (EventStore.file path) embeddingProvider
