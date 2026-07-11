namespace Nao.Agents

open System
open System.Threading.Tasks

/// A unique trace identifier for correlating events across agent calls
type TraceId = TraceId of Guid

/// A span within a trace (represents a unit of work)
type SpanId = SpanId of Guid

/// Span status
[<RequireQualifiedAccess>]
type SpanStatus =
    | Ok
    | Error of message: string
    | Cancelled

/// A single span in a distributed trace
type Span =
    { /// Unique span identifier
      Id: SpanId
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
      Events: SpanEvent list }

    member this.Duration =
        match this.EndTime with
        | Some endTime -> endTime - this.StartTime
        | None -> DateTimeOffset.UtcNow - this.StartTime

/// An event within a span
and SpanEvent =
    { Name: string
      Timestamp: DateTimeOffset
      Attributes: Map<string, string> }

/// Interface for trace collection
type ITracer =
    /// Start a new root trace
    abstract member StartTrace: operationName: string -> Span
    /// Start a child span under an existing span
    abstract member StartSpan: parentSpan: Span -> operationName: string -> Span
    /// End a span
    abstract member EndSpan: Span -> SpanStatus -> unit
    /// Add an event to the current span
    abstract member AddEvent: Span -> name: string -> attributes: Map<string, string> -> unit
    /// Set attributes on a span
    abstract member SetAttributes: Span -> Map<string, string> -> unit
    /// Get all completed spans for a trace
    abstract member GetTrace: TraceId -> Span list
