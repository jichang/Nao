namespace Nao.Agents

open System.Threading.Tasks

/// Tool selection result with reasoning
type ToolSelection =
    { /// The selected tool schema
      Tool: ToolSchema
      /// Confidence score (0.0 to 1.0)
      Confidence: float
      /// Reasoning for the selection
      Reasoning: string option }

/// Routes tool requests to the appropriate tool based on strategy
module ToolRouter =

    /// Select a tool using pattern matching on keywords
    let selectByPattern (patterns: Map<string, string list>) (query: string) (tools: ToolSchema list) : ToolSelection option =
        let lowerQuery = query.ToLowerInvariant()
        patterns
        |> Map.tryPick (fun toolName keywords ->
            let matchCount =
                keywords
                |> List.filter (fun kw -> lowerQuery.Contains(kw.ToLowerInvariant()))
                |> List.length
            if matchCount > 0 then
                tools
                |> List.tryFind (fun t -> t.Name = toolName)
                |> Option.map (fun t ->
                    { Tool = t
                      Confidence = float matchCount / float keywords.Length
                      Reasoning = Some (sprintf "Matched %d/%d keywords" matchCount keywords.Length) })
            else None)

    /// Select a tool by exact name match
    let selectByName (name: string) (tools: ToolSchema list) : ToolSelection option =
        tools
        |> List.tryFind (fun t -> t.Name = name)
        |> Option.map (fun t ->
            { Tool = t
              Confidence = 1.0
              Reasoning = Some "Exact name match" })

    /// Composite selection: tries strategies in order until one succeeds
    let selectWithFallback (strategies: (string -> ToolSchema list -> Task<ToolSelection option>) list) (query: string) (tools: ToolSchema list) : Task<ToolSelection option> =
        task {
            let mutable result = None
            let mutable i = 0
            while result.IsNone && i < strategies.Length do
                let! selection = strategies.[i] query tools
                result <- selection
                i <- i + 1
            return result
        }
