namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Agents

/// Context provided to a tool's Revert function so it can undo its effects
type RevertContext =
    { /// The input that was given to the tool
      Input: string
      /// The output the tool produced
      Output: string
      /// When the tool was executed
      ExecutedAt: DateTimeOffset
      /// Additional metadata from execution
      Metadata: Map<string, string> }

/// Identifies where a tool originated, so feedback-driven adjustments can target
/// the correct source (a JSON file to re-version, or a compiled assembly to patch).
type ToolProvenance =
    { /// Source kind: "json", "assembly", or "code".
      Kind: string
      /// Path to the originating artifact (a JSON file or a DLL), when applicable.
      Location: string option
      /// Optional member identifier within the artifact (e.g. an assembly type/property).
      Member: string option }

/// Helpers for building tool provenance values.
[<RequireQualifiedAccess>]
module ToolProvenance =
    /// Provenance for a tool loaded from a JSON definition file.
    let json (filePath: string) : ToolProvenance =
        { Kind = "json"; Location = Some filePath; Member = None }

    /// Provenance for a tool discovered in a compiled assembly.
    let assembly (dllPath: string) (memberName: string) : ToolProvenance =
        { Kind = "assembly"; Location = Some dllPath; Member = Some memberName }

    /// Provenance for a tool registered directly from code.
    let code (sourceName: string) : ToolProvenance =
        { Kind = "code"; Location = None; Member = Some sourceName }

/// Runtime context handed to a tool's Execute (and to the orchestrator) so it can request
/// approval for sensitive resource access dynamically — based on its own input or
/// intermediate results — locate the session's file folder, and launch background work.
/// The runtime builds one per session/turn and threads it explicitly; library and test code
/// can use `ToolContext.allowAll`.
type ToolContext =
    { /// The session this execution belongs to ("userId/sessionId"); "" when unscoped.
      SessionKey: string
      /// Session key whose file folder backs file operations in this context. Usually the
      /// same as SessionKey, but a task sub-session points this at its parent session so the
      /// user's attachments and the files the task generates share one folder.
      FilesKey: string
      /// Names of agents flagged async. When the orchestrator delegates to one of these, it
      /// spawns a background task (a sub-session) instead of running it inline.
      AsyncAgents: Set<string>
      /// Id of the turn currently being processed ("" when none).
      TurnId: string
      /// Launch a background task owned by this session, returning its task id. The default
      /// is a no-op signalling "no async task host available" by returning an empty id.
      SpawnTask: SessionExecution.TaskSpec -> Task<string>
      /// Request approval to access a resource, with a human-readable reason. The final
      /// argument forces an interactive prompt even for resources that would otherwise be
      /// auto-allowed (e.g. paths inside the workspace sandbox); already-granted rules still
      /// suppress the prompt. Returns true when allowed. The runtime answers from
      /// already-granted session/global rules or by prompting the user live.
      RequestPermission: ResourceAccess -> string -> bool -> Task<bool> }

/// Helpers for the tool execution context.
[<RequireQualifiedAccess>]
module ToolContext =
    /// Permissive, unscoped context used when no permission/session system is wired (tests,
    /// library use). SpawnTask returns an empty id, meaning "no async task host available".
    let allowAll: ToolContext =
        { SessionKey = ""
          FilesKey = ""
          AsyncAgents = Set.empty
          TurnId = ""
          SpawnTask = fun _ -> Task.FromResult ""
          RequestPermission = fun _ _ _ -> Task.FromResult true }

/// The result of resolving a permission request: the decision plus whether the user asked to
/// remember the grant for the rest of the session (so the session can record it in its own
/// state rather than re-prompting).
type PermissionOutcome =
    { Decision: PermissionDecision
      RememberForSession: bool }

/// A process-wide hook the server registers so the runtime layer (which cannot reference the
/// server) can resolve permission requests against the real decision logic — settings,
/// persisted grants, and the interactive prompt. When unset, access is allowed (no
/// permission system present, e.g. in tests).
[<RequireQualifiedAccess>]
module PermissionGate =
    /// (sessionKey, access, reason, forceConfirm) -> outcome. Set by the server at startup.
    let mutable Prompt: (string -> ResourceAccess -> string -> bool -> Task<PermissionOutcome>) option = None

/// Canonical, structured refusal handed back to the model whenever a resource access is
/// denied. Centralizing it here keeps every enforcement point (a tool's own declared
/// permissions, the runtime context, the server guard) emitting the same machine-readable
/// shape — `{ error, kind, resource, message, hint? }` — so agents can relay denials
/// consistently. The optional hint lets the server add UI-specific guidance.
[<RequireQualifiedAccess>]
module PermissionDenied =
    let private kindAndResource (access: ResourceAccess) : string * string =
        match access with
        | ResourceAccess.Web(_, url) -> "web", url
        | ResourceAccess.File(_, path) -> "file", path
        | ResourceAccess.ToolCall name -> "tool", name

    /// Build the structured refusal JSON for a denied access, optionally with a remediation
    /// hint (e.g. how to grant the access in Settings).
    let format (access: ResourceAccess) (hint: string option) : string =
        let kind, resource = kindAndResource access
        let message = sprintf "Permission denied: access to %s was not granted." resource
        match hint with
        | Some h ->
            System.Text.Json.JsonSerializer.Serialize
                {| error = "permission_denied"; kind = kind; resource = resource; message = message; hint = h |}
        | None ->
            System.Text.Json.JsonSerializer.Serialize
                {| error = "permission_denied"; kind = kind; resource = resource; message = message |}

