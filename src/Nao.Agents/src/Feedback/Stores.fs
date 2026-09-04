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

    let serialize (value: 'a) : string =
        JsonSerializer.Serialize(value, options)

    let deserialize<'a> (s: string) : 'a =
        JsonSerializer.Deserialize<'a>(s, options)

// ─── Store capabilities ───

/// Persists completed turn records so feedback can be analysed against them later.
type TurnStore =
    { SaveAsync: TurnRecord -> Task
      GetAsync: string -> Task<TurnRecord option>
      GetForSessionAsync: string -> Task<TurnRecord list>
      DeleteSessionAsync: string -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: string -> DateTimeOffset -> Task<Result<int, PlatformFailure>> }

/// Persists user feedback entries.
type FeedbackStore =
    { SaveAsync: Feedback -> Task
      GetForTurnAsync: string -> Task<Feedback list>
      GetForSessionAsync: string -> Task<Feedback list>
      GetAllAsync: unit -> Task<Feedback list>
      DeleteOwnerAsync: string -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: string -> DateTimeOffset -> Task<Result<int, PlatformFailure>> }
