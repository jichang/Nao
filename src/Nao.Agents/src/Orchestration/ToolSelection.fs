namespace Nao.Agents

open System
open System.Text.RegularExpressions
open System.Threading.Tasks

/// Limits applied when selecting tool schemas for an LLM context window.
type ToolSelectionConfig =
    { MaxTools: int
      RelevanceThreshold: float }

    static member Default =
        { MaxTools = 20
          RelevanceThreshold = 0.1 }

/// Functional capability for selecting tools relevant to a task and context budget.
type ToolSelection = {
    Available: Tool list
    Selected: Tool list
}

/// Functional capability for discovering and selecting tools relevant to a task and context budget.
type ToolSelector = {
    SelectAsync: string -> int -> ToolProtocol -> Task<ToolSelection>
}

[<RequireQualifiedAccess>]
module ToolSelector =
    /// Creates a keyword-based selector with no registry or persistent state.
    let create (config: ToolSelectionConfig) =
        let terms text =
            if String.IsNullOrWhiteSpace text then
                Set.empty
            else
                Regex.Replace(text, "([a-z0-9])([A-Z])", "$1 $2")
                |> fun value -> Regex.Split(value.ToLowerInvariant(), "[^a-z0-9]+")
                |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                |> Set.ofSeq

        let relevance queryTerms (tool: Tool) =
            if Set.isEmpty queryTerms then
                0.0
            else
                let weightedCoverage weight text =
                    Set.intersect queryTerms (terms text)
                    |> Set.count
                    |> float
                    |> (*) weight

                let weightedMatches =
                    weightedCoverage 1.0 tool.Name
                    + weightedCoverage 0.65 tool.Description
                    + weightedCoverage 0.5 tool.Schema.Input
                    + weightedCoverage 0.35 tool.Schema.Output

                weightedMatches / float queryTerms.Count

        let estimateTokens tool =
            let rendered = Tool.render tool
            (rendered.Length + 3) / 4

        let selectAsync taskDescription availableTokenBudget (protocol: ToolProtocol) =
            task {
                let! tools = protocol.ListTools()
                let queryTerms = terms taskDescription
                let ranked =
                    tools
                    |> List.map (fun tool -> tool, relevance queryTerms tool)
                    |> List.sortBy (fun (tool, score) -> -score, -tool.Priority, tool.Name)

                let relevant =
                    ranked
                    |> List.filter (fun (_, score) -> score >= config.RelevanceThreshold && score > 0.0)

                let candidates =
                    if List.isEmpty relevant then ranked |> List.truncate 1
                    else relevant

                let mutable remainingTokens = max 0 availableTokenBudget
                let selected = ResizeArray<Tool>()
                for tool, _ in candidates do
                    let tokens = estimateTokens tool
                    if selected.Count < max 0 config.MaxTools && tokens <= remainingTokens then
                        selected.Add tool
                        remainingTokens <- remainingTokens - tokens

                return
                    { Available = tools
                      Selected = selected |> Seq.toList }
            }

        { SelectAsync = selectAsync }
