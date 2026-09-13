namespace Nao.Feedback.Tests

open Nao.Agents

module InMemoryTurnStore =
    let create () =
        Nao.Persistence.InMemoryTurnStore.create ()

module InMemoryFeedbackStore =
    let create () =
        Nao.Persistence.InMemoryFeedbackStore.create ()

[<AutoOpen>]
module InMemoryFeedbackFactory =
    let inMemory () =
        FeedbackService.create (InMemoryTurnStore.create ()) (InMemoryFeedbackStore.create ())
