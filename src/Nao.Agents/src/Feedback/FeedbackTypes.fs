namespace Nao.Agents

open System

/// Whether the user was satisfied with a turn.
[<RequireQualifiedAccess>]
type FeedbackSentiment =
    | Positive
    | Negative
    | Neutral

/// User feedback attached to a turn. The signal that drives adjustments.
type Feedback =
    { /// Unique identifier for this feedback entry.
      Id: Guid
      /// Turn this feedback refers to.
      TurnId: string
      /// Session the turn belonged to.
      SessionId: string
      /// User who gave the feedback.
      UserId: string
      /// Positive / negative / neutral.
      Sentiment: FeedbackSentiment
      /// Optional free-text explanation from the user.
      Comment: string option
      /// When the feedback was given.
      CreatedAt: DateTimeOffset
      /// Arbitrary extra context.
      Metadata: Map<string, string> }

/// Where a feedback signal originated. Explicit feedback is an intentional good/bad
/// rating; conversation feedback is inferred heuristically from the chat history;
/// memory feedback is surfaced by the memory system. The source is stored on each
/// `Feedback` in its `Metadata` (see the `FeedbackSource` module) so the cross-session
/// aggregator can weigh and explain its suggestions without breaking existing literals.
module FeedbackSource =
    /// Metadata key under which the source marker is stored on a `Feedback`.
    [<Literal>]
    let Key = "source"

    [<Literal>]
    let Explicit = "explicit"

    [<Literal>]
    let Conversation = "conversation"

    [<Literal>]
    let Memory = "memory"

    /// Read the source marker from a feedback entry (defaults to explicit).
    let ofFeedback (f: Feedback) : string =
        match f.Metadata.TryFind Key with
        | Some v -> v
        | None -> Explicit

    /// Stamp a source marker into a metadata map.
    let stamp (source: string) (metadata: Map<string, string>) : Map<string, string> =
        metadata |> Map.add Key source
