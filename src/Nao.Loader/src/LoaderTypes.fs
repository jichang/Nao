namespace Nao.Loader

open Nao.Agents
open Nao.Core
open Nao.Eval

/// Parsed agent definition loaded from JSON configuration.
/// References tools and sub-agents by name; resolved at build time.
type AgentDef =
    { /// Unique name identifying this agent definition
      Name: string
      /// Optional version identifier (e.g. "1.0"). None = unversioned; matches any requested version.
      Version: string option
      /// Human-readable description of the agent's purpose
      Description: string
      /// LLM provider name (e.g. "ollama", "openai", "anthropic")
      Provider: string
      /// Model identifier (e.g. "llama3", "gpt-4o")
      Model: string
      /// Structured prompt defining the agent's behavior
      Prompt: Prompt
      /// Tool names this agent can invoke (resolved from workspace)
      Tools: string list
      /// Sub-agent names for delegation (resolved from workspace)
      SubAgents: string list
      /// LLM completion options (temperature, max tokens, etc.)
      Options: CompletionOptions
      /// Maximum orchestration rounds before forcing a response
      MaxRounds: int
      /// When true, selecting this agent runs the turn as a background task (a spawned
      /// sub-session conversation) rather than inline; the turn returns a task token.
      IsAsync: bool
      /// Where this agent definition came from (populated by the loading source).
      Provenance: ToolProvenance option }

/// How a tool executes (the mechanism for running the tool)
type ToolExecutionDef =
    /// Execute a program/process. Works with any executable: bash, python, node, .exe, etc.
    | Process of command: string * args: string list
    /// Call an HTTP endpoint. The input is sent as the request body.
    | Http of url: string * method: string * headers: Map<string, string>
    /// Use a named executor registered at runtime (for custom protocols: gRPC, MCP, etc.)
    | Custom of executor: string * config: Map<string, string>

/// Describes a named runtime that a process-based tool needs in order to run.
/// For example, a TypeScript entrypoint declares runtime "deno" so it launches as
/// `deno run -A entry.ts <args>` instead of being executed directly.
type ToolRuntime =
    { /// Identifier used in tool/session config (e.g. "deno", "ts-node", "python").
      Name: string
      /// Launcher executable that wraps the tool command ("" = run the command directly).
      Command: string
      /// Leading arguments for the launcher (e.g. ["run"; "-A"] for deno).
      Args: string list
      /// Default container image used when this runtime is containerized.
      DefaultImage: string option }

/// Built-in runtimes plus resolution of runtime names to launcher commands.
module ToolRuntime =

    /// Run the command directly on the host (no wrapping).
    let native = { Name = "native"; Command = ""; Args = []; DefaultImage = None }

    let deno = { Name = "deno"; Command = "deno"; Args = ["run"; "-A"]; DefaultImage = Some "denoland/deno:latest" }
    let tsNode = { Name = "ts-node"; Command = "ts-node"; Args = []; DefaultImage = Some "node:latest" }
    let node = { Name = "node"; Command = "node"; Args = []; DefaultImage = Some "node:latest" }
    let bun = { Name = "bun"; Command = "bun"; Args = ["run"]; DefaultImage = Some "oven/bun:latest" }
    let python = { Name = "python"; Command = "python3"; Args = []; DefaultImage = Some "python:3" }
    let bash = { Name = "bash"; Command = "bash"; Args = []; DefaultImage = Some "bash:latest" }

    /// Known runtimes, keyed by name.
    let builtins = [ native; deno; tsNode; node; bun; python; bash ]

    let private byName =
        builtins |> List.map (fun r -> r.Name.ToLowerInvariant(), r) |> Map.ofList

    /// Resolve a runtime name to its definition. Unknown / empty names resolve to a
    /// runtime that launches the given command directly (i.e. the name is treated as
    /// the launcher executable itself), so arbitrary interpreters still work.
    let resolve (name: string) : ToolRuntime =
        if System.String.IsNullOrWhiteSpace name then native
        else
            let key = name.Trim().ToLowerInvariant()
            match Map.tryFind key byName with
            | Some r -> r
            | None -> { Name = key; Command = name.Trim(); Args = []; DefaultImage = None }

/// Session-level policy deciding how tool processes are launched, overlaying the
/// runtime each tool declares for itself.
type RuntimePolicy =
    /// Each tool uses its own declared runtime, executed on the host.
    | HostDefault
    /// Force every tool to launch through the named runtime on the host (e.g. "deno").
    | ForceRuntime of runtime: string
    /// Run every tool inside a container. When `image` is None the tool runtime's
    /// default image is used (falling back to a base image).
    | Containerized of image: string option

/// Parsing of the session runtime-mode string into a RuntimePolicy.
module RuntimePolicy =

    /// Parse a runtime-mode string:
    ///  - "" / "native" / "host"  -> HostDefault
    ///  - "docker" / "container"   -> Containerized None
    ///  - "docker:<image>"         -> Containerized (Some image)
    ///  - any other value          -> ForceRuntime value (e.g. "deno")
    let parse (mode: string) : RuntimePolicy =
        if System.String.IsNullOrWhiteSpace mode then HostDefault
        else
            let m = mode.Trim()
            let lower = m.ToLowerInvariant()
            if lower = "native" || lower = "host" then HostDefault
            elif lower = "docker" || lower = "container" then Containerized None
            elif lower.StartsWith "docker:" then Containerized (Some (m.Substring("docker:".Length)))
            elif lower.StartsWith "container:" then Containerized (Some (m.Substring("container:".Length)))
            else ForceRuntime m

