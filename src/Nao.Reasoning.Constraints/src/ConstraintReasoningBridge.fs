namespace Nao.Reasoning.Constraints

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Nao.Agents
open Nao.Reasoning

[<RequireQualifiedAccess>]
module ConstraintReasoningBridge =
    let private digest (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let semanticMode =
        ReasoningSemanticMode.create
            ReasoningWorldAssumption.ClosedWorld
            ReasoningNegationSemantics.Classical
            ReasoningMonotonicity.Monotonic
            Set.empty
            (Map.ofList [ "logic", "typed-smt" ])

    let pinnedInputs problem =
        let identity = ConstraintProblem.identity problem
        [ ReasoningInputIdentity.create "constraint-problem" identity.Id identity.Version identity.Digest ]

    let private modelArtifact model =
        model
        |> Map.map (fun _ value ->
            match value with
            | ConstraintValue.Boolean item -> box item
            | ConstraintValue.Integer item -> box (string item)
            | ConstraintValue.Real item -> box (ConstraintRational.toString item))
        |> JsonSerializer.Serialize
        |> ReasoningArtifact.create "application/vnd.nao.constraint-model+json"

    let adapt
        authorization
        (request: ReasoningRequest)
        (problem: ConstraintProblem)
        (execution: ConstraintSolverExecution)
        =
        ReasoningRequest.requireOwner authorization request |> ignore
        ConstraintProblem.requireOwner authorization problem |> ignore

        if ReasoningRequest.semanticMode request <> semanticMode then
            invalidArg (nameof request) "Common reasoning semantics do not match the constraint problem."

        if ReasoningRequest.pinnedInputs request <> pinnedInputs problem then
            invalidArg (nameof request) "Common reasoning inputs do not match the constraint problem identity."

        let capabilities =
            if List.isEmpty (ConstraintProblem.objectives problem) then
                Set.ofList [ "smt"; "model"; "unsat-core" ]
            else
                Set.ofList [ "smt"; "model"; "unsat-core"; "optimization" ]

        let requirements = ReasoningRequest.engineRequirements request

        if
            ReasoningEngineRequirements.engineId requirements
            |> Option.exists ((<>) execution.EngineId)
            || not (Set.isSubset (ReasoningEngineRequirements.requiredCapabilities requirements) capabilities)
            || not (
                ReasoningEngineRequirements.allowedExecutionModes requirements
                |> Set.contains ReasoningExecutionMode.IsolatedProcess
            )
            || ReasoningEngineRequirements.requireProof requirements
        then
            invalidArg (nameof request) "Constraint solver execution does not meet the requested engine requirements."

        let engine =
            let configuration =
                execution.Options
                |> Map.toList
                |> List.map (fun (key, value) -> $"{key}={value}")
                |> String.concat "\n"

            let configurationDigest =
                digest
                    $"{execution.EngineId}\n{execution.EngineVersion}\n{ReasoningArtifact.digest execution.NormalizedProblem}\n{configuration}"

            ReasoningEngineIdentity.create
                execution.EngineId
                execution.EngineVersion
                configurationDigest
                ReasoningExecutionMode.IsolatedProcess
                capabilities

        let assertionsById =
            ReasoningRequest.assertions request
            |> List.map (fun value -> ReasoningAssertion.id value, value)
            |> Map.ofList

        let outcome, artifacts =
            match execution.Outcome with
            | ConstraintSolverOutcome.Satisfiable model ->
                let artifact = modelArtifact model

                ReasoningOutcome.Proven([ { Artifact = artifact; Citations = [] } ], ReasoningEvidence.Empty),
                [ artifact ]
            | ConstraintSolverOutcome.Optimal(model, objectives) ->
                let model = modelArtifact model

                let objectiveArtifact =
                    objectives
                    |> JsonSerializer.Serialize
                    |> ReasoningArtifact.create "application/vnd.nao.constraint-objectives+json"

                ReasoningOutcome.Proven(
                    [ { Artifact = model; Citations = [] }
                      { Artifact = objectiveArtifact
                        Citations = [] } ],
                    ReasoningEvidence.Empty
                ),
                [ model; objectiveArtifact ]
            | ConstraintSolverOutcome.Unsatisfiable core ->
                let unsatCore = core |> List.choose (fun id -> Map.tryFind id assertionsById)

                ReasoningOutcome.Disproven
                    { ReasoningEvidence.Empty with
                        UnsatCore = unsatCore },
                []
            | ConstraintSolverOutcome.Unknown reason ->
                ReasoningOutcome.Unknown(ReasoningUnknownReason.EngineReturnedUnknown reason, ReasoningEvidence.Empty),
                []
            | ConstraintSolverOutcome.TimedOut -> ReasoningOutcome.TimedOut, []
            | ConstraintSolverOutcome.Unbounded objective ->
                ReasoningOutcome.Unknown(
                    ReasoningUnknownReason.EngineReturnedUnknown $"Objective '{objective}' is unbounded.",
                    ReasoningEvidence.Empty
                ),
                []
            | ConstraintSolverOutcome.ResourceLimitExceeded(resource, limit, observed) ->
                ReasoningOutcome.ResourceLimitExceeded(resource, limit, observed), []
            | ConstraintSolverOutcome.Failed(code, detail) -> ReasoningOutcome.Failed(code, detail), []

        let conclusionCount =
            match outcome with
            | ReasoningOutcome.Proven(conclusions, _) -> conclusions.Length
            | _ -> 0

        let usage =
            { WallTime = execution.Duration
              CpuTime = None
              PeakMemoryBytes = execution.Statistics.PeakMemoryBytes
              Steps = execution.Statistics.Steps
              Conclusions = conclusionCount
              ProofSteps = 0
              ResponseBytes = execution.Statistics.ResponseBytes
              ExternalCalls = Some 0 }

        let audit =
            { TraceId = execution.ExecutionId
              StartedAt = execution.StartedAt
              CompletedAt = execution.StartedAt + execution.Duration
              CacheHit = false }

        ReasoningResult.create request engine outcome usage audit (execution.NormalizedProblem :: artifacts)
