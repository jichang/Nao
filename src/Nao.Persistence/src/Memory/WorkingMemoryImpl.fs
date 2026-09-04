namespace Nao.Persistence

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Nao.Agents

module InMemoryWorkingMemory =
    let create (config: WorkingMemoryConfig) : WorkingMemory =
        let items = ConcurrentDictionary<string * string, WorkingMemoryItem>()

        let requireOwner owner =
            if String.IsNullOrWhiteSpace owner then
                invalidArg (nameof owner) "Working-memory execution ID cannot be blank."

        let key owner itemKey = owner, itemKey

        let owned owner =
            items.Values
            |> Seq.filter (fun (item: WorkingMemoryItem) -> item.ExecutionId = owner)

        let normalize (item: WorkingMemoryItem) =
            if item.Pinned then
                { item with ExpiresAt = None }
            elif item.ExpiresAt.IsNone then
                { item with
                    ExpiresAt = Some(item.AddedAt + config.DefaultTtl) }
            else
                item

        let evictOverCapacity owner =
            let excess = (owned owner |> Seq.length) - config.Capacity

            if excess > 0 then
                owned owner
                |> Seq.filter (fun item -> not item.Pinned)
                |> Seq.sortBy (fun item -> item.Attention)
                |> Seq.truncate excess
                |> Seq.iter (fun item -> items.TryRemove(key owner item.Key) |> ignore)

        let setAsync (item: WorkingMemoryItem) =
            requireOwner item.ExecutionId
            let normalized = normalize item
            items.[key item.ExecutionId item.Key] <- normalized
            evictOverCapacity item.ExecutionId
            Task.FromResult()

        let getAsync owner itemKey =
            requireOwner owner

            match items.TryGetValue(key owner itemKey) with
            | true, item ->
                let boosted =
                    { item with
                        Attention = min 1.0 (item.Attention + 0.1) }

                items.[key owner itemKey] <- boosted
                Task.FromResult(Some boosted)
            | false, _ -> Task.FromResult None

        let getAllAsync owner =
            requireOwner owner

            owned owner
            |> Seq.sortByDescending (fun item -> item.Attention)
            |> Seq.toList
            |> Task.FromResult

        let getActiveAsync owner minimum =
            requireOwner owner

            owned owner
            |> Seq.filter (fun item -> item.Attention >= minimum)
            |> Seq.sortByDescending (fun item -> item.Attention)
            |> Seq.toList
            |> Task.FromResult

        let update owner itemKey change =
            requireOwner owner

            match items.TryGetValue(key owner itemKey) with
            | true, item -> items.[key owner itemKey] <- change item
            | false, _ -> ()

            Task.FromResult()

        let focusAsync owner itemKey boost =
            update owner itemKey (fun item ->
                { item with
                    Attention = min 1.0 (item.Attention + boost) })

        let decayAsync owner asOf =
            requireOwner owner
            let mutable removed = 0

            for item in owned owner |> Seq.toArray do
                if not item.Pinned then
                    let expired = item.ExpiresAt |> Option.exists (fun expiry -> expiry < asOf)

                    let decayed =
                        { item with
                            Attention = item.Attention * (1.0 - config.DecayRate) }

                    if expired || decayed.Attention < config.EvictionThreshold then
                        items.TryRemove(key owner item.Key) |> ignore
                        removed <- removed + 1
                    else
                        items.[key owner item.Key] <- decayed

            Task.FromResult removed

        let pinAsync owner itemKey =
            update owner itemKey (fun item ->
                { item with
                    Pinned = true
                    ExpiresAt = None })

        let unpinAsync owner itemKey (asOf: DateTimeOffset) =
            update owner itemKey (fun item ->
                { item with
                    Pinned = false
                    ExpiresAt = Some(asOf + config.DefaultTtl) })

        let removeAsync owner itemKey =
            requireOwner owner
            items.TryRemove(key owner itemKey) |> ignore
            Task.FromResult()

        let delete predicate =
            let matches = items.Values |> Seq.filter predicate |> Seq.toArray

            matches
            |> Array.iter (fun (item: WorkingMemoryItem) -> items.TryRemove(key item.ExecutionId item.Key) |> ignore)

            Task.FromResult matches.Length

        let protect owner operation =
            task {
                if String.IsNullOrWhiteSpace owner then
                    return
                        Error(
                            PlatformFailure.create
                                PlatformErrorCategory.InvalidInput
                                "Working-memory execution ID cannot be blank."
                                false
                                None
                        )
                else
                    try
                        let! count = operation ()
                        return Ok count
                    with ex ->
                        return Error(PlatformFailure.fromException PlatformFailureBoundary.Storage None ex)
            }

        let deleteOwnerAsync owner =
            protect owner (fun () -> delete (fun (item: WorkingMemoryItem) -> item.ExecutionId = owner))

        let deleteExpiredAsync owner before =
            protect owner (fun () ->
                delete (fun (item: WorkingMemoryItem) ->
                    item.ExecutionId = owner
                    && not item.Pinned
                    && (item.ExpiresAt |> Option.exists (fun expiry -> expiry < before))))

        let renderContextAsync owner topK =
            requireOwner owner

            owned owner
            |> Seq.sortByDescending (fun item -> item.Attention)
            |> Seq.truncate topK
            |> Seq.mapi (fun index item ->
                sprintf "[%d] (%s, attention=%.2f) %s" (index + 1) item.Source item.Attention item.Content)
            |> String.concat "\n"
            |> Task.FromResult

        { SetAsync = setAsync
          GetAsync = getAsync
          GetAllAsync = getAllAsync
          GetActiveAsync = getActiveAsync
          FocusAsync = focusAsync
          DecayAsync = decayAsync
          PinAsync = pinAsync
          UnpinAsync = unpinAsync
          RemoveAsync = removeAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync
          RenderContextAsync = renderContextAsync }
