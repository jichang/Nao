namespace Nao.Reasoning.Constraints

#nowarn "3511"

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Reasoning

type Z3RunnerRequest =
    { Script: string
      ExpectedEngineVersion: string
      ReturnModel: bool
      ReturnUnsatCore: bool
      ObjectiveIds: string list
      Timeout: TimeSpan
      MaxMemoryBytes: int64
      MaxSteps: int64
      MaxResponseBytes: int
      AllowFileSystem: bool
      AllowNetwork: bool
      AllowProcess: bool }

type Z3RunnerResponse =
    { EngineVersion: string
      Outcome: ConstraintSolverOutcome
      Statistics: ConstraintSolverStatistics }

type Z3Runner =
    private
        { RunAsync: Z3RunnerRequest -> CancellationToken -> Task<Z3RunnerResponse> }

[<RequireQualifiedAccess>]
module Z3Runner =
    let create runAsync = { RunAsync = runAsync }

module private SmtLibCompiler =
    let private sort =
        function
        | ConstraintDomain.Boolean -> "Bool"
        | ConstraintDomain.IntegerRange _
        | ConstraintDomain.UnboundedInteger -> "Int"
        | ConstraintDomain.RealRange _
        | ConstraintDomain.UnboundedReal -> "Real"

    let rec private value =
        function
        | ConstraintValue.Boolean true -> "true"
        | ConstraintValue.Boolean false -> "false"
        | ConstraintValue.Integer number when number < 0I -> $"(- {abs number})"
        | ConstraintValue.Integer number -> string number
        | ConstraintValue.Real number when ConstraintRational.numerator number < 0I ->
            let magnitude =
                ConstraintRational.create
                    (abs (ConstraintRational.numerator number))
                    (ConstraintRational.denominator number)

            $"(- {value (ConstraintValue.Real magnitude)})"
        | ConstraintValue.Real number when ConstraintRational.denominator number = 1I ->
            string (ConstraintRational.numerator number)
        | ConstraintValue.Real number ->
            $"(/ {ConstraintRational.numerator number} {ConstraintRational.denominator number})"

    let rec private expression =
        function
        | ConstraintExpression.Value constant -> value constant
        | ConstraintExpression.Variable name -> name
        | ConstraintExpression.Not item -> $"(not {expression item})"
        | ConstraintExpression.And items ->
            let arguments = items |> List.map expression |> String.concat " "
            $"(and {arguments})"
        | ConstraintExpression.Or items ->
            let arguments = items |> List.map expression |> String.concat " "
            $"(or {arguments})"
        | ConstraintExpression.Implies(left, right) -> $"(=> {expression left} {expression right})"
        | ConstraintExpression.Equal(left, right) -> $"(= {expression left} {expression right})"
        | ConstraintExpression.Distinct items ->
            let arguments = items |> List.map expression |> String.concat " "
            $"(distinct {arguments})"
        | ConstraintExpression.LessThan(left, right) -> $"(< {expression left} {expression right})"
        | ConstraintExpression.LessThanOrEqual(left, right) -> $"(<= {expression left} {expression right})"
        | ConstraintExpression.GreaterThan(left, right) -> $"(> {expression left} {expression right})"
        | ConstraintExpression.GreaterThanOrEqual(left, right) -> $"(>= {expression left} {expression right})"
        | ConstraintExpression.Add items ->
            let arguments = items |> List.map expression |> String.concat " "
            $"(+ {arguments})"
        | ConstraintExpression.Subtract(left, right) -> $"(- {expression left} {expression right})"
        | ConstraintExpression.Multiply items ->
            let arguments = items |> List.map expression |> String.concat " "
            $"(* {arguments})"
        | ConstraintExpression.Negate item -> $"(- {expression item})"

    let compile problem =
        let builder = StringBuilder()
        builder.AppendLine("(set-option :produce-models true)") |> ignore
        builder.AppendLine("(set-option :produce-unsat-cores true)") |> ignore

        for variable in ConstraintProblem.variables problem do
            builder.AppendLine($"(declare-const {variable.Name} {sort variable.Domain})")
            |> ignore

            match variable.Domain with
            | ConstraintDomain.IntegerRange(minimum, maximum) ->
                builder.AppendLine($"(assert (and (>= {variable.Name} {minimum}) (<= {variable.Name} {maximum})))")
                |> ignore
            | ConstraintDomain.RealRange(minimum, maximum) ->
                let minimum = value (ConstraintValue.Real minimum)
                let maximum = value (ConstraintValue.Real maximum)

                builder.AppendLine($"(assert (and (>= {variable.Name} {minimum}) (<= {variable.Name} {maximum})))")
                |> ignore
            | _ -> ()

        for assertion in ConstraintProblem.assertions problem do
            if assertion.TrackForUnsatCore then
                builder.AppendLine($"(assert (! {expression assertion.Expression} :named {assertion.Id}))")
                |> ignore
            else
                builder.AppendLine($"(assert {expression assertion.Expression})") |> ignore

        for objective in ConstraintProblem.objectives problem do
            let command =
                match objective.Kind with
                | ConstraintObjectiveKind.Minimize -> "minimize"
                | ConstraintObjectiveKind.Maximize -> "maximize"

            builder.AppendLine($"({command} {expression objective.Expression})") |> ignore

        builder.AppendLine("(check-sat)") |> ignore
        builder.ToString()

