namespace Nao.Persistence

open System.Collections.Concurrent
open System.Threading.Tasks
open Nao.Agents

/// Persists feedback that flows over the event bus and serves the read/command side for a
/// authenticated scope. The host resolves its authenticated scope independently from the
/// model-visible event metadata before any durable write.
type FeedbackEventConsumer =
    { Consumer: EventConsumer
      FeedbackFor: AuthorizationScope -> FeedbackService }

module FeedbackEventConsumer =

    let create (authorizationFor: EventScope -> AuthorizationScope) (rootFor: string -> string) =
        let services = ConcurrentDictionary<string, FeedbackService>()

        let serviceFor (authorization: AuthorizationScope) =
            let key =
                AuthorizationScope.identity authorization |> AuthorizationIdentity.storageKey

            services.GetOrAdd(key, fun value -> FeedbackDb.file (rootFor value))

        let consumer =
            EventConsumer.create (fun (evt: NaoEvent) ->
                match evt with
                | TurnCompleted(scope, turn) ->
                    let authorization = authorizationFor scope
                    (serviceFor authorization).RecordTurnAsync authorization turn
                | _ -> Task.CompletedTask)

        { Consumer = consumer
          FeedbackFor = serviceFor }

    let feedbackFor authorization consumer = consumer.FeedbackFor authorization
