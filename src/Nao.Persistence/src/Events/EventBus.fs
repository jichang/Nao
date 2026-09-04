namespace Nao.Persistence

open System.Threading.Tasks
open Nao.Agents

/// In-process sequential bus. Dispatch uses a subscription snapshot and isolates a failing
/// consumer so one bad sink never breaks a producer's turn.
module InMemoryEventBus =

    let create () : EventBus =
        let consumers = ResizeArray<EventConsumer>()
        let gate = obj ()

        let subscribe consumer =
            lock gate (fun () -> consumers.Add consumer)

        let unsubscribe consumer =
            lock gate (fun () ->
                let index =
                    consumers.FindIndex(fun candidate -> EventConsumer.sameIdentity candidate consumer)

                if index >= 0 then
                    consumers.RemoveAt index)

        let publishAsync (evt: NaoEvent) : Task =
            task {
                let snapshot = lock gate (fun () -> consumers.ToArray())

                for c in snapshot do
                    try
                        do! EventConsumer.handleAsync evt c
                    with _ ->
                        // Isolate consumers: a storage strategy failing must not abort the
                        // producer's turn. (No logger in this layer; swallow by design.)
                        ()
            }
            :> Task

        EventBus.create publishAsync subscribe unsubscribe
