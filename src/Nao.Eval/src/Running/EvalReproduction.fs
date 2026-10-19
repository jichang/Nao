namespace Nao.Eval

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open FSharp.SystemTextJson
open Nao.Agents

[<RequireQualifiedAccess>]
type EvalRunComparison =
    | Equivalent
    | NondeterministicVariance of changedCases: string list
    | Regression of changedCases: string list

[<RequireQualifiedAccess>]
module EvalRunComparison =
    let compare baseline candidate =
        if
            baseline.DatasetId <> candidate.DatasetId
            || baseline.DatasetRevision <> candidate.DatasetRevision
            || baseline.DatasetContentSha256 <> candidate.DatasetContentSha256
            || baseline.Configuration <> candidate.Configuration
        then
            invalidArg (nameof candidate) "Evaluation runs must identify the same dataset and executable configuration."

        let projection report =
            report.Results
            |> List.groupBy _.CaseId
            |> List.map (fun (caseId, results) ->
                caseId,
                (results
                 |> List.map (fun result -> result.ActualOutput, result.Verdict, result.Reason, result.EvaluatorName)))
            |> Map.ofList

        let baselineResults = projection baseline
        let candidateResults = projection candidate

        let changedCases =
            Set.union (baselineResults |> Map.keys |> Set.ofSeq) (candidateResults |> Map.keys |> Set.ofSeq)
            |> Seq.filter (fun caseId -> Map.tryFind caseId baselineResults <> Map.tryFind caseId candidateResults)
            |> Seq.sort
            |> Seq.toList

        match changedCases with
        | [] -> EvalRunComparison.Equivalent
        | _ when
            baseline.Reproducibility.Deterministic
            && candidate.Reproducibility.Deterministic
            ->
            EvalRunComparison.Regression changedCases
        | _ -> EvalRunComparison.NondeterministicVariance changedCases

type EvalReproductionBundle =
    private
        { SchemaVersion: int
          Dataset: EvalDataset
          Configuration: EvalExecutableConfiguration
          AgentId: string
          Evaluator: EvalComponentIdentity
          CaseIds: string list
          RandomSeed: int
          Timestamp: DateTimeOffset
          MaximumParallelism: int
          StopOnFirstFailure: bool
          Fixtures: EvalRecordedFixtureSet }

[<RequireQualifiedAccess>]
module EvalReproductionBundle =
    [<Literal>]
    let CurrentSchemaVersion = 1

    let private options () =
        let options = JsonSerializerOptions(WriteIndented = true)

        options.Converters.Add(
            JsonFSharpConverter(JsonUnionEncoding.InternalTag ||| JsonUnionEncoding.UnwrapFieldlessTags)
        )

        options

    let create dataset report agentId evaluator fixtures =
        if report.DatasetId <> EvalDataset.id dataset then
            invalidArg (nameof report) "Reproduction report does not identify the supplied dataset."

        if report.DatasetRevision <> EvalDataset.revision dataset then
            invalidArg (nameof report) "Reproduction report does not identify the supplied dataset revision."

        if report.DatasetContentSha256 <> EvalDataset.contentSha256 dataset then
            invalidArg (nameof report) "Reproduction report does not identify the supplied dataset content."

        if not (report.Configuration.Evaluators |> List.contains evaluator) then
            invalidArg (nameof evaluator) "Reproduction evaluator is not part of the report configuration."

        { SchemaVersion = CurrentSchemaVersion
          Dataset = dataset
          Configuration = report.Configuration
          AgentId = agentId
          Evaluator = evaluator
          CaseIds = report.Results |> List.filter (EvalResult.passed >> not) |> List.map _.CaseId
          RandomSeed = report.Reproducibility.RandomSeed
          Timestamp = report.RunAt
          MaximumParallelism = report.Reproducibility.Scheduling.MaximumParallelism
          StopOnFirstFailure = report.Reproducibility.Scheduling.StopOnFirstFailure
          Fixtures = fixtures }

    let serialize bundle =
        JsonSerializer.Serialize(bundle, options ())

    let deserialize (json: string) =
        let bundle = JsonSerializer.Deserialize<EvalReproductionBundle>(json, options ())

        if isNull (box bundle) || bundle.SchemaVersion <> CurrentSchemaVersion then
            invalidArg (nameof json) "Unsupported evaluation reproduction bundle schema version."

        EvalDataset.validate bundle.Dataset

        EvalRecordedFixtureSet.restore
            (EvalRecordedFixtureSet.schemaVersion bundle.Fixtures)
            (EvalRecordedFixtureSet.formatVersion bundle.Fixtures)
            (EvalRecordedFixtureSet.providers bundle.Fixtures)
            (EvalRecordedFixtureSet.tools bundle.Fixtures)
        |> ignore

        bundle

    let runAsync
        (authorization: AuthorizationScope)
        (createAgentContext: unit -> AgentContext)
        (harness: EtclovgConfig)
        (sandbox: SandboxConfig)
        (specification: EvalExecutionSpecification)
        (agent: Agent)
        (evaluator: Evaluator)
        (bundle: EvalReproductionBundle)
        (cancellationToken: CancellationToken)
        : Task<EvalReport> =
        task {
            if agent.Metadata.Id <> bundle.AgentId then
                invalidArg (nameof agent) "Reproduction agent identity does not match the bundle."

            if evaluator.Identity <> bundle.Evaluator then
                invalidArg (nameof evaluator) "Reproduction evaluator identity does not match the bundle."

            let resolved =
                EvalExecutionSpecification.resolve agent.Metadata.Id [ evaluator.Identity ] specification

            if resolved <> bundle.Configuration then
                invalidArg (nameof specification) "Local executable configuration does not match the bundle."

            let execution =
                EvalExecutionConfig.create
                    authorization
                    createAgentContext
                    harness
                    sandbox
                    specification
                    (EvalExecutionMode.RecordedReplay bundle.Fixtures)
                    (EvalRuntime.deterministic bundle.RandomSeed bundle.Timestamp)

            let config =
                { EvalRunnerConfig.create execution with
                    MaxParallelism = bundle.MaximumParallelism
                    StopOnFirstFailure = bundle.StopOnFirstFailure
                    CaptureTraces = true }

            return! EvalRunner.runDatasetAsync config evaluator agent bundle.Dataset cancellationToken
        }

    let caseIds bundle = bundle.CaseIds
