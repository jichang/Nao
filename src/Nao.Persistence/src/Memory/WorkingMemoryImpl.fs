namespace Nao.Persistence

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Nao.Agents

/// In-memory working-memory factory.
module InMemoryWorkingMemory =
    let create (config: WorkingMemoryConfig) : WorkingMemory =
        let items = ConcurrentDictionary<string, WorkingMemoryItem>()

        let evictOverCapacity () =
            if items.Count > config.Capacity then
                let toEvict =
                    items.Values
                    |> Seq.filter (fun i -> not i.Pinned)
                    |> Seq.sortBy (fun i -> i.Attention)
                    |> Seq.truncate (items.Count - config.Capacity)
                    |> Seq.toList
                for item in toEvict do
                    items.TryRemove(item.Key) |> ignore

        { SetAsync = fun (item: WorkingMemoryItem) ->
            let withExpiry =
                match item.ExpiresAt with
                | Some _ -> item
                | None -> { item with ExpiresAt = Some (DateTimeOffset.UtcNow + config.DefaultTtl) }
            items.AddOrUpdate(item.Key, withExpiry, fun _ _ -> withExpiry) |> ignore
            evictOverCapacity ()
            task { return () }

          GetAsync = fun (key: string) ->
            match items.TryGetValue(key) with
            | true, item ->
                // Boost attention on access
                let boosted = { item with Attention = min 1.0 (item.Attention + 0.1) }
                items.TryUpdate(key, boosted, item) |> ignore
                Task.FromResult(Some boosted)
            | false, _ -> Task.FromResult(None)

          GetAllAsync = fun () ->
            items.Values
            |> Seq.sortByDescending (fun i -> i.Attention)
            |> Seq.toList
            |> Task.FromResult

          GetActiveAsync = fun (minAttention: float) ->
            items.Values
            |> Seq.filter (fun i -> i.Attention >= minAttention)
            |> Seq.sortByDescending (fun i -> i.Attention)
            |> Seq.toList
            |> Task.FromResult

          FocusAsync = fun (key: string) (boost: float) ->
            match items.TryGetValue(key) with
            | true, item ->
                let focused = { item with Attention = min 1.0 (item.Attention + boost) }
                items.TryUpdate(key, focused, item) |> ignore
            | false, _ -> ()
            task { return () }

          DecayAsync = fun () ->
            task {
                let now = DateTimeOffset.UtcNow
                let mutable evicted = 0
                for kvp in items do
                    let item = kvp.Value
                    if item.Pinned then () // Skip pinned
                    else
                        // Check expiry
                        match item.ExpiresAt with
                        | Some exp when now > exp ->
                            items.TryRemove(kvp.Key) |> ignore
                            evicted <- evicted + 1
                        | _ ->
                            // Apply decay
                            let decayed = { item with Attention = item.Attention * (1.0 - config.DecayRate) }
                            if decayed.Attention < config.EvictionThreshold then
                                items.TryRemove(kvp.Key) |> ignore
                                evicted <- evicted + 1
                            else
                                items.TryUpdate(kvp.Key, decayed, item) |> ignore
                return evicted
            }

          PinAsync = fun (key: string) ->
            match items.TryGetValue(key) with
            | true, item ->
                items.TryUpdate(key, { item with Pinned = true; ExpiresAt = None }, item) |> ignore
            | false, _ -> ()
            task { return () }

          UnpinAsync = fun (key: string) ->
            match items.TryGetValue(key) with
            | true, item ->
                let unpinned =
                    { item with
                        Pinned = false
                        ExpiresAt = Some (DateTimeOffset.UtcNow + config.DefaultTtl) }
                items.TryUpdate(key, unpinned, item) |> ignore
            | false, _ -> ()
            task { return () }

          RemoveAsync = fun (key: string) ->
            items.TryRemove(key) |> ignore
            task { return () }

          ClearAsync = fun () ->
            items.Clear()
            task { return () }

          RenderContextAsync = fun (topK: int) ->
            let active =
                items.Values
                |> Seq.sortByDescending (fun i -> i.Attention)
                |> Seq.truncate topK
                |> Seq.toList
            let rendered =
                active
                |> List.mapi (fun idx item ->
                    sprintf "[%d] (%s, attention=%.2f) %s" (idx + 1) item.Source item.Attention item.Content)
                |> String.concat "\n"
            Task.FromResult(rendered) }
