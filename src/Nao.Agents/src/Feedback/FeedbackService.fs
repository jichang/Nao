namespace Nao.Agents

open System.Threading.Tasks

/// High-level facade that ties the feedback loop together:
///   1. record each completed turn,
///   2. accept user feedback and store it.
///
/// Feedback is recorded only; nothing is changed at runtime. Analysis of stored feedback
/// (to improve or create tools/agents) is left to a separate, opt-in system built on top.
///
/// Construct it with whichever stores you like (file-backed for the running app, database
/// for a shared deployment). The default factories wire sensible implementations.
type FeedbackService =
    { RecordTurnAsync: TurnRecord -> Task
      SaveFeedbackAsync: Feedback -> Task
      SubmitFeedbackAsync: Feedback -> Task
      DeleteSessionAsync: string -> Task<Result<int, PlatformFailure>> }

module FeedbackService =

    let create (turnStore: TurnStore) (feedbackStore: FeedbackStore) : FeedbackService =
        { RecordTurnAsync = turnStore.SaveAsync
          SaveFeedbackAsync = feedbackStore.SaveAsync
          SubmitFeedbackAsync = feedbackStore.SaveAsync
          DeleteSessionAsync = turnStore.DeleteSessionAsync }
