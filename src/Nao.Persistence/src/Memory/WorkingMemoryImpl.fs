namespace Nao.Persistence

open System
open System.Collections.Generic
open Nao.Agents

module internal WorkingMemoryAddressing =
    let key address =
        let authorizationKey =
            WorkingMemoryAddress.authorization address
            |> AuthorizationScope.identity
            |> AuthorizationIdentity.storageKey

        authorizationKey, WorkingMemoryAddress.executionId address |> ExecutionId.serialize

type internal WorkingMemoryProjection(config: WorkingMemoryConfig) =
    let sync = obj ()
    let items = Dictionary<(string * string) * string, WorkingMemoryItem>()
    let addressKey address = WorkingMemoryAddressing.key address
    let itemKey address key = addressKey address, key

    let owned address =
        let owner = addressKey address

        items
        |> Seq.choose (fun pair -> if fst pair.Key = owner then Some pair.Value else None)
        |> Seq.toList

    let normalize (item: WorkingMemoryItem) : WorkingMemoryItem =
        if item.Pinned then
            { item with ExpiresAt = None }
        elif item.ExpiresAt.IsNone then
            { item with
                ExpiresAt = Some(item.AddedAt + config.DefaultTtl) }
        else
            item

    let remove address (item: WorkingMemoryItem) =
        items.Remove(itemKey address item.Key) |> ignore

    let evictOverCapacity address =
        let candidates = owned address
        let excess = candidates.Length - config.Capacity

        if excess > 0 then
            candidates
            |> List.filter (fun item -> not item.Pinned)
            |> List.sortBy (fun item -> item.Attention, item.AddedAt, item.Key)
            |> List.truncate excess
            |> List.iter (remove address)

    member _.Set address (item: WorkingMemoryItem) =
        lock sync (fun () ->
            items.[itemKey address item.Key] <- normalize item
            evictOverCapacity address)

    member _.Get address key =
        lock sync (fun () ->
            match items.TryGetValue(itemKey address key) with
            | true, item -> Some item
            | false, _ -> None)

    member _.GetAll address =
        lock sync (fun () ->
            owned address
            |> List.sortByDescending (fun item -> item.Attention, item.AddedAt, item.Key))

    member this.GetActive address minimum =
        this.GetAll address |> List.filter (fun item -> item.Attention >= minimum)

    member _.Update address key (change: WorkingMemoryItem -> WorkingMemoryItem) =
        lock sync (fun () ->
            match items.TryGetValue(itemKey address key) with
            | true, item -> items.[itemKey address key] <- change item
            | false, _ -> ())

    member _.Decay address asOf =
        lock sync (fun () ->
            let mutable removed = 0

            for item in owned address do
                if not item.Pinned then
                    let expired = item.ExpiresAt |> Option.exists (fun expiry -> expiry < asOf)

                    let decayed =
                        { item with
                            Attention = item.Attention * (1.0 - config.DecayRate) }

                    if expired || decayed.Attention < config.EvictionThreshold then
                        remove address item
                        removed <- removed + 1
                    else
                        items.[itemKey address item.Key] <- decayed

            removed)

    member _.Delete address (predicate: WorkingMemoryItem -> bool) =
        lock sync (fun () ->
            let matches = owned address |> List.filter predicate
            matches |> List.iter (remove address)
            matches.Length)

    member this.Render address topK =
        this.GetAll address
        |> List.truncate topK
        |> List.mapi (fun index item ->
            sprintf "[%d] (%s, attention=%.2f) %s" (index + 1) item.Source item.Attention item.Content)
        |> String.concat "\n"

module InMemoryWorkingMemory =
    let internal validateConfig (config: WorkingMemoryConfig) =
        if config.Capacity < 0 then
            invalidArg (nameof config) "Working-memory capacity cannot be negative."

        if config.DefaultTtl < TimeSpan.Zero then
            invalidArg (nameof config) "Working-memory default TTL cannot be negative."

        if config.DecayRate < 0.0 || config.DecayRate > 1.0 then
            invalidArg (nameof config) "Working-memory decay rate must be between zero and one."

        if config.EvictionThreshold < 0.0 || config.EvictionThreshold > 1.0 then
            invalidArg (nameof config) "Working-memory eviction threshold must be between zero and one."

    let create (config: WorkingMemoryConfig) : WorkingMemory =
        validateConfig config
        let projection = WorkingMemoryProjection config

        { SetAsync = fun address item -> task { projection.Set address item }
          GetAsync = fun address key -> task { return projection.Get address key }
          GetAllAsync = fun address -> task { return projection.GetAll address }
          GetActiveAsync = fun address minimum -> task { return projection.GetActive address minimum }
          FocusAsync =
            fun address key boost ->
                task {
                    projection.Update address key (fun item ->
                        { item with
                            Attention = min 1.0 (item.Attention + boost) })
                }
          DecayAsync = fun address asOf -> task { return projection.Decay address asOf }
          PinAsync =
            fun address key ->
                task {
                    projection.Update address key (fun item ->
                        { item with
                            Pinned = true
                            ExpiresAt = None })
                }
          UnpinAsync =
            fun address key asOf ->
                task {
                    projection.Update address key (fun item ->
                        { item with
                            Pinned = false
                            ExpiresAt = Some(asOf + config.DefaultTtl) })
                }
          RemoveAsync = fun address key -> task { projection.Delete address (fun item -> item.Key = key) |> ignore }
          DeleteOwnerAsync = fun address -> task { return Ok(projection.Delete address (fun _ -> true)) }
          DeleteExpiredAsync =
            fun address before ->
                task {
                    return
                        Ok(
                            projection.Delete address (fun item ->
                                not item.Pinned
                                && (item.ExpiresAt |> Option.exists (fun expiry -> expiry < before)))
                        )
                }
          RenderContextAsync = fun address topK -> task { return projection.Render address topK } }
