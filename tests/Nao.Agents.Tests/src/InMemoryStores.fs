namespace Nao.Feedback.Tests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Nao.Agents

// In-memory feedback stores live in the test project only. Production (Nao.Feedback)
// ships exactly two store categories — file system (File*) and database (Ado*); these
// volatile variants exist solely to keep the unit tests fast and isolated.

module InMemoryTurnStore =
    let create () : TurnStore =
        let items = Dictionary<string, TurnRecord>()
        let sync = obj ()
        { SaveAsync = fun turn ->
            lock sync (fun () -> items.[turn.TurnId] <- turn)
            Task.CompletedTask
          GetAsync = fun turnId ->
            lock sync (fun () ->
                match items.TryGetValue turnId with
                | true, v -> Some v
                | _ -> None)
            |> Task.FromResult
          GetForSessionAsync = fun sessionId ->
            lock sync (fun () ->
                items.Values |> Seq.filter (fun t -> t.SessionId = sessionId) |> List.ofSeq)
            |> Task.FromResult }

module InMemoryFeedbackStore =
    let create () : FeedbackStore =
        let items = ResizeArray<Feedback>()
        let sync = obj ()
        { SaveAsync = fun feedback ->
            lock sync (fun () -> items.Add feedback)
            Task.CompletedTask
          GetForTurnAsync = fun turnId ->
            lock sync (fun () -> items |> Seq.filter (fun f -> f.TurnId = turnId) |> List.ofSeq)
            |> Task.FromResult
          GetForSessionAsync = fun sessionId ->
            lock sync (fun () -> items |> Seq.filter (fun f -> f.SessionId = sessionId) |> List.ofSeq)
            |> Task.FromResult
          GetAllAsync = fun () -> lock sync (fun () -> List.ofSeq items) |> Task.FromResult }

[<AutoOpen>]
module InMemoryFeedbackFactory =
    /// Re-exposes the in-memory FeedbackService for tests only. Production removed this
    /// factory so Nao.Feedback ships just the File and Database store categories.
    let inMemory () =
        FeedbackService.create (InMemoryTurnStore.create ()) (InMemoryFeedbackStore.create ())