/// Describes a single parameter a tool accepts in its JSON input object.
type ToolParameter =
    { /// Parameter name (the JSON object key)
      Name: string
      /// Human-readable description of the parameter
      Description: string
      /// Type hint (e.g. "string", "int", "object", "array")
      Type: string
      /// Whether this parameter is required
      Required: bool
      /// Default value applied when the parameter is omitted, if any
      Default: string option
      /// Example values for documentation / few-shot prompting
      Examples: string list }

/// A tool that an agent can invoke to perform actions or retrieve information.
/// Supports optional capabilities: content-type declaration, verify, and revert.
type Tool =
    { /// Unique name used by the agent to reference this tool
      Name: string
      /// Human-readable description shown to the LLM so it knows when to use the tool
      Description: string
      /// Optional version identifier (e.g. "1.0"). None = unversioned; matches any requested version.
      Version: string option
      /// Schema describing the named parameters this tool accepts in its JSON input object.
      /// Empty means the tool takes no parameters (or a single free-form string input).
      Schema: ToolParameter list
      /// Execute the tool with its context (for dynamic permission requests) and a string
      /// input, returning the result.
      Execute: ToolContext -> string -> Task<string>
      /// Static resource permissions this tool declares it needs. The runtime requests these
      /// through the context before each execution; a denied one short-circuits the call.
      Permissions: ResourceAccess list
      /// Declared content type of the tool's output (framework carries, does not interpret)
      OutputContentType: ContentMeta
      /// Verify the output is correct given the input. Returns Ok or Error with reason.
      Verify: (string -> string -> Task<Result<unit, string>>) option
      /// Revert/undo changes the tool has made to external resources.
      Revert: (RevertContext -> Task<Result<unit, string>>) option
      /// Where this tool came from (used by the feedback/adjust system to target patches).
      Provenance: ToolProvenance option }

    /// Create a simple tool with just name, description, and execute (text/plain, no revert).
    /// The execute function ignores the context; use the 4-argument overload for tools that
    /// request permission dynamically or declare static permissions.
    static member Create(name: string, description: string, execute: string -> Task<string>) =
        { Name = name
          Description = description
          Version = None
          Schema = []
          Execute = (fun _ctx input -> execute input)
          Permissions = []
          OutputContentType = ContentMeta.Text
          Verify = None
          Revert = None
          Provenance = None }

    /// Create a tool that receives its execution context (to request permission dynamically)
    /// and declares the static permissions it needs (auto-requested before each run).
    static member Create(name: string, description: string, permissions: ResourceAccess list, execute: ToolContext -> string -> Task<string>) =
        { Name = name
          Description = description
          Version = None
          Schema = []
          Execute = execute
          Permissions = permissions
          OutputContentType = ContentMeta.Text
          Verify = None
          Revert = None
          Provenance = None }

    /// Run the tool: request each declared static permission through the context first, then
    /// execute. A denied declared permission short-circuits with a refusal message instead of
    /// running the tool.
    member this.InvokeAsync(ctx: ToolContext, input: string) : Task<string> =
        task {
            let mutable denied = None
            for access in this.Permissions do
                if Option.isNone denied then
                    let! ok = ctx.RequestPermission access (sprintf "Tool '%s' requires this access." this.Name) false
                    if not ok then denied <- Some access
            match denied with
            | Some access ->
                return PermissionDenied.format access None
            | None -> return! this.Execute ctx input
        }

    /// Whether this tool declares revert capability
    member this.CanRevert = this.Revert.IsSome

    /// Whether this tool declares verify capability
    member this.CanVerify = this.Verify.IsSome

/// Helpers for version-qualified references of the form "name@version".
/// Used to look up a specific version of a tool or agent while remaining
/// backward compatible with plain, unversioned "name" references.
[<RequireQualifiedAccess>]
module VersionRef =

    /// Parse a possibly version-qualified reference "name@version" into
    /// its (name, version option) parts. "name" => (name, None).
    let parse (reference: string) : string * string option =
        if String.IsNullOrEmpty reference then ("", None)
        else
            let idx = reference.IndexOf('@')
            if idx < 0 then (reference, None)
            else
                let name = reference.Substring(0, idx)
                let ver = reference.Substring(idx + 1)
                (name, (if String.IsNullOrEmpty ver then None else Some ver))

    /// Whether an actual version satisfies a requested version.
    /// A request of None matches any actual version (name-only lookup).
    let matches (requested: string option) (actual: string option) : bool =
        match requested with
        | None -> true
        | Some _ -> requested = actual
