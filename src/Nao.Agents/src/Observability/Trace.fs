namespace Nao.Agents

open System
open System.Threading.Tasks

/// A unique trace identifier for correlating events across agent calls
type TraceId = TraceId of Guid

/// A span within a trace (represents a unit of work)
type SpanId = SpanId of Guid

module TraceId =
    let generate () = TraceId(Guid.NewGuid())
    let value (TraceId value) = value

    let tryParse (value: string) =
        match Guid.TryParse value with
        | true, id -> Some(TraceId id)
        | _ -> None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid trace ID.")

    let serialize = value >> _.ToString("D")

module SpanId =
    let generate () = SpanId(Guid.NewGuid())
    let value (SpanId value) = value

    let tryParse (value: string) =
        match Guid.TryParse value with
        | true, id -> Some(SpanId id)
        | _ -> None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid span ID.")

    let serialize = value >> _.ToString("D")

/// Span status
[<RequireQualifiedAccess>]
type SpanStatus =
    | Ok
    | Error of message: string
    | Cancelled

/// A single span in a distributed trace
type Span =
    {
        /// Unique span identifier
        Id: SpanId
        /// Execution identity, correlation, causation, and attempt for this span.
        Correlation: CorrelationContext
        /// Parent trace
        TraceId: TraceId
        /// Parent span (None for root spans)
        ParentSpanId: SpanId option
        /// Operation name
        OperationName: string
        /// When the span started
        StartTime: DateTimeOffset
        /// When the span ended (None if still running)
        EndTime: DateTimeOffset option
        /// Status of the span
        Status: SpanStatus
        /// Key-value attributes
        Attributes: Map<string, string>
        /// Events that occurred during this span
        Events: SpanEvent list
    }

    member this.Duration =
        match this.EndTime with
        | Some endTime -> endTime - this.StartTime
        | None -> DateTimeOffset.UtcNow - this.StartTime

/// An event within a span
and SpanEvent =
    { Name: string
      Timestamp: DateTimeOffset
      Attributes: Map<string, string> }

/// Functional trace collection operations.
type Tracer =
    {
        /// Start a new root trace
        StartTrace: CorrelationContext -> string -> Span
        /// Start a child span under an existing span
        StartSpan: Span -> string -> Span
        /// End a span
        EndSpan: Span -> SpanStatus -> unit
        /// Add an event to the current span
        AddEvent: Span -> string -> Map<string, string> -> unit
        /// Set attributes on a span
        SetAttributes: Span -> Map<string, string> -> unit
        /// Get all completed spans for a trace
        GetTrace: TraceId -> Span list
        /// Get all spans produced by one execution
        GetByExecution: ExecutionId -> Span list
    }
