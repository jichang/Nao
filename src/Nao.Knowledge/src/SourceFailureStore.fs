namespace Nao.Knowledge

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Nao.Agents

module SourceFailureStores =
    let private partition authorization =
        authorization |> AuthorizationScope.identity |> AuthorizationIdentity.storageKey

    let private requireOwner authorization owner =
        if AuthorizationScope.identity authorization <> owner then
            raise (UnauthorizedAccessException "Source failure record belongs to another authorization identity.")

    let inMemory () =
        let gate = obj ()

        let quarantined =
            Dictionary<string, Dictionary<SourceFailureId, QuarantinedSourceItem>>()

        let deadLettered =
            Dictionary<string, Dictionary<SourceFailureId, DeadLetteredSourceSync>>()

        let save
            (collection: Dictionary<string, Dictionary<SourceFailureId, 'T>>)
            (authorization: AuthorizationScope)
            (id: SourceFailureId)
            (value: 'T)
            (cancellationToken: CancellationToken)
            =
            cancellationToken.ThrowIfCancellationRequested()
            let key = partition authorization

            lock gate (fun () ->
                let records =
                    match collection.TryGetValue key with
                    | true, existing -> existing
                    | false, _ ->
                        let created = Dictionary()
                        collection.Add(key, created)
                        created

                match records.TryGetValue id with
                | true, existing when existing <> value ->
                    invalidOp "Source failure record ID is already bound to different evidence."
                | true, _ -> ()
                | false, _ -> records.Add(id, value))

            Task.CompletedTask

        let list
            (collection: Dictionary<string, Dictionary<SourceFailureId, 'T>>)
            (authorization: AuthorizationScope)
            (sourceId: KnowledgeSourceId)
            (getSourceId: 'T -> KnowledgeSourceId)
            (cancellationToken: CancellationToken)
            =
            cancellationToken.ThrowIfCancellationRequested()
            let key = partition authorization

            lock gate (fun () ->
                match collection.TryGetValue key with
                | true, records ->
                    records.Values
                    |> Seq.filter (fun item -> getSourceId item = sourceId)
                    |> Seq.toList
                | false, _ -> [])
            |> Task.FromResult

        { SaveQuarantinedAsync =
            fun authorization item cancellationToken ->
                requireOwner authorization (QuarantinedSourceItem.owner item)

                save quarantined authorization (QuarantinedSourceItem.id item) item cancellationToken
          SaveDeadLetteredAsync =
            fun authorization item cancellationToken ->
                requireOwner authorization (DeadLetteredSourceSync.owner item)

                save deadLettered authorization (DeadLetteredSourceSync.id item) item cancellationToken
          ListQuarantinedAsync =
            fun authorization sourceId cancellationToken ->
                list quarantined authorization sourceId QuarantinedSourceItem.sourceId cancellationToken
          ListDeadLetteredAsync =
            fun authorization sourceId cancellationToken ->
                list deadLettered authorization sourceId DeadLetteredSourceSync.sourceId cancellationToken }
