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

    /// Compatibility aliases retained for callers that previously requested pretty output.
    /// All framework JSON is compact so it can be embedded safely in LLM protocols.
    let indentedOptions = options
    let serializeIndented (value: 'a) : string = serialize value

// ─── Store capabilities ───

/// Persists completed turn records so feedback can be analysed against them later.
type TurnStore =
    { SaveAsync: TurnRecord -> Task
      GetAsync: string -> Task<TurnRecord option>
      GetForSessionAsync: string -> Task<TurnRecord list> }

/// Persists user feedback entries.
type FeedbackStore =
    { SaveAsync: Feedback -> Task
      GetForTurnAsync: string -> Task<Feedback list>
      GetForSessionAsync: string -> Task<Feedback list>
    /// Every feedback entry across all sessions — the input to cross-session aggregation.
      GetAllAsync: unit -> Task<Feedback list> }
