namespace Nao.Eval

open System
open System.Security.Cryptography
open System.Text
open Nao.Agents

type EvalRecordedFixtureSet =
    private
        { SchemaVersion: int
          FormatVersion: string
          Providers: (string * ReplayFixture<ProviderReplayFixture>) list
          Tools: (string * ReplayFixture<ToolReplayFixture>) list }

[<RequireQualifiedAccess>]
module EvalRecordedFixtureSet =
    [<Literal>]
    let CurrentSchemaVersion = 1

    let private required name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name "Fixture format versions must be canonical and non-blank."

    let create formatVersion (fixtures: ReplayFixtures) =
        required (nameof formatVersion) formatVersion

        { SchemaVersion = CurrentSchemaVersion
          FormatVersion = formatVersion
          Providers =
            fixtures.Providers
            |> Map.toList
            |> List.map (fun (key, fixture) -> IdempotencyKey.value key, fixture)
          Tools =
            fixtures.Tools
            |> Map.toList
            |> List.map (fun (key, fixture) -> IdempotencyKey.value key, fixture) }

    let capture formatVersion providerExchanges toolExecutions =
        RecordedReplayFixtures.create providerExchanges toolExecutions
        |> create formatVersion

    let restore schemaVersion formatVersion providers tools =
        if schemaVersion <> CurrentSchemaVersion then
            invalidArg (nameof schemaVersion) "Unsupported evaluation fixture schema version."

        let requireUnique fieldName entries =
            if entries |> List.countBy fst |> List.exists (fun (_, count) -> count > 1) then
                invalidArg fieldName "Recorded fixture identities must be unique."

        requireUnique (nameof providers) providers
        requireUnique (nameof tools) tools

        let fixtures =
            { Providers =
                providers
                |> List.map (fun (key, fixture) -> IdempotencyKey.parse key, fixture)
                |> Map.ofList
              Tools =
                tools
                |> List.map (fun (key, fixture) -> IdempotencyKey.parse key, fixture)
                |> Map.ofList }

        create formatVersion fixtures

    let schemaVersion value = value.SchemaVersion
    let formatVersion value = value.FormatVersion
    let providers value = value.Providers
    let tools value = value.Tools

    let toReplayFixtures value =
        { Providers =
            value.Providers
            |> List.map (fun (key, fixture) -> IdempotencyKey.parse key, fixture)
            |> Map.ofList
          Tools =
            value.Tools
            |> List.map (fun (key, fixture) -> IdempotencyKey.parse key, fixture)
            |> Map.ofList }

[<RequireQualifiedAccess>]
type EvalExecutionMode =
    | PinnedLive
    | PinnedMock
    | RecordedReplay of EvalRecordedFixtureSet

type EvalSchedulingSettings =
    { MaximumParallelism: int
      StopOnFirstFailure: bool }

type EvalReproducibility =
    { RandomSeed: int
      Deterministic: bool
      Scheduling: EvalSchedulingSettings
      Mode: string
      FixtureSchemaVersion: int option
      FixtureFormatVersion: string option }

type EvalRuntime =
    private
        { RandomSeed: int
          Deterministic: bool
          UtcNow: unit -> DateTimeOffset
          DeriveGuid: string -> Guid }

[<RequireQualifiedAccess>]
module EvalRuntime =
    let private derive seed purpose =
        let bytes = Encoding.UTF8.GetBytes(sprintf "%d|%s" seed purpose) |> SHA256.HashData
        let guidBytes = bytes.[0..15]
        Guid guidBytes

    let system randomSeed =
        { RandomSeed = randomSeed
          Deterministic = false
          UtcNow = fun () -> DateTimeOffset.UtcNow
          DeriveGuid = fun _ -> Guid.NewGuid() }

    let deterministic randomSeed timestamp =
        { RandomSeed = randomSeed
          Deterministic = true
          UtcNow = fun () -> timestamp
          DeriveGuid = derive randomSeed }

    let randomSeed runtime = runtime.RandomSeed
    let isDeterministic runtime = runtime.Deterministic
    let utcNow runtime = runtime.UtcNow()
    let guid purpose runtime = runtime.DeriveGuid purpose

    let correlation purpose runtime =
        { ExecutionId = runtime |> guid (purpose + "|execution") |> ExecutionId.ofGuid
          CorrelationId = runtime |> guid (purpose + "|correlation") |> CorrelationId.ofGuid
          CausationId = None
          Attempt = 1 }

[<RequireQualifiedAccess>]
module EvalReproducibility =
    let create mode runtime scheduling =
        if scheduling.MaximumParallelism <= 0 then
            invalidArg (nameof scheduling) "Evaluation parallelism must be positive."

        let modeName, schemaVersion, formatVersion =
            match mode with
            | EvalExecutionMode.PinnedLive -> "pinned-live", None, None
            | EvalExecutionMode.PinnedMock -> "pinned-mock", None, None
            | EvalExecutionMode.RecordedReplay fixtures ->
                "recorded-replay",
                Some(EvalRecordedFixtureSet.schemaVersion fixtures),
                Some(EvalRecordedFixtureSet.formatVersion fixtures)

        { RandomSeed = EvalRuntime.randomSeed runtime
          Deterministic = EvalRuntime.isDeterministic runtime
          Scheduling = scheduling
          Mode = modeName
          FixtureSchemaVersion = schemaVersion
          FixtureFormatVersion = formatVersion }
