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

        let saveAsync (turn: TurnRecord) =
            lock sync (fun () -> items.[turn.TurnId] <- turn)
            Task.CompletedTask

        let getAsync turnId =
            lock sync (fun () ->
                match items.TryGetValue turnId with
                | true, value -> Some value
                | _ -> None)
            |> Task.FromResult

        let getForSessionAsync sessionId =
            lock sync (fun () ->
                items.Values
                |> Seq.filter (fun turn -> turn.SessionId = sessionId)
                |> List.ofSeq)
            |> Task.FromResult

        let getForExecutionAsync executionId =
            lock sync (fun () ->
                items.Values
                |> Seq.filter (fun turn -> turn.Correlation.ExecutionId = executionId)
                |> List.ofSeq)
            |> Task.FromResult

        let delete (predicate: TurnRecord -> bool) =
            lock sync (fun () ->
                let keys =
                    items.Values
                    |> Seq.filter predicate
                    |> Seq.map (fun turn -> turn.TurnId)
                    |> Seq.toArray in

                keys |> Array.iter (items.Remove >> ignore)
                keys.Length)
            |> Task.FromResult

        let protect (sessionId: string) operation =
            if String.IsNullOrWhiteSpace sessionId then
                Error(
                    PlatformFailure.create PlatformErrorCategory.InvalidInput "Turn session cannot be blank." false None
                )
                |> Task.FromResult
            else
                task {
                    let! count = operation ()
                    return Ok count
                }

        let deleteSessionAsync sessionId =
            protect sessionId (fun () -> delete (fun turn -> turn.SessionId = sessionId))

        let deleteExpiredAsync sessionId before =
            protect sessionId (fun () -> delete (fun turn -> turn.SessionId = sessionId && turn.CreatedAt < before))

        { SaveAsync = saveAsync
          GetAsync = getAsync
          GetForSessionAsync = getForSessionAsync
          GetForExecutionAsync = getForExecutionAsync
          DeleteSessionAsync = deleteSessionAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module InMemoryFeedbackStore =
    let create () : FeedbackStore =
        let items = ResizeArray<Feedback>()
        let sync = obj ()

        let saveAsync (feedback: Feedback) =
            lock sync (fun () ->
                items.RemoveAll(fun item -> item.Id = feedback.Id) |> ignore
                items.Add feedback)

            Task.CompletedTask

        let getForTurnAsync (turnId: string) =
            lock sync (fun () -> items |> Seq.filter (fun feedback -> feedback.TurnId = turnId) |> List.ofSeq)
            |> Task.FromResult

        let getForSessionAsync (sessionId: string) =
            lock sync (fun () ->
                items
                |> Seq.filter (fun feedback -> feedback.SessionId = sessionId)
                |> List.ofSeq)
            |> Task.FromResult

        let getAllAsync () =
            lock sync (fun () -> List.ofSeq items) |> Task.FromResult

        let delete (predicate: Feedback -> bool) =
            lock sync (fun () -> items.RemoveAll(fun feedback -> predicate feedback))
            |> Task.FromResult

        let protect (owner: string) operation =
            if String.IsNullOrWhiteSpace owner then
                Error(
                    PlatformFailure.create
                        PlatformErrorCategory.InvalidInput
                        "Feedback owner cannot be blank."
                        false
                        None
                )
                |> Task.FromResult
            else
                task {
                    let! count = operation ()
                    return Ok count
                }

        let deleteOwnerAsync (owner: string) =
            protect owner (fun () -> delete (fun feedback -> feedback.UserId = owner))

        let deleteExpiredAsync (owner: string) before =
            protect owner (fun () -> delete (fun feedback -> feedback.UserId = owner && feedback.CreatedAt < before))

        { SaveAsync = saveAsync
          GetForTurnAsync = getForTurnAsync
          GetForSessionAsync = getForSessionAsync
          GetAllAsync = getAllAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

[<AutoOpen>]
module InMemoryFeedbackFactory =
    /// Re-exposes the in-memory FeedbackService for tests only. Production removed this
    /// factory so Nao.Feedback ships just the File and Database store categories.
    let inMemory () =
        FeedbackService.create (InMemoryTurnStore.create ()) (InMemoryFeedbackStore.create ())
