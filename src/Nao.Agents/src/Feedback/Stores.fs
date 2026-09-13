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
    { SaveAsync: AuthorizationScope -> TurnRecord -> Task
      GetAsync: AuthorizationScope -> string -> Task<TurnRecord option>
      GetAllAsync: AuthorizationScope -> Task<TurnRecord list>
      GetForExecutionAsync: AuthorizationScope -> ExecutionId -> Task<TurnRecord list>
      DeleteScopeAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: AuthorizationScope -> DateTimeOffset -> Task<Result<int, PlatformFailure>> }

/// Persists user feedback entries.
type FeedbackStore =
    { SaveAsync: AuthorizationScope -> Feedback -> Task
      GetForTurnAsync: AuthorizationScope -> string -> Task<Feedback list>
      GetAllAsync: AuthorizationScope -> Task<Feedback list>
      DeleteScopeAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: AuthorizationScope -> DateTimeOffset -> Task<Result<int, PlatformFailure>> }
