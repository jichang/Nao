namespace Nao.Runtime.Orleans.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Runtime.Orleans.Grains
open Orleans
open Orleans.Concurrency

[<TestClass>]
type SessionTurnLedgerTests() =
    let request turnId input =
        SessionTurnRequest(
            TurnId = turnId,
            LlmInput = input,
            DisplayText = input,
            AttachmentNames = [| "context.txt" |],
            Correlation = (CorrelationContext.root () |> OrleansCorrelationContext.fromDomain)
        )

    [<TestMethod>]
    member _.FingerprintBindsTurnInputConversationAgentAndCorrelation() =
        let original = request "turn-a" "hello"
        let originalHash = SessionTurnLedger.requestHash "conversation" "agent" original

        let changedInputs =
            [ request "turn-a" "different"
              request "turn-b" "hello"
              SessionTurnRequest(
                  TurnId = "turn-a",
                  LlmInput = "hello",
                  DisplayText = "different",
                  AttachmentNames = [| "context.txt" |],
                  Correlation = original.Correlation
              )
              SessionTurnRequest(
                  TurnId = "turn-a",
                  LlmInput = "hello",
                  DisplayText = "hello",
                  AttachmentNames = [| "other.txt" |],
                  Correlation = original.Correlation
              )
              SessionTurnRequest(
                  TurnId = "turn-a",
                  LlmInput = "hello",
                  DisplayText = "hello",
                  AttachmentNames = [| "context.txt" |],
                  Correlation = (CorrelationContext.root () |> OrleansCorrelationContext.fromDomain)
              ) ]

        for changed in changedInputs do
            Assert.AreNotEqual(originalHash, SessionTurnLedger.requestHash "conversation" "agent" changed)

        Assert.AreNotEqual(originalHash, SessionTurnLedger.requestHash "other-conversation" "agent" original)

        Assert.AreNotEqual(originalHash, SessionTurnLedger.requestHash "conversation" "other-agent" original)

    [<TestMethod>]
    member _.RequestValidationRejectsMissingStableIdentityAndInput() =
        let cases =
            [ request "" "hello"
              request "turn" ""
              SessionTurnRequest(
                  TurnId = "turn",
                  LlmInput = "hello",
                  DisplayText = "hello",
                  AttachmentNames = [| "" |],
                  Correlation = (CorrelationContext.root () |> OrleansCorrelationContext.fromDomain)
              ) ]

        for invalidRequest in cases do
            Assert.ThrowsExactly<ArgumentException>(fun () -> SessionTurnLedger.validateRequest invalidRequest)
            |> ignore

    [<TestMethod>]
    member _.TerminalResultPreservesSequenceAndMarksReplay() =
        let record =
            SessionTurnRecord(TurnId = "turn", Phase = SessionTurnPhase.Succeeded, Response = "done", Sequence = 42L)

        let result = SessionTurnLedger.toResult true record
        Assert.AreEqual("turn", result.TurnId)
        Assert.AreEqual(SessionTurnPhase.Succeeded, result.Phase)
        Assert.AreEqual("done", result.Response)
        Assert.AreEqual(42L, result.Sequence)
        Assert.IsTrue(result.Replayed)

    [<TestMethod>]
    member _.LifecycleScheduleAndLegalHoldGateDeletion() =
        let archiveAfter = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        let deleteAfter = archiveAfter.AddDays 30.0
        SessionLifecycle.validateSchedule archiveAfter deleteAfter

        Assert.ThrowsExactly<ArgumentException>(fun () -> SessionLifecycle.validateSchedule deleteAfter archiveAfter)
        |> ignore

        Assert.IsFalse(SessionLifecycle.deletionAllowed deleteAfter deleteAfter "legal investigation")
        Assert.IsFalse(SessionLifecycle.deletionAllowed (deleteAfter.AddTicks(-1L)) deleteAfter "")
        Assert.IsTrue(SessionLifecycle.deletionAllowed deleteAfter deleteAfter "")

    [<TestMethod>]
    member _.ActivationRecoveryInterruptsOnlyRunningTurns() =
        let accepted =
            SessionTurnRecord(TurnId = "accepted", Phase = SessionTurnPhase.Accepted)

        let running =
            SessionTurnRecord(TurnId = "running", Phase = SessionTurnPhase.Running)

        let succeeded =
            SessionTurnRecord(TurnId = "succeeded", Phase = SessionTurnPhase.Succeeded)

        Assert.IsTrue(SessionTurnLedger.recoverInterrupted [ accepted; running; succeeded ])
        Assert.AreEqual(SessionTurnPhase.Accepted, accepted.Phase)
        Assert.AreEqual(SessionTurnPhase.Interrupted, running.Phase)
        Assert.AreEqual(SessionTurnPhase.Succeeded, succeeded.Phase)
        Assert.IsFalse(SessionTurnLedger.recoverInterrupted [ accepted; running; succeeded ])

    [<TestMethod>]
    member _.OnlyLiveProgressMayInterleaveAndRetentionUsesNoReminder() =
        let methods = typeof<ISessionGrain>.GetMethods()

        let hasAlwaysInterleave name =
            methods
            |> Array.find (fun methodInfo -> methodInfo.Name = name)
            |> _.GetCustomAttributes(typeof<AlwaysInterleaveAttribute>, true)
            |> Array.isEmpty
            |> not

        Assert.IsTrue(hasAlwaysInterleave "GetLiveStepsAsync")
        Assert.IsFalse(hasAlwaysInterleave "ExecuteTurnAsync")
        Assert.IsFalse(typeof<IRemindable>.IsAssignableFrom(typeof<ISessionGrain>))
