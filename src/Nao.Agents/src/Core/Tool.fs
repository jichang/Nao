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

/// Runtime context handed to a tool's Execute (and to the orchestrator) so it can request
/// approval for sensitive resource access dynamically — based on its own input or
/// intermediate results — and locate the session's file folder.
/// The runtime builds one per session/turn and threads it explicitly; library and test code
/// can use `ToolContext.allowAll`.
/// Structured data published by a tool for persistence and frontend rendering.
type ToolResultData = { Kind: string; ContentType: string; Payload: string }

type ToolContext = { SessionKey: string; FilesKey: string; TurnId: string; RequestPermission: ResourceAccess -> string -> bool -> Task<bool>; PublishData: ToolResultData -> Task }

/// Helpers for the tool execution context.
[<RequireQualifiedAccess>]
module ToolContext =
        /// Permissive, unscoped context used when no permission/session system is wired (tests,
        /// library use).
    let allowAll: ToolContext =
        { SessionKey = ""; FilesKey = ""; TurnId = ""; RequestPermission = (fun _ _ _ -> Task.FromResult true); PublishData = (fun _ -> Task.CompletedTask) }

[<RequireQualifiedAccess>]
module ToolVersion =
    /// Version assigned by the simple tool constructors.
    let Default = "1.0"

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

/// Describes a tool's input and output contract.
type ToolSignature =
        { /// Schema describing the named parameters this tool accepts in its JSON input object.
            /// Empty means the tool takes no parameters (or a single free-form string input).
            Input: ToolParameter list
            /// Declared content type of the tool's output (framework carries, does not interpret).
            Output: ContentMeta }

[<RequireQualifiedAccess>]
module ToolSignature =
    let Text = { Input = []; Output = ContentMeta.Text }

/// A tool that an agent can invoke to perform actions or retrieve information.
/// Supports optional capabilities: content-type declaration, prepare, verify, and revert.
type Tool =
    {
        /// Unique name used by the agent to reference this tool
        Name: string
        /// Human-readable description shown to the LLM so it knows when to use the tool
        Description: string
        /// Selection priority used as a tie-breaker after tool suitability
        Priority: int
        /// Required version identifier (e.g. "1.0").
        Version: string
        /// Input/output contract for this tool.
        Signature: ToolSignature
        /// Execute the tool with its context (for dynamic permission requests) and a string
        /// input, returning the result.
        Execute: ToolContext -> string -> Task<string>
        /// Static resource permissions this tool declares it needs. The runtime requests these
        /// through the context before each execution; a denied one short-circuits the call.
        Permissions: ResourceAccess list
        /// Validate and normalize raw model-generated input before invocation. The prepared
        /// value is used for execution, verification, tracing, and duplicate detection.
        Prepare: (string -> Result<string, string>) option
        /// Verify the output is correct given the input. Returns Ok or Error with reason.
        Verify: (string -> string -> Task<Result<unit, string>>) option
        /// Revert/undo changes the tool has made to external resources.
        Revert: (RevertContext -> Task<Result<unit, string>>) option
    }

    /// Create a simple tool with just name, description, and execute (text/plain, no revert).
    /// The execute function ignores the context; use the 4-argument overload for tools that
    /// request permission dynamically or declare static permissions.
    static member Create(name: string, description: string, execute: string -> Task<string>) =
        { Name = name
          Description = description
          Priority = 0
          Version = ToolVersion.Default
          Signature = ToolSignature.Text
          Execute = (fun _ctx input -> execute input)
          Permissions = []
          Prepare = None
          Verify = None
          Revert = None }

    /// Create a tool that receives its execution context (to request permission dynamically)
    /// and declares the static permissions it needs (auto-requested before each run).
    static member Create(name: string, description: string, permissions: ResourceAccess list, execute: ToolContext -> string -> Task<string>) =
        { Name = name
          Description = description
          Priority = 0
          Version = ToolVersion.Default
          Signature = ToolSignature.Text
          Execute = execute
          Permissions = permissions
          Prepare = None
          Verify = None
          Revert = None }

    /// Validate and normalize model-generated input without invoking the tool.
    member this.PrepareInput(input: string) : Result<string, string> =
        try
            match this.Prepare with
            | Some prepare -> prepare input
            | None -> Ok input
        with ex ->
            Error(sprintf "Input preparation raised an exception: %s" ex.Message)

    /// Invoke a tool with input that has already passed preparation.
    member this.InvokePreparedAsync(ctx: ToolContext, preparedInput: string) : Task<string> =
        task {
            let mutable denied = None
            for access in this.Permissions do
                if Option.isNone denied then
                    let! ok = ctx.RequestPermission access (sprintf "Tool '%s' requires this access." this.Name) false
                    if not ok then denied <- Some access
            match denied with
            | Some access ->
                return PermissionDenied.format access None
            | None -> return! this.Execute ctx preparedInput
        }

    /// Run the tool: request each declared static permission through the context first, then
    /// execute. A denied declared permission short-circuits with a refusal message instead of
    /// running the tool.
    member this.InvokeAsync(ctx: ToolContext, input: string) : Task<string> =
        task {
            match this.PrepareInput input with
            | Ok preparedInput -> return! this.InvokePreparedAsync(ctx, preparedInput)
            | Error reason -> return invalidArg "input" (sprintf "Tool '%s' input preparation failed: %s" this.Name reason)
        }

    /// Whether this tool declares revert capability
    member this.CanRevert = this.Revert.IsSome

    /// Whether this tool declares verify capability
    member this.CanVerify = this.Verify.IsSome

    /// Whether this tool declares input preparation capability
    member this.CanPrepare = this.Prepare.IsSome

/// Helpers for version-qualified references of the form "name@version".
[<RequireQualifiedAccess>]
module VersionRef =

    /// Parse a required version-qualified reference into its name and version.
    let parse (reference: string) : string * string =
        if String.IsNullOrWhiteSpace reference then
            invalidArg "reference" "A tool reference must use the form name@version."
        let separator = reference.IndexOf('@')
        if separator <= 0 || separator = reference.Length - 1 then
            invalidArg "reference" "A tool reference must use the form name@version."
        reference.Substring(0, separator), reference.Substring(separator + 1)
