namespace Nao.Loader

open System.IO
open Nao.Agents
open Nao.Agents
open Nao.Eval

/// Combined result of loading from all sources
type WorkspaceDefinitions =
    { /// All agent definitions (from JSON, etc.)
      AgentDefs: AgentDef list
      /// All tool definitions (from JSON, etc.)
      ToolDefs: ToolDef list
      /// All eval suite definitions
      EvalSuiteDefs: EvalSuiteDef list
      /// All constitution definitions (governance)
      ConstitutionDefs: ConstitutionDef list
      /// Pre-built agents (from assemblies, etc.)
      Agents: IAgent list
      /// Pre-built tools (from assemblies, etc.)
      Tools: Tool list
      /// Pre-built evaluators (from assemblies, etc.)
      Evaluators: IEvaluator list
      /// Any errors encountered during loading
      Errors: LoadError list }

/// Loads definitions from multiple sources and merges results
module WorkspaceLoader =

    /// Load from a single source, splitting results from errors
    let private loadSource (source: IDefinitionSource) =
        let result = source.Load()
        let agentDefs, agentErrors =
            result.Agents
            |> List.partition (fun r -> match r with Result.Ok _ -> true | _ -> false)
        let toolDefs, toolErrors =
            result.Tools
            |> List.partition (fun r -> match r with Result.Ok _ -> true | _ -> false)
        let evalDefs, evalErrors =
            result.EvalSuites
            |> List.partition (fun r -> match r with Result.Ok _ -> true | _ -> false)
        let constDefs, constErrors =
            result.Constitutions
            |> List.partition (fun r -> match r with Result.Ok _ -> true | _ -> false)

        let unwrapOk results =
            results |> List.choose (fun r -> match r with Result.Ok v -> Some v | _ -> None)
        let unwrapErr results =
            results |> List.choose (fun r -> match r with Result.Error e -> Some e | _ -> None)

        let defs =
            { AgentDefs = unwrapOk agentDefs
              ToolDefs = unwrapOk toolDefs
              EvalSuiteDefs = unwrapOk evalDefs
              ConstitutionDefs = unwrapOk constDefs
              Agents = result.BuiltAgents
              Tools = result.BuiltTools
              Evaluators = result.BuiltEvaluators
              Errors = unwrapErr agentErrors @ unwrapErr toolErrors @ unwrapErr evalErrors @ unwrapErr constErrors }
        defs

    /// Load definitions from multiple sources and merge
    let load (sources: IDefinitionSource list) : WorkspaceDefinitions =
        let results = sources |> List.map loadSource
        { AgentDefs = results |> List.collect (fun r -> r.AgentDefs)
          ToolDefs = results |> List.collect (fun r -> r.ToolDefs)
          EvalSuiteDefs = results |> List.collect (fun r -> r.EvalSuiteDefs)
          ConstitutionDefs = results |> List.collect (fun r -> r.ConstitutionDefs)
          Agents = results |> List.collect (fun r -> r.Agents)
          Tools = results |> List.collect (fun r -> r.Tools)
          Evaluators = results |> List.collect (fun r -> r.Evaluators)
          Errors = results |> List.collect (fun r -> r.Errors) }

    /// Load a typical workspace: .nao/ JSON files + optional plugins/ assembly directory
    ///
    /// Expected structure:
    ///   <workspaceRoot>/
    ///   ├── .nao/
    ///   │   ├── agents/       ← JSON agent definitions
    ///   │   ├── tools/        ← JSON tool definitions
    ///   │   └── evals/        ← JSON eval suite definitions
    ///   └── plugins/          ← .NET assembly plugins (optional)
    ///       ├── MyTools.dll
    ///       └── MyAgents.dll
    let loadWorkspace (workspaceRoot: string) : WorkspaceDefinitions =
        let sources = ResizeArray<IDefinitionSource>()

        // JSON definitions from .nao/
        let naoDir = Path.Combine(workspaceRoot, ".nao")
        if Directory.Exists naoDir then
            sources.Add(JsonSource(naoDir))

        // Assembly plugins from plugins/
        let pluginsDir = Path.Combine(workspaceRoot, "plugins")
        if Directory.Exists pluginsDir then
            for source in AssemblySource.fromDirectory pluginsDir do
                sources.Add(source)

        load (sources |> Seq.toList)
