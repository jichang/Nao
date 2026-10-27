namespace Nao.Reasoning.Datalog

#nowarn "3511"

open System
open System.Globalization
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Reasoning

type SouffleRunnerRequest =
    { Program: string
      Timeout: TimeSpan
      MaxMemoryBytes: int64
      MaxSteps: int64
      MaxOutputRows: int
      MaxOutputBytes: int
      AllowFileSystem: bool
      AllowNetwork: bool
      AllowProcess: bool
      ExternalFunctors: Set<string> }

type SouffleRunnerResponse =
    { EngineVersion: string
      Rows: DatalogQueryRow list
      ResponseBytes: int
      PeakMemoryBytes: int64
      Steps: int64
      TimedOut: bool
      Failure: (string * string) option }

type SouffleRunner =
    private
        { RunAsync: SouffleRunnerRequest -> CancellationToken -> Task<SouffleRunnerResponse> }

[<RequireQualifiedAccess>]
module SouffleRunner =
    let create runAsync = { RunAsync = runAsync }

module private SouffleCompiler =
    let private typeName =
        function
        | DatalogType.Symbol -> "symbol"
        | DatalogType.Signed -> "number"
        | DatalogType.Unsigned -> "unsigned"
        | DatalogType.Float -> "float"

    let private value =
        function
        | DatalogValue.Symbol text -> JsonSerializer.Serialize text
        | DatalogValue.Signed number -> string number
        | DatalogValue.Unsigned number -> string number
        | DatalogValue.Float number -> number.ToString("R", CultureInfo.InvariantCulture)

    let private variable (name: string) =
        if Char.IsUpper name[0] then
            name
        else
            Char.ToUpperInvariant(name[0]).ToString() + name.Substring 1

    let private term (termValue: DatalogTerm) =
        match termValue with
        | DatalogTerm.Variable name -> variable name
        | DatalogTerm.Constant constant -> value constant

    let private atom (value: DatalogAtom) =
        let terms = value.Terms |> List.map term |> String.concat ", "
        $"{value.Relation}({terms})"

    let compile (program: DatalogProgram) =
        let declarations =
            DatalogProgram.relations program
            |> List.map (fun relation ->
                let columns =
                    relation.Columns
                    |> List.map (fun column -> $"{column.Name}:{typeName column.Type}")
                    |> String.concat ", "

                $".decl {relation.Name}({columns})")

        let facts =
            DatalogProgram.facts program
            |> List.map (fun fact ->
                let values = fact.Values |> List.map value |> String.concat ", "
                $"{fact.Relation}({values}).")

        let rules =
            DatalogProgram.rules program
            |> List.map (fun rule ->
                let body =
                    rule.Body
                    |> List.map (function
                        | DatalogLiteral.Positive value -> atom value
                        | DatalogLiteral.Negative value -> $"!{atom value}")
                    |> String.concat ", "

                $"{atom rule.Head} :- {body}.")

        let query = DatalogProgram.query program

        let relations =
            DatalogProgram.relations program
            |> List.map (fun value -> value.Name, value)
            |> Map.ofList

        let relation = relations[query.Atom.Relation]

        let variableTypes =
            List.zip query.Atom.Terms relation.Columns
            |> List.choose (fun (termValue, column) ->
                match termValue with
                | DatalogTerm.Variable name -> Some(name, column.Type)
                | DatalogTerm.Constant _ -> None)
            |> Map.ofList

        let outputColumns =
            query.OutputVariables
            |> List.map (fun name -> $"{name}:{typeName variableTypes[name]}")
            |> String.concat ", "

        let outputTerms = query.OutputVariables |> List.map variable |> String.concat ", "

        declarations
        @ facts
        @ rules
        @ [ $".decl __nao_query({outputColumns})"
            $"__nao_query({outputTerms}) :- {atom query.Atom}."
            ".output __nao_query" ]
        |> String.concat "\n"

