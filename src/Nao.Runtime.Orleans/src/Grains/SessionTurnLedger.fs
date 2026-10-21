namespace Nao.Runtime.Orleans.Grains

open System
open System.Security.Cryptography
open System.Text
open Nao.Agents
open Orleans

[<RequireQualifiedAccess>]
module SessionTurnPhase =
    [<Literal>]
    let Accepted = "accepted"

    [<Literal>]
    let Running = "running"

    [<Literal>]
    let Interrupted = "interrupted"

    [<Literal>]
    let Succeeded = "succeeded"

    [<Literal>]
    let Failed = "failed"

    let isTerminal phase = phase = Succeeded || phase = Failed

[<RequireQualifiedAccess>]
module SessionLifecyclePhase =
    [<Literal>]
    let Active = "active"

    [<Literal>]
    let Paused = "paused"

    [<Literal>]
    let Archived = "archived"

[<GenerateSerializer>]
type SessionLifecycleSnapshot() =
    [<Id(0u)>]
    member val Phase: string = SessionLifecyclePhase.Active with get, set

    [<Id(1u)>]
    member val ArchiveAfter: DateTimeOffset = DateTimeOffset.MaxValue with get, set

    [<Id(2u)>]
    member val DeleteAfter: DateTimeOffset = DateTimeOffset.MaxValue with get, set

    [<Id(3u)>]
    member val LegalHoldReason: string = "" with get, set

    [<Id(4u)>]
    member val ArchivedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

[<GenerateSerializer>]
type SessionTurnRequest() =
    [<Id(0u)>]
    member val TurnId: string = "" with get, set

    [<Id(1u)>]
    member val LlmInput: string = "" with get, set

    [<Id(2u)>]
    member val DisplayText: string = "" with get, set

    [<Id(3u)>]
    member val AttachmentNames: string[] = [||] with get, set

    [<Id(4u)>]
    member val Correlation: OrleansCorrelationContext = OrleansCorrelationContext() with get, set

[<GenerateSerializer>]
type SessionTurnRecord() =
    [<Id(0u)>]
    member val TenantId: string = "" with get, set

    [<Id(1u)>]
    member val WorkspaceId: string = "" with get, set

    [<Id(2u)>]
    member val UserId: string = "" with get, set

    [<Id(3u)>]
    member val SessionId: string = "" with get, set

    [<Id(4u)>]
    member val TurnId: string = "" with get, set

    [<Id(5u)>]
    member val IdempotencyKey: string = "" with get, set

    [<Id(6u)>]
    member val RequestHash: string = "" with get, set

    [<Id(7u)>]
    member val ConversationName: string = "" with get, set

    [<Id(8u)>]
    member val AgentName: string = "" with get, set

    [<Id(9u)>]
    member val DisplayText: string = "" with get, set

    [<Id(10u)>]
    member val AttachmentNames: string[] = [||] with get, set

    [<Id(11u)>]
    member val Correlation: CorrelationContext = Unchecked.defaultof<_> with get, set

    [<Id(12u)>]
    member val Phase: string = SessionTurnPhase.Accepted with get, set

    [<Id(13u)>]
    member val Response: string = "" with get, set

    [<Id(14u)>]
    member val Failure: string = "" with get, set

    [<Id(15u)>]
    member val AcceptedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

    [<Id(16u)>]
    member val StartedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

    [<Id(17u)>]
    member val CompletedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

    [<Id(18u)>]
    member val Sequence: int64 = 0L with get, set

    [<Id(19u)>]
    member val PermissionGrantIds: string[] = [||] with get, set

    [<Id(20u)>]
    member val MemoryKeys: string[] = [||] with get, set

    [<Id(21u)>]
    member val ArtifactIds: string[] = [||] with get, set

    [<Id(22u)>]
    member val TraceExecutionIds: string[] = [||] with get, set

[<GenerateSerializer>]
type SessionTurnResult() =
    [<Id(0u)>]
    member val TurnId: string = "" with get, set

    [<Id(1u)>]
    member val Phase: string = "" with get, set

    [<Id(2u)>]
    member val Response: string = "" with get, set

    [<Id(3u)>]
    member val Failure: string = "" with get, set

    [<Id(4u)>]
    member val Replayed: bool = false with get, set

    [<Id(5u)>]
    member val Sequence: int64 = 0L with get, set

exception internal SessionTurnRecovered of SessionTurnResult

[<RequireQualifiedAccess>]
module SessionTurnLedger =
    let validateRequest (request: SessionTurnRequest) =
        if isNull (box request) then
            nullArg (nameof request)

        TurnId.parse request.TurnId |> ignore

        if String.IsNullOrWhiteSpace request.LlmInput then
            invalidArg (nameof request.LlmInput) "Turn input must be non-blank."

        if isNull request.DisplayText then
            nullArg (nameof request.DisplayText)

        if isNull request.AttachmentNames then
            nullArg (nameof request.AttachmentNames)

        if isNull (box request.Correlation) then
            nullArg (nameof request.Correlation)

        request.AttachmentNames
        |> Array.iter (fun attachment ->
            if String.IsNullOrWhiteSpace attachment then
                invalidArg (nameof request.AttachmentNames) "Attachment names must be non-blank.")

    let requestHash (conversationName: string) (agentName: string) (request: SessionTurnRequest) =
        let values =
            [| conversationName
               agentName
               request.TurnId
               request.LlmInput
               request.DisplayText
               request.Correlation.ExecutionId
               request.Correlation.CorrelationId
               request.Correlation.CausationExecutionId
               string request.Correlation.Attempt
               yield! request.AttachmentNames |]

        let canonical =
            values
            |> Array.map (fun value -> sprintf "%d:%s" value.Length value)
            |> String.concat ""

        canonical
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let toResult replayed (record: SessionTurnRecord) =
        SessionTurnResult(
            TurnId = record.TurnId,
            Phase = record.Phase,
            Response = record.Response,
            Failure = record.Failure,
            Replayed = replayed,
            Sequence = record.Sequence
        )

    let recoverInterrupted (turns: seq<SessionTurnRecord>) =
        let mutable changed = false

        for turn in turns do
            if turn.Phase = SessionTurnPhase.Running then
                turn.Phase <- SessionTurnPhase.Interrupted
                changed <- true

        changed

[<RequireQualifiedAccess>]
module SessionLifecycle =
    let validateSchedule archiveAfter deleteAfter =
        if archiveAfter = DateTimeOffset.MinValue || deleteAfter = DateTimeOffset.MinValue then
            invalidArg (nameof archiveAfter) "Lifecycle deadlines must be explicit."

        if deleteAfter < archiveAfter then
            invalidArg (nameof deleteAfter) "Session deletion cannot precede archival."

    let snapshot phase archiveAfter deleteAfter legalHoldReason archivedAt =
        SessionLifecycleSnapshot(
            Phase = phase,
            ArchiveAfter = archiveAfter,
            DeleteAfter = deleteAfter,
            LegalHoldReason = legalHoldReason,
            ArchivedAt = archivedAt
        )

    let deletionAllowed now deleteAfter legalHoldReason =
        String.IsNullOrWhiteSpace legalHoldReason && now >= deleteAfter
