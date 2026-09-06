namespace Nao.Persistence

open System
open Nao.Agents

[<RequireQualifiedAccess>]
module HarnessCheckpointSerialization =

    [<CLIMutable>]
    type Dto =
        { Id: Guid
          ExecutionId: string
          CorrelationId: string
          CausationId: string
          Attempt: int
          Owner: string
          TurnId: string
          AgentId: string
          Phase: string
          RecordedAt: DateTimeOffset }

    let phaseToString =
        function
        | HarnessCheckpointPhase.Accepted -> "accepted"
        | HarnessCheckpointPhase.ExecutionStarted -> "execution-started"
        | HarnessCheckpointPhase.Succeeded -> "succeeded"
        | HarnessCheckpointPhase.Failed -> "failed"
        | HarnessCheckpointPhase.Denied -> "denied"
        | HarnessCheckpointPhase.Cancelled -> "cancelled"
        | HarnessCheckpointPhase.TimedOut -> "timed-out"
        | HarnessCheckpointPhase.LimitExceeded -> "limit-exceeded"
        | HarnessCheckpointPhase.Indeterminate -> "indeterminate"

    let phaseFromString =
        function
        | "accepted" -> HarnessCheckpointPhase.Accepted
        | "execution-started" -> HarnessCheckpointPhase.ExecutionStarted
        | "succeeded" -> HarnessCheckpointPhase.Succeeded
        | "failed" -> HarnessCheckpointPhase.Failed
        | "denied" -> HarnessCheckpointPhase.Denied
        | "cancelled" -> HarnessCheckpointPhase.Cancelled
        | "timed-out" -> HarnessCheckpointPhase.TimedOut
        | "limit-exceeded" -> HarnessCheckpointPhase.LimitExceeded
        | "indeterminate" -> HarnessCheckpointPhase.Indeterminate
        | phase -> invalidArg (nameof phase) (sprintf "Unknown harness checkpoint phase '%s'." phase)

    let toDto (checkpoint: HarnessCheckpoint) : Dto =
        { Id = checkpoint.Id
          ExecutionId = ExecutionId.serialize checkpoint.Correlation.ExecutionId
          CorrelationId = CorrelationId.serialize checkpoint.Correlation.CorrelationId
          CausationId =
            checkpoint.Correlation.CausationId
            |> Option.map ExecutionId.serialize
            |> Option.defaultValue null
          Attempt = checkpoint.Correlation.Attempt
          Owner = checkpoint.Owner
          TurnId = checkpoint.TurnId
          AgentId = checkpoint.AgentId
          Phase = phaseToString checkpoint.Phase
          RecordedAt = checkpoint.RecordedAt }

    let ofDto (checkpoint: Dto) : HarnessCheckpoint =
        if checkpoint.Attempt < 1 then
            invalidArg (nameof checkpoint.Attempt) "Correlation attempt must be positive."

        { Id = checkpoint.Id
          Correlation =
            { ExecutionId = ExecutionId.parse checkpoint.ExecutionId
              CorrelationId = CorrelationId.parse checkpoint.CorrelationId
              CausationId = Option.ofObj checkpoint.CausationId |> Option.map ExecutionId.parse
              Attempt = checkpoint.Attempt }
          Owner = checkpoint.Owner
          TurnId = checkpoint.TurnId
          AgentId = checkpoint.AgentId
          Phase = phaseFromString checkpoint.Phase
          RecordedAt = checkpoint.RecordedAt }
