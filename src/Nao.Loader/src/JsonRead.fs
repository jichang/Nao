namespace Nao.Loader

open System.Text.Json
open System.Text.Json.Serialization
open Nao.Agents
open Nao.Core
open Nao.Eval

/// Strict, schema-driven loading of definition JSON.
///
/// Each definition is deserialized directly into a well-typed object via
/// System.Text.Json with full F# support (records, options, unions, lists, maps).
/// The schema is described once by the `*Wire` records below; anything that does not
/// match it — a wrong type, an unknown union tag, an unknown property, or a missing
/// required field — throws instead of being silently defaulted. There are no aliases
/// and no value fallbacks: omission is the ONLY leniency, and only for fields modelled
/// as `option`, which then resolve to their type's natural empty value.
[<RequireQualifiedAccess>]
module JsonRead =

    /// Shared serializer options: snake_case property names, and F# unions encoded with
    /// an internal "type" discriminator plus named fields (fieldless cases collapse to
    /// their bare snake_case name). Unknown properties are rejected.
    let private options =
        let fsharp : JsonFSharpOptions =
            JsonFSharpOptions.Default()
                .WithUnionInternalTag()
                .WithUnionNamedFields()
                .WithUnionTagName("type")
                .WithUnionTagNamingPolicy(JsonNamingPolicy.SnakeCaseLower)
                .WithUnionFieldNamingPolicy(JsonNamingPolicy.SnakeCaseLower)
                .WithUnionUnwrapFieldlessTags()
                .WithSkippableOptionFields()
        let o =
            JsonSerializerOptions(
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)
        o.Converters.Add(JsonFSharpConverter(fsharp))
        o

    let private deserialize<'T> (json: string) : 'T = JsonSerializer.Deserialize<'T>(json, options)

    // ─── Wire schema: an exact, strict mirror of the on-disk JSON ───
    // `option` fields may be omitted (absence is well-defined); every other field is
    // mandatory and its type is enforced.

    type private PromptWire =
        { Role: string option
          Objective: string option
          DomainKnowledge: string list option
          Constraints: string list option
          Examples: PromptExample list option
          OutputFormat: OutputFormat option
          Context: string list option }

    type private CompletionOptionsWire =
        { Temperature: float option
          MaxTokens: int option
          StopSequences: string list option }

    type private AgentWire =
        { Name: string
          Version: string option
          Description: string option
          Provider: string option
          Model: string option
          Prompt: PromptWire option
          Tools: string list option
          SubAgents: string list option
          Options: CompletionOptionsWire option
          MaxRounds: int option
          IsAsync: bool option }

    type private ToolWire =
        { Name: string
          Version: string option
          Description: string option
          /// "prompt" or "executable". Omitted ⇒ "executable".
          Kind: string option
          /// Prompt body — required when kind = "prompt".
          Prompt: PromptWire option
          /// How an executable tool runs — required when kind = "executable".
          Execution: ToolExecutionDef option
          Runtime: string option
          /// Run the executable tool as a background task. Executable tools only.
          IsAsync: bool option
          OutputContentType: string option
          Verify: ToolExecutionDef option
          Revert: ToolExecutionDef option }

    type private EvaluatorWire =
        { Type: string option
          Criteria: string option
          Scale: string option
          Pattern: string option
          Keywords: string list option }

    type private EvalCaseWire =
        { Id: string
          Description: string option
          Input: string option
          Expected: string option
          Tags: string list option
          Metadata: Map<string, string> option }

    type private EvalSuiteWire =
        { Name: string
          Description: string option
          Agent: string option
          Evaluator: EvaluatorWire option
          Cases: EvalCaseWire list option }

    type private ConstitutionRuleWire =
        { Id: string
          Description: string option
          Category: string option
          Priority: int option
          IsHardConstraint: bool option
          Pattern: string option }

    type private ConstitutionWire =
        { Name: string
          Version: string option
          Description: string option
          Rules: ConstitutionRuleWire list option }

    // ─── Wire -> domain mapping (omitted optional fields resolve to natural empties) ───

    let private toPrompt (w: PromptWire) : Prompt =
        { Role = defaultArg w.Role ""
          Objective = defaultArg w.Objective ""
          DomainKnowledge = defaultArg w.DomainKnowledge []
          Constraints = defaultArg w.Constraints []
          Examples = defaultArg w.Examples []
          OutputFormat = defaultArg w.OutputFormat FreeText
          Context = defaultArg w.Context [] }

    let private toCompletionOptions (w: CompletionOptionsWire) : CompletionOptions =
        { Temperature = defaultArg w.Temperature CompletionOptions.Default.Temperature
          MaxTokens = w.MaxTokens
          StopSequences = defaultArg w.StopSequences [] }

    let private toEvaluatorRef (w: EvaluatorWire) : EvaluatorRef =
        { Type = defaultArg w.Type ""
          Criteria = defaultArg w.Criteria ""
          Scale = defaultArg w.Scale ""
          Pattern = defaultArg w.Pattern ""
          Keywords = defaultArg w.Keywords [] }

    let private toEvalCase (w: EvalCaseWire) : EvalCase =
        { Id = w.Id
          Description = defaultArg w.Description ""
          Input = defaultArg w.Input ""
          Expected = w.Expected
          Tags = defaultArg w.Tags []
          Metadata = defaultArg w.Metadata Map.empty }

    let private toConstitutionRule (w: ConstitutionRuleWire) : ConstitutionRuleDef =
        { Id = w.Id
          Description = defaultArg w.Description ""
          Category = defaultArg w.Category ""
          Priority = defaultArg w.Priority 0
          IsHardConstraint = defaultArg w.IsHardConstraint true
          Pattern = defaultArg w.Pattern "" }

    // ─── Public readers: parse a raw JSON document into a definition ───

    let agentDef (json: string) : AgentDef =
        let w = deserialize<AgentWire> json
        { Name = w.Name
          Version = w.Version
          Description = defaultArg w.Description ""
          Provider = defaultArg w.Provider ""
          Model = defaultArg w.Model ""
          Prompt = w.Prompt |> Option.map toPrompt |> Option.defaultValue Prompt.Empty
          Tools = defaultArg w.Tools []
          SubAgents = defaultArg w.SubAgents []
          Options = w.Options |> Option.map toCompletionOptions |> Option.defaultValue CompletionOptions.Default
          MaxRounds = defaultArg w.MaxRounds 5
          IsAsync = defaultArg w.IsAsync false
          Provenance = None }

    let toolDef (json: string) : ToolDef =
        let w = deserialize<ToolWire> json
        let kindStr = (defaultArg w.Kind "executable").Trim().ToLowerInvariant()
        let kind =
            match kindStr with
            | "prompt" ->
                // Prompt tools are LLM-backed and always synchronous; executable-only
                // fields must not appear on them.
                if w.Execution.IsSome then
                    failwithf "Tool '%s': a prompt tool must not declare 'execution'." w.Name
                if w.Runtime.IsSome then
                    failwithf "Tool '%s': a prompt tool must not declare 'runtime'." w.Name
                if defaultArg w.IsAsync false then
                    failwithf "Tool '%s': a prompt tool is always synchronous and must not set 'is_async'." w.Name
                if w.Verify.IsSome || w.Revert.IsSome then
                    failwithf "Tool '%s': a prompt tool must not declare 'verify' or 'revert'." w.Name
                match w.Prompt with
                | Some p -> PromptTool (toPrompt p)
                | None -> failwithf "Tool '%s': a prompt tool requires a 'prompt' block." w.Name
            | "executable" ->
                if w.Prompt.IsSome then
                    failwithf "Tool '%s': an executable tool must not declare a 'prompt' block." w.Name
                match w.Execution with
                | Some e -> ExecutableTool (e, defaultArg w.Runtime "", defaultArg w.IsAsync false)
                | None -> failwithf "Tool '%s': an executable tool requires an 'execution' block." w.Name
            | other ->
                failwithf "Tool '%s': unknown kind '%s' (expected 'prompt' or 'executable')." w.Name other
        { Name = w.Name
          Version = w.Version
          Description = defaultArg w.Description ""
          Kind = kind
          OutputContentType = defaultArg w.OutputContentType ""
          VerifyExecution = w.Verify
          RevertExecution = w.Revert
          Provenance = None }

    let evalSuiteDef (json: string) : EvalSuiteDef =
        let w = deserialize<EvalSuiteWire> json
        { Name = w.Name
          Description = defaultArg w.Description ""
          Agent = defaultArg w.Agent ""
          Evaluator =
            match w.Evaluator with
            | Some e -> toEvaluatorRef e
            | None -> { Type = ""; Criteria = ""; Scale = ""; Pattern = ""; Keywords = [] }
          Cases = defaultArg w.Cases [] |> List.map toEvalCase }

    let constitutionDef (json: string) : ConstitutionDef =
        let w = deserialize<ConstitutionWire> json
        { Name = w.Name
          Version = defaultArg w.Version ""
          Rules = defaultArg w.Rules [] |> List.map toConstitutionRule }
