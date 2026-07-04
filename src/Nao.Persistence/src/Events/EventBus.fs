namespace Nao.Persistence

open System.Threading.Tasks
open Nao.Agents

/// In-process synchronous bus: publishing awaits every consumer so persistence is
/// deterministic for the desktop app. A failing consumer is isolated (its exception is
/// swallowed) so one bad sink never breaks a producer's turn.
type InMemoryEventBus() =
    let consumers = ResizeArray<IEventConsumer>()
    let gate = obj ()

    interface IEventBus with
        member _.Subscribe(consumer: IEventConsumer) =
            lock gate (fun () -> consumers.Add consumer)

        member _.Unsubscribe(consumer: IEventConsumer) =
            lock gate (fun () -> consumers.Remove consumer |> ignore)

        member _.PublishAsync(evt: NaoEvent) : Task =
            task {
                let snapshot = lock gate (fun () -> consumers.ToArray())
                for c in snapshot do
                    try
                        do! c.HandleAsync evt
                    with _ ->
                        // Isolate consumers: a storage strategy failing must not abort the
                        // producer's turn. (No logger in this layer; swallow by design.)
                        ()
            } :> Task