[<RequireQualifiedAccess>]
module Z3Endpoint =
    let private domainContains domain value =
        match domain, value with
        | ConstraintDomain.Boolean, ConstraintValue.Boolean _ -> true
        | ConstraintDomain.UnboundedInteger, ConstraintValue.Integer _ -> true
        | ConstraintDomain.UnboundedReal, ConstraintValue.Real _ -> true
        | ConstraintDomain.IntegerRange(minimum, maximum), ConstraintValue.Integer number ->
            number >= minimum && number <= maximum
        | ConstraintDomain.RealRange(minimum, maximum), ConstraintValue.Real number ->
            ConstraintRational.compare number minimum >= 0
            && ConstraintRational.compare number maximum <= 0
        | _ -> false

    let private validateOutcome problem outcome =
        let variables =
            ConstraintProblem.variables problem
            |> List.map (fun value -> value.Name, value.Domain)
            |> Map.ofList

        let assertionIds =
            ConstraintProblem.assertions problem |> List.map _.Id |> Set.ofList

        let trackedIds =
            ConstraintProblem.assertions problem
            |> List.filter _.TrackForUnsatCore
            |> List.map _.Id
            |> Set.ofList

        let validModel model =
            (model |> Map.keys |> Set.ofSeq) = (variables |> Map.keys |> Set.ofSeq)
            && model |> Map.forall (fun name value -> domainContains variables[name] value)

        match outcome with
        | ConstraintSolverOutcome.Satisfiable model -> validModel model
        | ConstraintSolverOutcome.Optimal(model, objectives) ->
            let expectedObjectives =
                ConstraintProblem.objectives problem
                |> List.map (fun value -> value.Id, value)
                |> Map.ofList

            let expected = expectedObjectives |> Map.keys |> Set.ofSeq
            let supplied = objectives |> List.map _.ObjectiveId |> Set.ofList

            validModel model
            && expected = supplied
            && objectives.Length = supplied.Count
            && objectives
               |> List.forall (fun result ->
                   let expectedSort =
                       ConstraintValidation.sortOf problem expectedObjectives[result.ObjectiveId].Expression

                   ConstraintValue.sort result.Value = expectedSort)
        | ConstraintSolverOutcome.Unsatisfiable core ->
            core |> List.distinct |> List.length = core.Length
            && core
               |> List.forall (fun id -> Set.contains id trackedIds && Set.contains id assertionIds)
        | ConstraintSolverOutcome.Unbounded objective ->
            ConstraintProblem.objectives problem
            |> List.exists (fun value -> value.Id = objective)
        | ConstraintSolverOutcome.Unknown reason -> not (String.IsNullOrWhiteSpace reason)
        | ConstraintSolverOutcome.TimedOut
        | ConstraintSolverOutcome.ResourceLimitExceeded _
        | ConstraintSolverOutcome.Failed _ -> true

    let create engineVersion (timeProvider: TimeProvider) (runner: Z3Runner) =
        if String.IsNullOrWhiteSpace engineVersion || engineVersion <> engineVersion.Trim() then
            invalidArg (nameof engineVersion) "Z3 version must be canonical and non-blank."

        fun authorization (problem: ConstraintProblem) (limits: ReasoningLimits) (cancellationToken: CancellationToken) ->
            task {
                let startedAt = timeProvider.GetUtcNow()
                let timestamp = timeProvider.GetTimestamp()
                ConstraintProblem.requireOwner authorization problem |> ignore

                let request =
                    { Script = SmtLibCompiler.compile problem
                      ExpectedEngineVersion = engineVersion
                      ReturnModel = true
                      ReturnUnsatCore = true
                      ObjectiveIds = ConstraintProblem.objectives problem |> List.map _.Id
                      Timeout = ReasoningLimits.timeout limits
                      MaxMemoryBytes = ReasoningLimits.maxMemoryBytes limits
                      MaxSteps = ReasoningLimits.maxSteps limits
                      MaxResponseBytes = ReasoningLimits.maxResponseBytes limits
                      AllowFileSystem = false
                      AllowNetwork = false
                      AllowProcess = false }

                let finish outcome statistics =
                    { ExecutionId = Guid.NewGuid()
                      EngineId = "z3"
                      EngineVersion = engineVersion
                      Problem = ConstraintProblem.identity problem
                      NormalizedProblem = ReasoningArtifact.create "application/smtlib;version=2.6" request.Script
                      Options =
                        Map.ofList
                            [ "timeout-ms", string request.Timeout.TotalMilliseconds
                              "max-memory-bytes", string request.MaxMemoryBytes
                              "max-steps", string request.MaxSteps
                              "max-response-bytes", string request.MaxResponseBytes
                              "allow-file-system", string request.AllowFileSystem
                              "allow-network", string request.AllowNetwork
                              "allow-process", string request.AllowProcess ]
                      StartedAt = startedAt
                      Duration = timeProvider.GetElapsedTime timestamp
                      Outcome = outcome
                      Statistics = statistics }

                use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                timeout.CancelAfter request.Timeout

                try
                    let! response = runner.RunAsync request timeout.Token
                    let statistics = response.Statistics

                    let outcome =
                        if
                            response.EngineVersion <> engineVersion
                            || statistics.PeakMemoryBytes |> Option.exists (fun value -> value < 0L)
                            || statistics.Steps |> Option.exists (fun value -> value < 0L)
                            || statistics.ResponseBytes |> Option.exists (fun value -> value < 0)
                            || not (validateOutcome problem response.Outcome)
                        then
                            ConstraintSolverOutcome.Failed(
                                "invalid_response",
                                "Z3 returned mismatched identity, malformed values, or invalid statistics."
                            )
                        elif
                            statistics.PeakMemoryBytes
                            |> Option.exists (fun value -> value > request.MaxMemoryBytes)
                        then
                            ConstraintSolverOutcome.ResourceLimitExceeded(
                                ReasoningResourceLimit.Memory,
                                request.MaxMemoryBytes,
                                statistics.PeakMemoryBytes.Value
                            )
                        elif statistics.Steps |> Option.exists (fun value -> value > request.MaxSteps) then
                            ConstraintSolverOutcome.ResourceLimitExceeded(
                                ReasoningResourceLimit.Steps,
                                request.MaxSteps,
                                statistics.Steps.Value
                            )
                        elif
                            statistics.ResponseBytes
                            |> Option.exists (fun value -> value > request.MaxResponseBytes)
                        then
                            ConstraintSolverOutcome.ResourceLimitExceeded(
                                ReasoningResourceLimit.ResponseBytes,
                                int64 request.MaxResponseBytes,
                                int64 statistics.ResponseBytes.Value
                            )
                        else
                            response.Outcome

                    return finish outcome statistics
                with
                | :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                    return
                        finish
                            ConstraintSolverOutcome.TimedOut
                            { Values = Map.empty
                              PeakMemoryBytes = None
                              Steps = None
                              ResponseBytes = None }
                | :? OperationCanceledException as cancellation -> return raise cancellation
                | ex ->
                    return
                        finish
                            (ConstraintSolverOutcome.Failed("native_failure", ex.Message))
                            { Values = Map.empty
                              PeakMemoryBytes = None
                              Steps = None
                              ResponseBytes = None }
            }
