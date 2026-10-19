namespace Nao.Agents

open System
open System.Collections.Generic
open System.Threading

type EmbeddingCallDescriptor =
    { ProfileId: string
      BatchId: string
      InputCount: int
      PreflightInputTokens: int
      StartedAt: DateTimeOffset }

type EmbeddingCallObservation =
    { ProfileId: string
      BatchId: string
      InputCount: int
      InputTokens: int
      EstimatedCostUsd: decimal
      ProviderAttempts: int
      StartedAt: DateTimeOffset
      CompletedAt: DateTimeOffset }

type EmbeddingCallHandle =
    private
        { Descriptor: EmbeddingCallDescriptor
          Authorization: AuthorizationScope option
          Tracer: Tracer option
          Span: Span option
          SyncRoot: obj
          mutable Ended: bool }

type internal ExecutionInstrumentationContext =
    { Authorization: AuthorizationScope
      Correlation: CorrelationContext
      Tracer: Tracer option
      ParentSpan: Span option
      EmbeddingCalls: ResizeArray<EmbeddingCallObservation> }

[<RequireQualifiedAccess>]
module ExecutionInstrumentation =
    let private current = AsyncLocal<ExecutionInstrumentationContext option>()

    let private required name (value: string) =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name "Instrumentation identity must be non-blank and have no surrounding whitespace."

        value

    let internal install authorization correlation tracer parentSpan =
        let previous = current.Value

        let context =
            { Authorization = authorization
              Correlation = correlation
              Tracer = tracer
              ParentSpan = parentSpan
              EmbeddingCalls = ResizeArray() }

        current.Value <- Some context
        previous, context

    let internal restore context = current.Value <- context

    let internal embeddingCalls context =
        lock context.EmbeddingCalls (fun () -> List.ofSeq context.EmbeddingCalls)

    let tryStartSpan operationName =
        match current.Value with
        | Some context ->
            match context.Tracer, context.ParentSpan with
            | Some tracer, Some parent ->
                Some(context.Authorization, tracer, tracer.StartSpan context.Authorization parent operationName)
            | _ -> None
        | None -> None

    let beginEmbeddingCall (descriptor: EmbeddingCallDescriptor) =
        let descriptor: EmbeddingCallDescriptor =
            { descriptor with
                ProfileId = required (nameof descriptor.ProfileId) descriptor.ProfileId
                BatchId = required (nameof descriptor.BatchId) descriptor.BatchId }

        if descriptor.InputCount <= 0 || descriptor.PreflightInputTokens < 0 then
            invalidArg (nameof descriptor) "Embedding instrumentation counts are invalid."

        match RuntimeExecutionBudget.beginEmbeddingCall () with
        | Some limit -> raise (ExecutionLimitExceededException limit)
        | None -> ()

        let context = current.Value

        let span =
            match context with
            | Some value ->
                match value.Tracer, value.ParentSpan with
                | Some tracer, Some parent ->
                    let span = tracer.StartSpan value.Authorization parent "embedding.generate"

                    tracer.SetAttributes
                        value.Authorization
                        span
                        (Map.ofList
                            [ "embedding.profile_id", descriptor.ProfileId
                              "embedding.batch_id", descriptor.BatchId
                              "embedding.input_count", string descriptor.InputCount
                              "embedding.preflight_tokens", string descriptor.PreflightInputTokens ])

                    Some span
                | _ -> None
            | None -> None

        { Descriptor = descriptor
          Authorization = context |> Option.map _.Authorization
          Tracer = context |> Option.bind _.Tracer
          Span = span
          SyncRoot = obj ()
          Ended = false }

    let private tryEnd handle operation =
        lock handle.SyncRoot (fun () ->
            if handle.Ended then
                false
            else
                handle.Ended <- true
                operation ()
                true)

    let completeEmbeddingCall handle inputTokens estimatedCostUsd providerAttempts completedAt =
        if inputTokens < 0 || estimatedCostUsd < 0m || providerAttempts <= 0 then
            invalidArg (nameof inputTokens) "Completed embedding instrumentation usage is invalid."

        if completedAt < handle.Descriptor.StartedAt then
            invalidArg (nameof completedAt) "Embedding instrumentation completion precedes its start."

        let observation =
            { ProfileId = handle.Descriptor.ProfileId
              BatchId = handle.Descriptor.BatchId
              InputCount = handle.Descriptor.InputCount
              InputTokens = inputTokens
              EstimatedCostUsd = estimatedCostUsd
              ProviderAttempts = providerAttempts
              StartedAt = handle.Descriptor.StartedAt
              CompletedAt = completedAt }

        let completed =
            tryEnd handle (fun () ->
                match current.Value with
                | Some context ->
                    lock context.EmbeddingCalls (fun () -> context.EmbeddingCalls.Add observation)

                    RuntimeMetrics.get ()
                    |> Option.iter (fun metrics ->
                        metrics.Record
                            context.Authorization
                            (MetricRecord.embeddingCall
                                context.Correlation
                                completedAt
                                observation.ProfileId
                                observation.BatchId
                                observation.InputCount
                                observation.InputTokens
                                (max 0L (int64 (observation.CompletedAt - observation.StartedAt).TotalMilliseconds))
                                observation.EstimatedCostUsd
                                observation.ProviderAttempts))
                | None -> ()

                match handle.Authorization, handle.Tracer, handle.Span with
                | Some authorization, Some tracer, Some span ->
                    tracer.SetAttributes
                        authorization
                        span
                        (Map.ofList
                            [ "embedding.input_tokens", string inputTokens
                              "embedding.estimated_cost_usd", string estimatedCostUsd
                              "embedding.provider_attempts", string providerAttempts ])

                    tracer.EndSpan authorization span SpanStatus.Ok
                | _ -> ())

        if not completed then
            invalidOp "Embedding instrumentation call has already ended."

        match RuntimeExecutionBudget.recordEmbeddingUsage inputTokens estimatedCostUsd with
        | Some limit -> raise (ExecutionLimitExceededException limit)
        | None -> ()

    let failEmbeddingCall handle category =
        tryEnd handle (fun () ->
            match handle.Authorization, handle.Tracer, handle.Span with
            | Some authorization, Some tracer, Some span ->
                tracer.EndSpan authorization span (SpanStatus.Error(TelemetryErrorCategory.ofPlatform category))
            | _ -> ())
        |> ignore
