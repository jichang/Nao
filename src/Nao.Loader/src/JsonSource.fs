namespace Nao.Loader

open System.IO
open Nao.Agents

/// Loads definitions from a directory of JSON files.
///
/// Expected folder structure:
///   <rootDir>/
///   ├── agents/
///   │   ├── my-agent.json
///   │   └── research-agent.json
///   ├── tools/
///   │   ├── web-search.json
///   │   └── calculator.json
///   └── evals/
///       ├── qa-suite.json
///       └── coding-suite.json
type JsonSource(rootDir: string) =

    let parseFile (filePath: string) (reader: string -> 'T) : LoadResult<'T> =
        if not (File.Exists filePath) then
            Result.Error (FileNotFound filePath)
        else
            try
                Result.Ok (reader (File.ReadAllText filePath))
            with ex ->
                Result.Error (ParseError (filePath, ex.Message))

    let loadFromDir (subDir: string) (reader: string -> 'T) : LoadResult<'T> list =
        let dir = Path.Combine(rootDir, subDir)
        if Directory.Exists dir then
            Directory.GetFiles(dir, "*.json")
            |> Array.map (fun f -> parseFile f reader)
            |> Array.toList
        else []

    /// Load tool definitions, stamping each with provenance pointing at its source file.
    let loadToolDefs () : LoadResult<ToolDef> list =
        let dir = Path.Combine(rootDir, "tools")
        if Directory.Exists dir then
            Directory.GetFiles(dir, "*.json")
            |> Array.map (fun f ->
                match parseFile f JsonRead.toolDef with
                | Result.Ok def -> Result.Ok { def with Provenance = Some (ToolProvenance.json f) }
                | other -> other)
            |> Array.toList
        else []

    /// Load agent definitions, stamping each with provenance pointing at its source file.
    let loadAgentDefs () : LoadResult<AgentDef> list =
        let dir = Path.Combine(rootDir, "agents")
        if Directory.Exists dir then
            Directory.GetFiles(dir, "*.json")
            |> Array.map (fun f ->
                match parseFile f JsonRead.agentDef with
                | Result.Ok def -> Result.Ok { def with Provenance = Some (ToolProvenance.json f) }
                | other -> other)
            |> Array.toList
        else []

    interface IDefinitionSource with
        member _.Name = sprintf "json:%s" rootDir

        member _.Load() =
            { Agents = loadAgentDefs ()
              Tools = loadToolDefs ()
              EvalSuites = loadFromDir "evals" JsonRead.evalSuiteDef
              Constitutions = loadFromDir "governance" JsonRead.constitutionDef
              BuiltAgents = []
              BuiltTools = []
              BuiltEvaluators = [] }

module JsonSource =

    /// Create a JSON source from a .nao/ directory within a workspace root
    let fromWorkspace (workspaceRoot: string) =
        let naoDir = Path.Combine(workspaceRoot, ".nao")
        JsonSource(naoDir) :> IDefinitionSource

    /// Create a JSON source from an explicit directory
    let fromDirectory (dir: string) =
        JsonSource(dir) :> IDefinitionSource
