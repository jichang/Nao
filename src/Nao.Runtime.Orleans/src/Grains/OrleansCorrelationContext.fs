namespace Nao.Runtime.Orleans.Grains

open System
open Nao.Agents
open Orleans

/// Trace and execution lineage crossing the Orleans grain boundary.
[<GenerateSerializer>]
type OrleansCorrelationContext() =
    [<Id(0u)>]
    member val ExecutionId: string = "" with get, set

    [<Id(1u)>]
    member val CorrelationId: string = "" with get, set

    [<Id(2u)>]
    member val CausationExecutionId: string = "" with get, set

    [<Id(3u)>]
    member val Attempt: int = 0 with get, set

[<RequireQualifiedAccess>]
module OrleansCorrelationContext =
    let fromDomain (correlation: CorrelationContext) =
        let envelope = OrleansCorrelationContext()
        envelope.ExecutionId <- ExecutionId.serialize correlation.ExecutionId
        envelope.CorrelationId <- CorrelationId.serialize correlation.CorrelationId

        envelope.CausationExecutionId <-
            correlation.CausationId
            |> Option.map ExecutionId.serialize
            |> Option.defaultValue ""

        envelope.Attempt <- correlation.Attempt
        envelope

    let toDomain (value: OrleansCorrelationContext) =
        if isNull (box value) || value.Attempt <= 0 then
            invalidArg (nameof value) "Orleans correlation context is missing or invalid."

        { ExecutionId = ExecutionId.parse value.ExecutionId
          CorrelationId = CorrelationId.parse value.CorrelationId
          CausationId =
            if String.IsNullOrWhiteSpace value.CausationExecutionId then
                None
            else
                Some(ExecutionId.parse value.CausationExecutionId)
          Attempt = value.Attempt }