/// The two kinds of tool a definition can describe.
type ToolKind =
    /// Prompt-defined: the tool body is an LLM prompt. Invoking the tool completes the
    /// prompt against the configured provider (optionally enriched with conversation
    /// context). Always synchronous — there is no external process to background.
    | PromptTool of prompt: Prompt
    /// Executable: the tool runs external code through a process, HTTP endpoint, or a
    /// custom executor. May run synchronously (inline) or asynchronously (spawned as a
    /// background task) when `isAsync` is set.
    | ExecutableTool of execution: ToolExecutionDef * runtime: string * isAsync: bool

/// Parsed tool definition loaded from JSON configuration.
/// A tool is either prompt-defined (LLM-backed, sync) or executable (process/HTTP/custom,
/// sync or async) — see `ToolKind`.
type ToolDef =
    { /// Unique name identifying this tool
      Name: string
      /// Optional version identifier (e.g. "1.0"). None = unversioned; matches any requested version.
      Version: string option
      /// Human-readable description shown to agents
      Description: string
      /// Whether this tool is prompt-defined or executable, and how it executes.
      Kind: ToolKind
      /// Content type of the tool's output (e.g. "text/plain", "application/json")
      OutputContentType: string
      /// Optional execution definition for verifying output (executable tools only)
      VerifyExecution: ToolExecutionDef option
      /// Optional execution definition for reverting changes (executable tools only)
      RevertExecution: ToolExecutionDef option
      /// Where this tool definition came from (populated by the loading source).
      Provenance: ToolProvenance option }

/// Accessors over a tool definition's kind.
module ToolDef =

    /// The executable runtime the tool declares ("" / native for prompt tools).
    let runtime (def: ToolDef) : string =
        match def.Kind with
        | ExecutableTool (_, rt, _) -> rt
        | PromptTool _ -> ""

    /// Whether the tool runs asynchronously (false for prompt tools).
    let isAsync (def: ToolDef) : bool =
        match def.Kind with
        | ExecutableTool (_, _, async) -> async
        | PromptTool _ -> false

/// Reference to an evaluator used in an eval suite
type EvaluatorRef =
    { /// Evaluator type (e.g. "llm-judge", "pattern", "keyword")
      Type: string
      /// Evaluation criteria description (for LLM judge)
      Criteria: string
      /// Score scale (e.g. "1-5", "pass/fail")
      Scale: string
      /// Regex pattern for pattern-based evaluation
      Pattern: string
      /// Required keywords for keyword-based evaluation
      Keywords: string list }

/// Parsed eval suite definition grouping test cases with an evaluator
type EvalSuiteDef =
    { /// Unique name for this evaluation suite
      Name: string
      /// Description of what this suite evaluates
      Description: string
      /// Agent name to evaluate (resolved from workspace)
      Agent: string
      /// Evaluator configuration for scoring results
      Evaluator: EvaluatorRef
      /// Test cases to run against the agent
      Cases: EvalCase list }

/// Parsed constitution rule definition loaded from JSON
type ConstitutionRuleDef =
    { /// Unique rule identifier
      Id: string
      /// Human-readable description
      Description: string
      /// Category: "Safety", "Privacy", "Behavioral", "Format", or "Domain:<name>"
      Category: string
      /// Priority (higher = more important)
      Priority: int
      /// Whether this is a hard constraint (blocks output) vs soft (warning)
      IsHardConstraint: bool
      /// Regex pattern for the check (content matching this pattern violates the rule)
      Pattern: string }

/// Parsed constitution definition (collection of rules)
type ConstitutionDef =
    { /// Constitution name
      Name: string
      /// Version identifier
      Version: string
      /// Rules in this constitution
      Rules: ConstitutionRuleDef list }

/// Errors that can occur during definition loading
type LoadError =
    /// File path does not exist
    | FileNotFound of path: string
    /// JSON or configuration parsing failed
    | ParseError of source: string * message: string
    /// Definition is syntactically valid but semantically invalid
    | ValidationError of source: string * message: string
    /// A referenced agent, tool, or evaluator was not found
    | MissingReference of kind: string * name: string
    /// .NET assembly could not be loaded or reflected
    | AssemblyLoadError of path: string * message: string

/// Functions for formatting load errors
module LoadError =
    /// Format a LoadError as a human-readable string
    let format error =
        match error with
        | FileNotFound path -> sprintf "File not found: %s" path
        | ParseError (source, msg) -> sprintf "Parse error in %s: %s" source msg
        | ValidationError (source, msg) -> sprintf "Validation error in %s: %s" source msg
        | MissingReference (kind, name) -> sprintf "Missing %s reference: '%s'" kind name
        | AssemblyLoadError (path, msg) -> sprintf "Assembly load error in %s: %s" path msg

/// Result type for load operations
type LoadResult<'T> = Result<'T, LoadError>

/// The result of loading definitions from a source
type LoadedDefinitions =
    { Agents: LoadResult<AgentDef> list
      Tools: LoadResult<ToolDef> list
      EvalSuites: LoadResult<EvalSuiteDef> list
      /// Governance constitution definitions
      Constitutions: LoadResult<ConstitutionDef> list
      /// Pre-built agents discovered directly (e.g. from assemblies)
      BuiltAgents: IAgent list
      /// Pre-built tools discovered directly (e.g. from assemblies)
      BuiltTools: Tool list
      /// Pre-built evaluators discovered directly (e.g. from assemblies)
      BuiltEvaluators: IEvaluator list }

    static member Empty =
        { Agents = []
          Tools = []
          EvalSuites = []
          Constitutions = []
          BuiltAgents = []
          BuiltTools = []
          BuiltEvaluators = [] }

/// A source that can provide agent/tool/eval definitions
type IDefinitionSource =
    /// A human-readable name for this source (e.g. "json:/path/.nao", "assembly:MyPlugin.dll")
    abstract member Name: string
    /// Load all definitions from this source
    abstract member Load: unit -> LoadedDefinitions
