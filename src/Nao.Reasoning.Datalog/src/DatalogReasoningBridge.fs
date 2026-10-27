namespace Nao.Reasoning.Datalog

open System
open System.Text.Json
open Nao.Agents
open Nao.Reasoning

[<RequireQualifiedAccess>]
module DatalogReasoningBridge =
    let semanticMode (program: DatalogProgram) =
        let hasNegation =
            DatalogProgram.rules program
            |> List.exists (fun rule ->
                rule.Body
                |> List.exists (function
                    | DatalogLiteral.Negative _ -> true
                    | DatalogLiteral.Positive _ -> false))

        ReasoningSemanticMode.create
            ReasoningWorldAssumption.ClosedWorld
            ReasoningNegationSemantics.NegationAsFailure
            (if hasNegation then
                 ReasoningMonotonicity.NonMonotonic
             else
                 ReasoningMonotonicity.Monotonic)
            Set.empty
            (Map.ofList [ "rule-language", "stratified-datalog" ])

    let pinnedInputs (program: DatalogProgram) =
        let identity = DatalogProgram.identity program

        [ ReasoningInputIdentity.create "datalog-rule-set" identity.Id identity.Version identity.Digest ]

    let private value =
        function
        | DatalogValue.Symbol text -> box text
        | DatalogValue.Signed number -> box number
        | DatalogValue.Unsigned number -> box number
        | DatalogValue.Float number -> box number

    let private bindingArtifact (binding: DatalogBinding) =
        binding
        |> Map.map (fun _ item -> value item)
        |> JsonSerializer.Serialize
        |> ReasoningArtifact.create "application/vnd.nao.datalog-binding+json"

    let private citations (program: DatalogProgram) (derivations: DatalogDerivation list) =
        let facts =
            DatalogProgram.facts program
            |> List.map (fun value -> value.Id, value.Citations)
            |> Map.ofList

        let rules =
            DatalogProgram.rules program
            |> List.map (fun value -> value.Id, value.Citations)
            |> Map.ofList

        derivations
        |> List.collect (fun derivation ->
            (Map.tryFind derivation.RuleId rules |> Option.defaultValue [])
            @ (derivation.PremiseFactIds
               |> List.collect (fun id -> Map.tryFind id facts |> Option.defaultValue [])))
        |> List.distinct

    let adapt
        (authorization: AuthorizationScope)
        (request: ReasoningRequest)
        (program: DatalogProgram)
        (execution: DatalogExecution)
        =
        ReasoningRequest.requireOwner authorization request |> ignore
        DatalogProgram.requireOwner authorization program |> ignore

        if ReasoningRequest.semanticMode request <> semanticMode program then
            invalidArg (nameof request) "Common reasoning semantics do not match the Datalog program."

        if ReasoningRequest.pinnedInputs request <> pinnedInputs program then
            invalidArg (nameof request) "Common reasoning inputs do not match the Datalog rule-set identity."

        let engine =
            ReasoningEngineIdentity.create
                execution.Provenance.EngineId
                execution.Provenance.EngineVersion
                execution.Provenance.RuleSet.Digest
                ReasoningExecutionMode.IsolatedProcess
                (Set.ofList [ "datalog"; "recursion"; "stratified-negation" ])

        let outcome, conclusions =
            match execution.Outcome with
            | DatalogExecutionOutcome.Completed [] -> ReasoningOutcome.Disproven ReasoningEvidence.Empty, []
            | DatalogExecutionOutcome.Completed rows ->
                let conclusions =
                    rows
                    |> List.map (fun row ->
                        { Artifact = bindingArtifact row.Binding
                          Citations = citations program row.Derivations })

                ReasoningOutcome.Proven(conclusions, ReasoningEvidence.Empty), conclusions
            | DatalogExecutionOutcome.TimedOut -> ReasoningOutcome.TimedOut, []
            | DatalogExecutionOutcome.ResourceLimitExceeded(resource, limit, observed) ->
                ReasoningOutcome.ResourceLimitExceeded(resource, limit, observed), []
            | DatalogExecutionOutcome.RunnerFailure(code, detail) -> ReasoningOutcome.Failed(code, detail), []

        let usage =
            { WallTime = execution.Provenance.Duration
              CpuTime = None
              PeakMemoryBytes = None
              Steps = None
              Conclusions = conclusions.Length
              ProofSteps = 0
              ResponseBytes = None
              ExternalCalls = Some 0 }

        let audit =
            { TraceId = execution.Provenance.ExecutionId
              StartedAt = execution.Provenance.StartedAt
              CompletedAt = execution.Provenance.StartedAt + execution.Provenance.Duration
              CacheHit = false }

        ReasoningResult.create request engine outcome usage audit (conclusions |> List.map _.Artifact)