[<RequireQualifiedAccess>]
module SouffleEndpoint =
    let private validateRows (program: DatalogProgram) rows =
        let query = DatalogProgram.query program

        let relation =
            DatalogProgram.relations program
            |> List.find (fun value -> value.Name = query.Atom.Relation)

        let variableTypes =
            List.zip query.Atom.Terms relation.Columns
            |> List.choose (fun (termValue, column) ->
                match termValue with
                | DatalogTerm.Variable name -> Some(name, column.Type)
                | DatalogTerm.Constant _ -> None)
            |> Map.ofList

        let expected = query.OutputVariables |> Set.ofList
        let ruleIds = DatalogProgram.rules program |> List.map _.Id |> Set.ofList
        let factIds = DatalogProgram.facts program |> List.map _.Id |> Set.ofList

        rows
        |> List.forall (fun row ->
            let supplied = row.Binding |> Map.keys |> Set.ofSeq

            supplied = expected
            && row.Binding
               |> Map.forall (fun name value ->
                   DatalogValue.validate value
                   DatalogValue.valueType value = variableTypes[name])
            && row.Derivations
               |> List.forall (fun derivation ->
                   Set.contains derivation.RuleId ruleIds
                   && derivation.PremiseFactIds |> List.forall (fun id -> Set.contains id factIds)))

    let create
        engineVersion
        maxHostFacts
        (timeProvider: TimeProvider)
        (providers: DatalogHostRelationProvider list)
        (runner: SouffleRunner)
        =
        if String.IsNullOrWhiteSpace engineVersion || engineVersion <> engineVersion.Trim() then
            invalidArg (nameof engineVersion) "Souffle version must be canonical and non-blank."

        if maxHostFacts <= 0 then
            invalidArg (nameof maxHostFacts) "Souffle host-fact limit must be positive."

        let providerMap =
            providers |> List.map (fun value -> value.RelationName, value) |> Map.ofList

        if providerMap.Count <> providers.Length then
            invalidArg (nameof providers) "Datalog host relation providers must be unique."

        fun authorization (program: DatalogProgram) (limits: ReasoningLimits) cancellationToken ->
            task {
                let startedAt = timeProvider.GetUtcNow()
                let timestamp = timeProvider.GetTimestamp()
                DatalogProgram.requireOwner authorization program |> ignore

                let hostRelations =
                    DatalogProgram.relations program
                    |> List.filter (fun value -> value.Kind = DatalogRelationKind.HostMaterialized)

                let expectedProviders = hostRelations |> List.map _.Name |> Set.ofList
                let suppliedProviders = providers |> List.map _.RelationName |> Set.ofList

                if expectedProviders <> suppliedProviders then
                    invalidArg
                        (nameof providers)
                        "Every host-materialized relation requires exactly one declared provider."

                let mutable hostFacts = []

                for relation in hostRelations do
                    let request =
                        { Relation = relation
                          MaxFacts = maxHostFacts - hostFacts.Length }

                    let! facts = providerMap[relation.Name].ResolveAsync authorization request cancellationToken
                    hostFacts <- hostFacts @ facts

                    if hostFacts.Length > maxHostFacts then
                        invalidOp "Datalog host relation provider exceeded its declared fact limit."

                let validated =
                    DatalogValidation.create
                        authorization
                        (DatalogProgram.identity program)
                        (DatalogProgram.relations program)
                        (DatalogProgram.facts program @ hostFacts)
                        (DatalogProgram.rules program)
                        (DatalogProgram.query program)

                let runnerRequest =
                    { Program = SouffleCompiler.compile validated
                      Timeout = ReasoningLimits.timeout limits
                      MaxMemoryBytes = ReasoningLimits.maxMemoryBytes limits
                      MaxSteps = ReasoningLimits.maxSteps limits
                      MaxOutputRows = ReasoningLimits.maxConclusions limits
                      MaxOutputBytes = ReasoningLimits.maxResponseBytes limits
                      AllowFileSystem = false
                      AllowNetwork = false
                      AllowProcess = false
                      ExternalFunctors = Set.empty }

                use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                timeout.CancelAfter runnerRequest.Timeout

                let finish outcome =
                    { Provenance =
                        { ExecutionId = Guid.NewGuid()
                          EngineId = "souffle"
                          EngineVersion = engineVersion
                          RuleSet = DatalogProgram.identity program
                          StartedAt = startedAt
                          Duration = timeProvider.GetElapsedTime timestamp }
                      Outcome = outcome }

                try
                    let! response = runner.RunAsync runnerRequest timeout.Token

                    if
                        response.ResponseBytes < 0
                        || response.PeakMemoryBytes < 0L
                        || response.Steps < 0L
                        || not (validateRows validated response.Rows)
                    then
                        return
                            finish (
                                DatalogExecutionOutcome.RunnerFailure(
                                    "invalid_response",
                                    "Souffle returned malformed bindings, derivations, or usage."
                                )
                            )
                    elif response.EngineVersion <> engineVersion then
                        return
                            finish (
                                DatalogExecutionOutcome.RunnerFailure("identity_mismatch", "Souffle version mismatch.")
                            )
                    elif response.TimedOut then
                        return finish DatalogExecutionOutcome.TimedOut
                    elif response.PeakMemoryBytes > runnerRequest.MaxMemoryBytes then
                        return
                            finish (
                                DatalogExecutionOutcome.ResourceLimitExceeded(
                                    ReasoningResourceLimit.Memory,
                                    runnerRequest.MaxMemoryBytes,
                                    response.PeakMemoryBytes
                                )
                            )
                    elif response.Steps > runnerRequest.MaxSteps then
                        return
                            finish (
                                DatalogExecutionOutcome.ResourceLimitExceeded(
                                    ReasoningResourceLimit.Steps,
                                    runnerRequest.MaxSteps,
                                    response.Steps
                                )
                            )
                    elif response.Rows.Length > runnerRequest.MaxOutputRows then
                        return
                            finish (
                                DatalogExecutionOutcome.ResourceLimitExceeded(
                                    ReasoningResourceLimit.Conclusions,
                                    int64 runnerRequest.MaxOutputRows,
                                    int64 response.Rows.Length
                                )
                            )
                    elif response.ResponseBytes > runnerRequest.MaxOutputBytes then
                        return
                            finish (
                                DatalogExecutionOutcome.ResourceLimitExceeded(
                                    ReasoningResourceLimit.ResponseBytes,
                                    int64 runnerRequest.MaxOutputBytes,
                                    int64 response.ResponseBytes
                                )
                            )
                    else
                        match response.Failure with
                        | Some(code, detail) -> return finish (DatalogExecutionOutcome.RunnerFailure(code, detail))
                        | None -> return finish (DatalogExecutionOutcome.Completed response.Rows)
                with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                    return finish DatalogExecutionOutcome.TimedOut
            }
