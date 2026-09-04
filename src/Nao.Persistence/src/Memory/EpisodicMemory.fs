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
    let create (store: EventStore) (embeddingProvider: EmbeddingProvider option) : EpisodicMemory =
        let inner = InMemoryEpisodicMemory.create embeddingProvider

        let replay event =
            match event with
            | EpisodicEvent.Record episode -> inner.RecordAsync episode |> _.GetAwaiter().GetResult()
            | EpisodicEvent.Link(owner, fromId, toId) -> inner.LinkAsync owner fromId toId |> _.GetAwaiter().GetResult()
            | EpisodicEvent.ForgetBelow(owner, threshold) ->
                inner.ForgetBelowAsync owner threshold |> _.GetAwaiter().GetResult() |> ignore
            | EpisodicEvent.DeleteOwner owner -> inner.DeleteOwnerAsync owner |> _.GetAwaiter().GetResult() |> ignore
            | EpisodicEvent.DeleteExpired(owner, before) ->
                inner.DeleteExpiredAsync owner before |> _.GetAwaiter().GetResult() |> ignore

        do
            store.LoadAll()
            |> Seq.map FSharpJson.deserialize<EpisodicDocument>
            |> Seq.iter (fun document ->
                if document.Version <> 1 then
                    invalidOp (sprintf "Unsupported episodic-memory document version: %d." document.Version)

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

        { RecordAsync = fun episode -> appendAfter (inner.RecordAsync episode) (EpisodicEvent.Record episode)
          QueryAsync = inner.QueryAsync
          LinkAsync =
            fun owner fromId toId ->
                appendAfter (inner.LinkAsync owner fromId toId) (EpisodicEvent.Link(owner, fromId, toId))
          GetChainAsync = inner.GetChainAsync
          SynthesizeAsync = inner.SynthesizeAsync
          ForgetBelowAsync =
            fun owner threshold ->
                countAfter (inner.ForgetBelowAsync owner threshold) (EpisodicEvent.ForgetBelow(owner, threshold))
          DeleteOwnerAsync = fun owner -> deleteAfter (inner.DeleteOwnerAsync owner) (EpisodicEvent.DeleteOwner owner)
          DeleteExpiredAsync =
            fun owner before ->
                deleteAfter (inner.DeleteExpiredAsync owner before) (EpisodicEvent.DeleteExpired(owner, before)) }

module EpisodicMemories =
    let ado (factory: DbConnectionFactory) (embeddingProvider: EmbeddingProvider option) : EpisodicMemory =
        PersistentEpisodicMemory.create (EventStore.db factory "episodic") embeddingProvider

    let file (baseDir: string) (embeddingProvider: EmbeddingProvider option) : EpisodicMemory =
        PersistentEpisodicMemory.create
            (EventStore.file (System.IO.Path.Combine(baseDir, "episodic.jsonl")))
            embeddingProvider
