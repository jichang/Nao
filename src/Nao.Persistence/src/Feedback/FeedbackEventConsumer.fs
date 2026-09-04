namespace Nao.Persistence

open System.Collections.Concurrent
open System.Threading.Tasks
open Nao.Agents

/// Persists feedback that flows over the event bus and serves the read/command side for a
/// session. It is BOTH an event consumer (the write side — it persists TurnCompleted
/// under each session's folder) AND the provider of the read/command
/// FeedbackService for a session (the query side). The folder is derived from the event's
/// session key by `rootFor`; the backing FeedbackService is the store-level swap point (File
/// today, Database later), so changing where data lands needs no producer change.
type FeedbackEventConsumer =
    { Consumer: EventConsumer
      FeedbackFor: string -> FeedbackService }

module FeedbackEventConsumer =

    let create (rootFor: string -> string) =
        let services = ConcurrentDictionary<string, FeedbackService>()

        let serviceFor (sessionKey: string) =
            services.GetOrAdd(rootFor sessionKey, fun dir -> FeedbackDb.file dir)

        let consumer =
            EventConsumer.create (fun (evt: NaoEvent) ->
                match evt with
                | TurnCompleted(scope, turn) -> (serviceFor scope.SessionKey).RecordTurnAsync turn
                | _ -> Task.CompletedTask)

        { Consumer = consumer
          FeedbackFor = serviceFor }

    let feedbackFor sessionKey consumer = consumer.FeedbackFor sessionKey
