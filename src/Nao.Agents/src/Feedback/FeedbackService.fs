namespace Nao.Agents

open System
open System.IO
open System.Threading.Tasks
open Nao.Agents
open Nao.Agents

/// High-level facade that ties the feedback loop together:
///   1. record each completed turn,
///   2. accept user feedback and store it.
///
/// Feedback is recorded only; nothing is changed at runtime. Analysis of stored feedback
/// (to improve or create tools/agents) is left to a separate, opt-in system built on top.
///
/// Construct it with whichever stores you like (file-backed for the running app, database
/// for a shared deployment). The default factories wire sensible implementations.
type FeedbackService
    (turnStore: ITurnStore,
     feedbackStore: IFeedbackStore) =

    // ----- Turns & feedback --------------------------------------------------

    /// Persist a completed turn so feedback can later be analysed against it.
    member _.RecordTurnAsync(turn: TurnRecord) : Task = turnStore.SaveAsync turn

    /// Persist a raw feedback entry. Used by the event-storage consumer to record feedback.
    member _.SaveFeedbackAsync(feedback: Feedback) : Task = feedbackStore.SaveAsync feedback

    /// Record explicit user feedback. The entry is stored only; it never mutates a tool or
    /// agent at runtime — analysis and improvements are left to a separate offline system.
    member _.SubmitFeedbackAsync(feedback: Feedback) : Task = feedbackStore.SaveAsync feedback
