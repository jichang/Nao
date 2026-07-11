namespace Nao.Agents

open System
open System.IO
open System.Collections.Generic
open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Serialization

/// Shared JSON options for feedback artifacts. The F# converter handles options,
/// records, discriminated unions, and maps so everything round-trips cleanly.
module FeedbackJson =
    let options =
        let o = JsonSerializerOptions(WriteIndented = false)
        o.Converters.Add(JsonFSharpConverter(JsonUnionEncoding.InternalTag ||| JsonUnionEncoding.UnwrapFieldlessTags))
        o

    let serialize (value: 'a) : string = JsonSerializer.Serialize(value, options)
    let deserialize<'a> (s: string) : 'a = JsonSerializer.Deserialize<'a>(s, options)

    /// Pretty-printed variant for human-editable artifacts (e.g. emitted tool JSON).
    let indentedOptions =
        let o = JsonSerializerOptions(WriteIndented = true)
        o.Converters.Add(JsonFSharpConverter(JsonUnionEncoding.InternalTag ||| JsonUnionEncoding.UnwrapFieldlessTags))
        o

    let serializeIndented (value: 'a) : string = JsonSerializer.Serialize(value, indentedOptions)

// ─── Store interfaces ───

/// Persists completed turn records so feedback can be analysed against them later.
type ITurnStore =
    abstract member SaveAsync: TurnRecord -> Task
    abstract member GetAsync: turnId: string -> Task<TurnRecord option>
    abstract member GetForSessionAsync: sessionId: string -> Task<TurnRecord list>

/// Persists user feedback entries.
type IFeedbackStore =
    abstract member SaveAsync: Feedback -> Task
    abstract member GetForTurnAsync: turnId: string -> Task<Feedback list>
    abstract member GetForSessionAsync: sessionId: string -> Task<Feedback list>
    /// Every feedback entry across all sessions — the input to cross-session aggregation.
    abstract member GetAllAsync: unit -> Task<Feedback list>
