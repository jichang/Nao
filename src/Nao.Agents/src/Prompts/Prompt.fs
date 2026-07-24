namespace Nao.Agents

/// An operation for replacing or transforming a text prompt field.
type PromptTextOperation =
    | ReplaceText of string
    | AppendText of string
    | UpdateText of (string -> string)

/// An operation for replacing, extending, or transforming a list prompt field.
type PromptListOperation<'T> =
    | ReplaceList of 'T list
    | AppendList of 'T list
    | UpdateList of ('T list -> 'T list)

/// An operation for replacing or transforming a scalar prompt field.
type PromptValueOperation<'T> =
    | ReplaceValue of 'T
    | UpdateValue of ('T -> 'T)

/// Field-level changes to apply to a prompt.
type PromptPatch =
    { Role: PromptTextOperation option
      Objective: PromptTextOperation option
      DomainKnowledge: PromptListOperation<string> option
      Constraints: PromptListOperation<string> option
      Examples: PromptListOperation<PromptExample> option
      OutputFormat: PromptValueOperation<OutputFormat> option
      Context: PromptListOperation<string> option }

    static member Empty =
        { Role = None
          Objective = None
          DomainKnowledge = None
          Constraints = None
          Examples = None
          OutputFormat = None
          Context = None }

/// A structured prompt definition following prompt engineering best practices
type Prompt =
    { /// The agent's role and identity (e.g. "You are a financial analyst...")
      Role: string

      /// The specific task or objective the agent should accomplish
      Objective: string

      /// Domain-specific knowledge and context the agent needs
      DomainKnowledge: string list

      /// Constraints and rules the agent must follow
      Constraints: string list

      /// Few-shot examples demonstrating expected behavior
      Examples: PromptExample list

      /// Desired output format
      OutputFormat: OutputFormat

      /// Additional context injected at runtime (e.g. retrieved documents)
      Context: string list }

    static member Empty =
        { Role = ""
          Objective = ""
          DomainKnowledge = []
          Constraints = []
          Examples = []
          OutputFormat = FreeText
          Context = [] }

/// Functions for working with structured prompts
module Prompt =

    let private applyText operation value =
        match operation with
        | ReplaceText replacement -> replacement
        | AppendText suffix -> value + suffix
        | UpdateText update -> update value

    let private applyList operation value =
        match operation with
        | ReplaceList replacement -> replacement
        | AppendList additions -> value @ additions
        | UpdateList update -> update value

    let private applyValue operation value =
        match operation with
        | ReplaceValue replacement -> replacement
        | UpdateValue update -> update value

    /// Apply explicit field-level operations to a prompt.
    let applyPatch (patch: PromptPatch) (prompt: Prompt) =
        { prompt with
            Role = patch.Role |> Option.map (fun operation -> applyText operation prompt.Role) |> Option.defaultValue prompt.Role
            Objective = patch.Objective |> Option.map (fun operation -> applyText operation prompt.Objective) |> Option.defaultValue prompt.Objective
            DomainKnowledge = patch.DomainKnowledge |> Option.map (fun operation -> applyList operation prompt.DomainKnowledge) |> Option.defaultValue prompt.DomainKnowledge
            Constraints = patch.Constraints |> Option.map (fun operation -> applyList operation prompt.Constraints) |> Option.defaultValue prompt.Constraints
            Examples = patch.Examples |> Option.map (fun operation -> applyList operation prompt.Examples) |> Option.defaultValue prompt.Examples
            OutputFormat = patch.OutputFormat |> Option.map (fun operation -> applyValue operation prompt.OutputFormat) |> Option.defaultValue prompt.OutputFormat
            Context = patch.Context |> Option.map (fun operation -> applyList operation prompt.Context) |> Option.defaultValue prompt.Context }

    /// Render a structured prompt into a single system message string.
    /// Combines role, objective, domain knowledge, constraints, examples,
    /// output format, and context into a well-formatted markdown prompt.
    let render (prompt: Prompt) =
        let sections = ResizeArray<string>()

        if prompt.Role <> "" then
            sections.Add(sprintf "# Role\n%s" prompt.Role)

        if prompt.Objective <> "" then
            sections.Add(sprintf "# Objective\n%s" prompt.Objective)

        if prompt.DomainKnowledge <> [] then
            let items = prompt.DomainKnowledge |> List.map (sprintf "- %s") |> String.concat "\n"
            sections.Add(sprintf "# Domain Knowledge\n%s" items)

        if prompt.Constraints <> [] then
            let items = prompt.Constraints |> List.map (sprintf "- %s") |> String.concat "\n"
            sections.Add(sprintf "# Constraints\n%s" items)

        if prompt.Examples <> [] then
            let examples =
                prompt.Examples
                |> List.mapi (fun i ex ->
                    let explanation =
                        match ex.Explanation with
                        | Some e -> sprintf "\nExplanation: %s" e
                        | None -> ""
                    sprintf "## Example %d\nInput: %s\nOutput: %s%s" (i + 1) ex.Input ex.Output explanation)
                |> String.concat "\n\n"
            sections.Add(sprintf "# Examples\n%s" examples)

        match prompt.OutputFormat with
        | FreeText -> ()
        | Json schema ->
            let schemaNote = schema |> Option.map (sprintf "\nSchema: %s") |> Option.defaultValue ""
            sections.Add(sprintf "# Output Format\nRespond in JSON.%s" schemaNote)
        | Markdown ->
            sections.Add("# Output Format\nRespond in Markdown.")
        | Custom instruction ->
            sections.Add(sprintf "# Output Format\n%s" instruction)

        if prompt.Context <> [] then
            let items = prompt.Context |> List.map (sprintf "- %s") |> String.concat "\n"
            sections.Add(sprintf "# Context\n%s" items)

        sections |> Seq.toList |> String.concat "\n\n"
