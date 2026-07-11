namespace Nao.Agents

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
