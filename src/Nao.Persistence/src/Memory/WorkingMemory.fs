namespace Nao.Persistence

open System
open System.IO
open System.Data.Common
open Nao.Agents

[<RequireQualifiedAccess>]
type WorkingMemoryEvent =
    | Set of WorkingMemoryItem
    | Focus of key: string * boost: float
    | Decay of asOf: DateTimeOffset
    | Pin of key: string
    | Unpin of key: string * asOf: DateTimeOffset
    | Remove of key: string
    | DeleteOwner
    | DeleteExpired of before: DateTimeOffset

type WorkingMemoryDocument =
    { Version: int
      Policy: WorkingMemoryConfig
      Event: WorkingMemoryEvent }

module PersistentWorkingMemory =
    let create (openStore: string -> EventStore) (config: WorkingMemoryConfig) : WorkingMemory =
        InMemoryWorkingMemory.validateConfig config
        let sync = obj ()

        let load address =
            let authorizationKey, executionKey = WorkingMemoryAddressing.key address
            let streamKey = authorizationKey + "/" + executionKey
            let store = openStore streamKey
            let projection = WorkingMemoryProjection config

            let events =
                EventStream.loadCurrent
                    ("working/" + streamKey)
                    1
                    FSharpJson.deserialize<WorkingMemoryDocument>
                    (fun document -> document.Version)
                    (fun document ->
                        if document.Policy <> config then
                            raise (
                                InvalidDataException
                                    "Working-memory replay policy does not match the configured policy."
                            )

                        document.Event)
                    store

            for event in events do
                match event with
                | WorkingMemoryEvent.Set item -> projection.Set address item
                | WorkingMemoryEvent.Focus(key, boost) ->
                    projection.Update address key (fun item ->
                        { item with
                            Attention = min 1.0 (item.Attention + boost) })
                | WorkingMemoryEvent.Decay asOf -> projection.Decay address asOf |> ignore
                | WorkingMemoryEvent.Pin key ->
                    projection.Update address key (fun item ->
                        { item with
                            Pinned = true
                            ExpiresAt = None })
                | WorkingMemoryEvent.Unpin(key, asOf) ->
                    projection.Update address key (fun item ->
                        { item with
                            Pinned = false
                            ExpiresAt = Some(asOf + config.DefaultTtl) })
                | WorkingMemoryEvent.Remove key -> projection.Delete address (fun item -> item.Key = key) |> ignore
                | WorkingMemoryEvent.DeleteOwner -> projection.Delete address (fun _ -> true) |> ignore
                | WorkingMemoryEvent.DeleteExpired before ->
                    projection.Delete address (fun item ->
                        not item.Pinned
                        && (item.ExpiresAt |> Option.exists (fun expiry -> expiry < before)))
                    |> ignore

            store, projection

        let read address operation =
            task {
                return
                    lock sync (fun () ->
                        let _, projection = load address
                        operation projection)
            }

        let mutate address operation event =
            task {
                return
                    lock sync (fun () ->
                        let store, projection = load address
                        let result = operation projection

                        let document: WorkingMemoryDocument =
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
                    let! count = operation ()
                    return Ok count
                with
                | :? IOException as error -> return failure error
                | :? InvalidDataException as error -> return failure error
                | :? DbException as error -> return failure error
                | :? UnauthorizedAccessException as error -> return failure error
            }

        { SetAsync =
            fun address item ->
                mutate address (fun projection -> projection.Set address item) (WorkingMemoryEvent.Set item)
          GetAsync = fun address key -> read address (fun projection -> projection.Get address key)
          GetAllAsync = fun address -> read address (fun projection -> projection.GetAll address)
          GetActiveAsync = fun address minimum -> read address (fun projection -> projection.GetActive address minimum)
          FocusAsync =
            fun address key boost ->
                mutate
                    address
                    (fun projection ->
                        projection.Update address key (fun item ->
                            { item with
                                Attention = min 1.0 (item.Attention + boost) }))
                    (WorkingMemoryEvent.Focus(key, boost))
          DecayAsync =
            fun address asOf ->
                mutate address (fun projection -> projection.Decay address asOf) (WorkingMemoryEvent.Decay asOf)
          PinAsync =
            fun address key ->
                mutate
                    address
                    (fun projection ->
                        projection.Update address key (fun item ->
                            { item with
                                Pinned = true
                                ExpiresAt = None }))
                    (WorkingMemoryEvent.Pin key)
          UnpinAsync =
            fun address key asOf ->
                mutate
                    address
                    (fun projection ->
                        projection.Update address key (fun item ->
                            { item with
                                Pinned = false
                                ExpiresAt = Some(asOf + config.DefaultTtl) }))
                    (WorkingMemoryEvent.Unpin(key, asOf))
          RemoveAsync =
            fun address key ->
                mutate
                    address
                    (fun projection -> projection.Delete address (fun item -> item.Key = key) |> ignore)
                    (WorkingMemoryEvent.Remove key)
          DeleteOwnerAsync =
            fun address ->
                protect (fun () ->
                    mutate
                        address
                        (fun projection -> projection.Delete address (fun _ -> true))
                        WorkingMemoryEvent.DeleteOwner)
          DeleteExpiredAsync =
            fun address before ->
                protect (fun () ->
                    mutate
                        address
                        (fun projection ->
                            projection.Delete address (fun item ->
                                not item.Pinned
                                && (item.ExpiresAt |> Option.exists (fun expiry -> expiry < before))))
                        (WorkingMemoryEvent.DeleteExpired before))
          RenderContextAsync = fun address topK -> read address (fun projection -> projection.Render address topK) }

module WorkingMemories =
    let ado (factory: DbConnectionFactory) config : WorkingMemory =
        PersistentWorkingMemory.create (fun key -> EventStore.db factory ("working/" + key)) config

    let file (baseDir: string) config : WorkingMemory =
        PersistentWorkingMemory.create
            (fun key -> EventStore.file (Path.Combine(baseDir, "working", key + ".jsonl")))
            config
