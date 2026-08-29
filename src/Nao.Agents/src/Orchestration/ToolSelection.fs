namespace Nao.Agents

open System.Threading.Tasks

/// Limits applied when selecting tool schemas for an LLM context window.
type ToolSelectionConfig =
    { MaxTools: int
      RelevanceThreshold: float }

    static member Default =
        { MaxTools = 20
          RelevanceThreshold = 0.1 }

/// Selects the tools relevant to a task and available context budget.
type IToolSelector =
    abstract member SelectAsync:
        taskDescription: string ->
        availableTokenBudget: int ->
        tools: ITool list ->
        Task<ITool list>

/// Keyword-based tool selector with no registry or persistent state.
type ToolSelector(config: ToolSelectionConfig) =
    let relevance (query: string) (tool: ITool) =
        let queryWords = query.ToLowerInvariant().Split(' ') |> Set.ofArray
        let toolWords =
            (sprintf "%s %s" tool.Name tool.Description).ToLowerInvariant().Split(' ')
            |> Set.ofArray
        let overlap = Set.intersect queryWords toolWords |> Set.count
        float overlap / float (max 1 queryWords.Count)

    let estimateTokens tool =
        let rendered = Tool.render tool
        (rendered.Length + 3) / 4

    interface IToolSelector with
        member _.SelectAsync taskDescription availableTokenBudget tools =
            let ranked =
                tools
                |> List.map (fun tool -> tool, relevance taskDescription tool)
                |> List.filter (fun (_, score) -> score >= config.RelevanceThreshold)
                |> List.sortByDescending snd

            let mutable remainingTokens = availableTokenBudget
            let selected = ResizeArray<ITool>()
            for tool, _ in ranked do
                let tokens = estimateTokens tool
                if selected.Count < config.MaxTools && tokens <= remainingTokens then
                    selected.Add tool
                    remainingTokens <- remainingTokens - tokens

            selected |> Seq.toList |> Task.FromResult
