namespace Nao.Agents

open System

/// Rich schema definition for a tool, extending the basic Tool type
type ToolSchema =
    { /// Unique tool name
      Name: string
      /// Human-readable description of what the tool does
      Description: string
      /// Category/namespace for grouping related tools
      Category: string option
      /// Input parameters schema
      Parameters: ToolParameter list
      /// Description of the return value
      ReturnDescription: string option
      /// Example invocations with expected results
      Examples: ToolExample list
      /// Whether the tool has side effects (read-only vs write)
      IsSideEffectFree: bool
      /// Estimated cost category for invocation
      CostCategory: ToolCostCategory
      /// Version identifier
      Version: string option }

/// Example of tool usage for documentation and few-shot prompting
and ToolExample =
    { /// Natural language description of the scenario
      Scenario: string
      /// The input given to the tool
      Input: string
      /// Expected output
      ExpectedOutput: string }

/// Cost category hint for tool selection optimization
and [<RequireQualifiedAccess>] ToolCostCategory =
    | Free
    | Cheap
    | Moderate
    | Expensive
    | Unknown

module ToolSchema =
    let fromTool (tool: Tool) : ToolSchema =
        { Name = tool.Name
          Description = tool.Description
          Category = None
          Parameters =
            match tool.Schema with
            | [] -> [ { Name = "input"; Description = "Tool input"; Type = "string"; Required = true; Default = None; Examples = [] } ]
            | declared -> declared
          ReturnDescription = None
          Examples = []
          IsSideEffectFree = false
          CostCategory = ToolCostCategory.Unknown
          Version = tool.Version }

    let render (schema: ToolSchema) : string =
        let paramsStr =
            schema.Parameters
            |> List.map (fun p ->
                let reqStr = if p.Required then " (required)" else " (optional)"
                sprintf "    - %s: %s [%s]%s" p.Name p.Description p.Type reqStr)
            |> String.concat "\n"

        let examplesStr =
            schema.Examples
            |> List.map (fun ex -> sprintf "    Example: %s\n      Input: %s\n      Output: %s" ex.Scenario ex.Input ex.ExpectedOutput)
            |> String.concat "\n"

        let lines =
            [ yield sprintf "  %s: %s" schema.Name schema.Description
              if schema.Parameters.Length > 0 then yield sprintf "  Parameters:\n%s" paramsStr
              if schema.Examples.Length > 0 then yield sprintf "  Examples:\n%s" examplesStr ]

        String.concat "\n" lines
