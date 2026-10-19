namespace Nao.Eval.Evaluators

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Nao.Agents
open Nao.Eval

type StructuredOutputSchema =
    { Identity: EvalComponentIdentity
      Validate: JsonElement -> Result<unit, string> }

[<RequireQualifiedAccess>]
module StructuredOutputSchema =
    let create name version configuration validate =
        { Identity = EvalComponentIdentity.create name version configuration
          Validate = validate }

module DeterministicExecution =
    let private terminalState =
        function
        | ExecutionTerminalStatus.Succeeded -> EvalExpectedTerminalState.Succeeded
        | ExecutionTerminalStatus.Failed _ -> EvalExpectedTerminalState.Failed
        | ExecutionTerminalStatus.Cancelled -> EvalExpectedTerminalState.Cancelled
        | ExecutionTerminalStatus.TimedOut -> EvalExpectedTerminalState.TimedOut
        | ExecutionTerminalStatus.Denied _ -> EvalExpectedTerminalState.Denied
        | ExecutionTerminalStatus.LimitExceeded _ -> EvalExpectedTerminalState.LimitExceeded
        | ExecutionTerminalStatus.Indeterminate _ -> EvalExpectedTerminalState.Indeterminate

    let private sha256 (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private jsonValue (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.String -> value.GetString()
        | JsonValueKind.Number
        | JsonValueKind.True
        | JsonValueKind.False
        | JsonValueKind.Null -> value.GetRawText()
        | _ -> value.GetRawText()

    let private validateStructured
        (schema: StructuredOutputSchema option)
        (expected: EvalExpectedBehavior)
        (actual: string)
        =
        if schema.IsNone && Map.isEmpty expected.Properties then
            []
        else
            try
                use document = JsonDocument.Parse actual
                let root = document.RootElement

                let schemaFailures =
                    match schema with
                    | Some validator ->
                        match validator.Validate root with
                        | Ok() -> []
                        | Error failure -> [ "schema: " + failure ]
                    | None -> []

                let propertyFailures =
                    expected.Properties
                    |> Map.toList
                    |> List.choose (fun (name, expectedValue) ->
                        let mutable property = Unchecked.defaultof<JsonElement>

                        if
                            root.ValueKind <> JsonValueKind.Object
                            || not (root.TryGetProperty(name, &property))
                        then
                            Some(sprintf "property '%s' is missing" name)
                        elif jsonValue property <> expectedValue then
                            Some(sprintf "property '%s' does not equal '%s'" name expectedValue)
                        else
                            None)

                schemaFailures @ propertyFailures
            with :? JsonException as error ->
                [ "structured output is invalid JSON: " + error.Message ]

    let private validateTools (expected: EvalExpectedBehavior) (trace: ExecutionTrace option) =
        let observed =
            trace
            |> Option.map _.Steps
            |> Option.defaultValue []
            |> List.choose (fun step ->
                match step.Action with
                | TraceAction.ToolInvocation toolName -> Some(toolName, step.Input)
                | _ -> None)

        let countFailures =
            expected.ToolCalls
            |> List.choose (fun toolCall ->
                let matching = observed |> List.filter (fst >> (=) toolCall.Tool)
                let count = matching.Length

                if count < toolCall.MinimumCount || count > toolCall.MaximumCount then
                    Some(
                        sprintf
                            "tool '%s' count %d is outside [%d,%d]"
                            toolCall.Tool
                            count
                            toolCall.MinimumCount
                            toolCall.MaximumCount
                    )
                elif
                    toolCall.ArgumentsSha256
                    |> Option.exists (fun expectedHash ->
                        matching |> List.exists (snd >> sha256 >> (=) expectedHash) |> not)
                then
                    Some(sprintf "tool '%s' has no invocation with the expected argument hash" toolCall.Tool)
                else
                    None)

        let expectedSequence =
            expected.ToolCalls
            |> List.collect (fun toolCall -> List.replicate toolCall.MinimumCount toolCall.Tool)

        let observedRequiredSequence =
            observed
            |> List.map fst
            |> List.filter (fun tool -> expectedSequence |> List.contains tool)
            |> List.truncate expectedSequence.Length

        let sequenceFailures =
            if observedRequiredSequence = expectedSequence then
                []
            else
                [ sprintf
                      "tool sequence [%s] does not match required sequence [%s]"
                      (String.concat "," observedRequiredSequence)
                      (String.concat "," expectedSequence) ]

        countFailures @ sequenceFailures

    let private validateArtifacts (expected: EvalExpectedBehavior) (artifacts: Artifact list) =
        expected.Artifacts
        |> List.choose (fun expectedArtifact ->
            let matches =
                artifacts
                |> List.filter (fun artifact ->
                    Artifact.contentType artifact = expectedArtifact.MediaType
                    && (expectedArtifact.ContentSha256
                        |> Option.forall (fun hash ->
                            artifact |> Artifact.contentHash |> ArtifactContentHash.value = hash)))

            if not expectedArtifact.RequiredMetadata.IsEmpty then
                Some(
                    sprintf
                        "artifact '%s' requires metadata but the artifact contract has no metadata evidence"
                        expectedArtifact.MediaType
                )
            elif matches.IsEmpty then
                Some(sprintf "required artifact '%s' was not produced" expectedArtifact.MediaType)
            else
                None)

    let private validateCitations (expected: EvalExpectedBehavior) (actual: string) =
        expected.Citations
        |> List.choose (fun citation ->
            let token =
                match citation.ChunkId with
                | Some chunkId -> sprintf "[%s#%s]" citation.SourceId chunkId
                | None -> sprintf "[%s]" citation.SourceId

            if actual.Contains(token, StringComparison.Ordinal) then
                None
            else
                Some(sprintf "required citation '%s' is missing" token))

    let private validateForbidden (expected: EvalExpectedBehavior) (execution: ExecutionResult) =
        let observedActions =
            [ yield!
                  execution.Evidence.Trace
                  |> Option.map _.Steps
                  |> Option.defaultValue []
                  |> List.map (fun step -> sprintf "%A" step.Action)

              yield! execution.PolicyDecisions.PolicyEvaluations |> List.map _.Action ]

        expected.ForbiddenActions
        |> List.choose (fun forbidden ->
            if
                observedActions
                |> List.exists (fun action -> action.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            then
                Some(sprintf "forbidden action '%s' was observed" forbidden)
            else
                None)

    let private validateBudgets (expected: EvalExpectedBehavior) (execution: ExecutionResult) =
        match expected.Budgets with
        | None -> []
        | Some budgets ->
            [ match budgets.MaximumDuration with
              | Some maximum when execution.Usage.ElapsedTime > maximum ->
                  yield sprintf "duration %O exceeds %O" execution.Usage.ElapsedTime maximum
              | _ -> ()

              match budgets.MaximumToolCalls with
              | Some maximum when execution.Usage.ToolCalls > maximum ->
                  yield sprintf "tool calls %d exceed %d" execution.Usage.ToolCalls maximum
              | _ -> ()

              match budgets.MaximumCostUsd with
              | Some maximum when execution.Usage.EstimatedCostUsd > maximum ->
                  yield sprintf "cost %M exceeds %M" execution.Usage.EstimatedCostUsd maximum
              | _ -> ()

              match budgets.MaximumInputTokens, execution.Evidence.Metrics with
              | Some maximum, Some metrics ->
                  match metrics.TotalInputTokens with
                  | Some actual when actual > maximum -> yield sprintf "input tokens %d exceed %d" actual maximum
                  | None -> yield "input-token budget cannot be verified without a provider-reported split"
                  | _ -> ()
              | Some _, None -> yield "input-token budget cannot be verified without metrics"
              | _ -> ()

              match budgets.MaximumOutputTokens, execution.Evidence.Metrics with
              | Some maximum, Some metrics ->
                  match metrics.TotalOutputTokens with
                  | Some actual when actual > maximum -> yield sprintf "output tokens %d exceed %d" actual maximum
                  | None -> yield "output-token budget cannot be verified without a provider-reported split"
                  | _ -> ()
              | Some _, None -> yield "output-token budget cannot be verified without metrics"
              | _ -> () ]

    let create (schema: StructuredOutputSchema option) =
        let schemaIdentity =
            schema
            |> Option.map (fun value ->
                String.concat
                    ":"
                    [ EvalComponentIdentity.name value.Identity
                      EvalComponentIdentity.version value.Identity
                      EvalComponentIdentity.configurationSha256 value.Identity ])
            |> Option.defaultValue "none"

        Evaluator.deterministic
            "DeterministicExecution"
            "1"
            ("schema=" + schemaIdentity)
            true
            "Fails closed on malformed structured output, missing execution evidence, or any expected-behavior violation."
            (fun context case actual ->
                task {
                    let expected = EvalCase.expectedBehavior case

                    let failures =
                        [ if not (expected.TerminalStates.Contains(terminalState context.Execution.Status)) then
                              yield sprintf "unexpected terminal state %A" context.Execution.Status

                          yield! validateStructured schema expected actual
                          yield! validateTools expected context.Execution.Evidence.Trace
                          yield! validateArtifacts expected context.Execution.Outputs.Artifacts
                          yield! validateCitations expected actual
                          yield! validateForbidden expected context.Execution
                          yield! validateBudgets expected context.Execution ]

                    if failures.IsEmpty then
                        return EvalVerdict.Pass, "All deterministic execution expectations passed."
                    else
                        return EvalVerdict.Fail, String.concat "; " failures
                })

    let evaluator = create None
