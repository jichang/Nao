namespace Nao.Persistence

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Nao.Agents

type private PermissionGrantEvent =
    | GrantSaved of Version: int * PermissionGrant
    | GrantRevoked of Version: int * GrantId: Guid * RevokedAt: DateTimeOffset
    | ExpiredDeleted of Version: int * Before: DateTimeOffset
    | ScopeDeleted of Version: int

[<RequireQualifiedAccess>]
module PermissionGrantStores =
    let private eventVersion = 1

    let private applyEvent grants event =
        match event with
        | GrantSaved(version, grant) when version = eventVersion ->
            PermissionGrant.validate grant
            Map.add grant.Id grant grants
        | GrantRevoked(version, grantId, revokedAt) when version = eventVersion ->
            match Map.tryFind grantId grants with
            | Some grant when grant.RevokedAt.IsNone ->
                Map.add
                    grantId
                    { grant with
                        RevokedAt = Some revokedAt }
                    grants
            | _ -> grants
        | ExpiredDeleted(version, before) when version = eventVersion ->
            grants
            |> Map.filter (fun _ grant -> grant.ExpiresAt |> Option.forall (fun expiresAt -> expiresAt > before))
        | ScopeDeleted version when version = eventVersion -> Map.empty
        | GrantSaved(version, _)
        | GrantRevoked(version, _, _)
        | ExpiredDeleted(version, _)
        | ScopeDeleted version -> invalidOp $"Unsupported permission grant event version {version}."

    let private replay (store: EventStore) =
        store.LoadAll()
        |> List.map FSharpJson.deserialize<PermissionGrantEvent>
        |> List.fold applyEvent Map.empty

    let private applicablePartitions authorization =
        [ PermissionGrantScope.Session
          PermissionGrantScope.Workspace
          PermissionGrantScope.Durable ]
        |> List.choose (fun scope ->
            try
                Some(PermissionGrantPartition.storageKey scope authorization)
            with :? ArgumentException ->
                None)
        |> Set.ofList

    let private eventSourced (storeForPartition: string -> EventStore) : PermissionGrantStore =
        let locks = ConcurrentDictionary<string, obj>()

        let streamLock partition =
            locks.GetOrAdd(partition, fun _ -> obj ())

        let loadPartition partition =
            lock (streamLock partition) (fun () -> storeForPartition partition |> replay)

        { SaveAsync =
            fun authorization grant ->
                PermissionGrant.validate grant
                let partition = PermissionGrantPartition.storageKey grant.Scope authorization

                lock (streamLock partition) (fun () ->
                    let store = storeForPartition partition
                    let grants = replay store

                    if Map.containsKey grant.Id grants then
                        invalidOp $"Permission grant '{grant.Id}' already exists."

                    if grant.RevokedAt.IsSome then
                        invalidArg (nameof grant) "A new permission grant cannot already be revoked."

                    GrantSaved(eventVersion, grant) |> FSharpJson.serialize |> store.Append)

                Task.CompletedTask
          GetActiveAsync =
            fun authorization at ->
                applicablePartitions authorization
                |> Seq.collect (fun partition -> loadPartition partition |> Map.values)
                |> Seq.filter (PermissionGrant.isActive at)
                |> Seq.sortBy _.GrantedAt
                |> Seq.toList
                |> Task.FromResult
          RevokeAsync =
            fun authorization grantId revokedAt ->
                let result =
                    applicablePartitions authorization
                    |> Seq.tryPick (fun partition ->
                        lock (streamLock partition) (fun () ->
                            let store = storeForPartition partition

                            match replay store |> Map.tryFind grantId with
                            | Some grant when grant.RevokedAt.IsNone ->
                                GrantRevoked(eventVersion, grantId, revokedAt)
                                |> FSharpJson.serialize
                                |> store.Append

                                Some true
                            | Some _ -> Some false
                            | None -> None))
                    |> Option.defaultValue false

                Task.FromResult result
          DeleteExpiredAsync =
            fun authorization before ->
                let mutable deleted = 0

                for partition in applicablePartitions authorization do
                    lock (streamLock partition) (fun () ->
                        let store = storeForPartition partition

                        let expired =
                            replay store
                            |> Map.values
                            |> Seq.filter (fun grant ->
                                grant.ExpiresAt |> Option.exists (fun expiresAt -> expiresAt <= before))
                            |> Seq.length

                        if expired > 0 then
                            ExpiredDeleted(eventVersion, before) |> FSharpJson.serialize |> store.Append

                            deleted <- deleted + expired)

                Task.FromResult deleted
          DeleteScopeAsync =
            fun authorization scope ->
                let partition = PermissionGrantPartition.storageKey scope authorization

                let deleted =
                    lock (streamLock partition) (fun () ->
                        let store = storeForPartition partition
                        let count = replay store |> Map.count

                        if count > 0 then
                            ScopeDeleted eventVersion |> FSharpJson.serialize |> store.Append

                        count)

                Task.FromResult deleted }

    let file rootDirectory =
        eventSourced (fun partition ->
            IO.Path.Combine(rootDirectory, "permission-grants", $"{partition}.jsonl")
            |> EventStore.file)

    let db factory =
        eventSourced (fun partition -> EventStore.db factory $"permission-grants-{partition}")
