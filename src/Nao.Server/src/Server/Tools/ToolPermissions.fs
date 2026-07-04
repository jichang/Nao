namespace Nao.Assistant

open System
open System.IO
open System.Threading.Tasks
open Nao.Agents
open Nao.Agents

/// Resource-permission enforcement for the built-in tools. Tools are the single seam through
/// which agents touch the outside world, so this is where deny-by-default web/file access is
/// enforced. Built-in tools are classified into a `ResourceAccess` (we know their input
/// formats); the request is evaluated against the user's global allowlist (settings) plus the
/// cross-session store rules. A denied call returns a structured error instead of executing.
module ToolPermissions =

    // Settings are read through a tiny TTL cache so we don't hit disk on every call.
    let mutable private cachedAt = DateTime.MinValue
    let mutable private cached = PermissionSettings.Default

    let private settings () =
        if (DateTime.UtcNow - cachedAt).TotalSeconds > 2.0 then
            cached <- (AppSettingsStore.load ()).Permissions
            cachedAt <- DateTime.UtcNow
        cached

    /// The user's global allowlist (from settings) expressed as Allow rules.
    let private globalAllowRules (s: PermissionSettings) : PermissionRule list =
        let mk kind pattern : PermissionRule =
            { Id = ""
              Kind = kind
              Pattern = pattern
              Operations = []
              Decision = PermissionDecision.Allow
              Scope = RuleScope.Global
              CreatedAt = DateTimeOffset.UtcNow }
        (s.AllowedWebDomains |> List.map (mk ResourceKind.Web))
        @ (s.AllowedFilePaths |> List.map (mk ResourceKind.File))


    /// Map a known built-in tool invocation to the resource it would touch. Unknown
    /// tools return None and are not guarded (their resource semantics are opaque). Tool
    /// inputs are JSON objects, so we read the relevant field from the parsed args.
    let classify (ctx: ToolContext) (name: string) (input: string) : ResourceAccess option =
        let a = parseArgs input
        match name with
        | "web_fetch" -> Some(ResourceAccess.Web("fetch", (a.StringOrRaw "url").Trim()))
        | "http_request" ->
            let methodStr = (a.StringOr("method", "GET")).Trim().ToUpperInvariant()
            Some(ResourceAccess.Web(methodStr, (a.StringOrRaw "url").Trim()))
        | "read_file" -> Some(ResourceAccess.File("read", resolvePath ctx (a.StringOrRaw "path")))
        | "write_file" -> Some(ResourceAccess.File("write", resolvePath ctx (a.StringOrRaw "path")))
        | "create_folder" -> Some(ResourceAccess.File("write", resolvePath ctx (a.StringOrRaw "path")))
        | "delete" -> Some(ResourceAccess.File("delete", resolvePath ctx (a.StringOrRaw "path")))
        | "list_folder" ->
            let rel = a.StringOrRaw "path"
            let path = if String.IsNullOrWhiteSpace rel then currentWorkDir ctx else resolvePath ctx rel
            Some(ResourceAccess.File("read", path))
        | "search_files" ->
            let sub = a.StringOr("path", "")
            let path = if String.IsNullOrWhiteSpace sub then currentWorkDir ctx else resolvePath ctx sub
            Some(ResourceAccess.File("read", path))
        | "find_files" -> Some(ResourceAccess.File("read", currentWorkDir ctx))
        | _ -> None

    /// Map a built-in tool invocation to EVERY resource it would touch. Most tools touch a
    /// single resource (`classify`). Tools that need to authorize specific resources they only
    /// discover at runtime (e.g. `convert_document`'s exact source and target paths) request
    /// those themselves via `requestConfirmedAsync` and so are NOT statically classified here.
    let classifyAll (ctx: ToolContext) (name: string) (input: string) : ResourceAccess list =
        classify ctx name input |> Option.toList

    let private isUnder (root: string) (path: string) =
        let norm (s: string) = Path.GetFullPath(s).Replace('\\', '/').TrimEnd('/')
        let r = norm root
        let p = norm path
        p = r || p.StartsWith(r + "/", StringComparison.Ordinal)

    /// Evaluate the static rules for an access request. The "default when nothing matches" is
    /// `Ask` for resources outside the allowlist/sandbox, so the async layer can prompt the
    /// user live; explicit deny rules still short-circuit to `Deny` and in-sandbox file access
    /// stays `Allow` unless `forceConfirm` is set (the caller wants to confirm this specific
    /// resource even though it is inside the sandbox).
    let decide (sessionKey: string) (forceConfirm: bool) (access: ResourceAccess) : PermissionDecision =
        let s = settings ()
        if not s.Enabled then
            PermissionDecision.Allow
        else
            // Settings allowlist + cross-session (global) store rules. Per-session grants
            // are owned by the SessionGrain and consulted there before we are ever asked.
            let rules = globalAllowRules s @ PermissionStore.globalRules ()
            match access with
            | ResourceAccess.File(_, path) ->
                let dft =
                    if forceConfirm then PermissionDecision.Ask
                    elif isUnder (workDirForKey sessionKey) path then PermissionDecision.Allow
                    else PermissionDecision.Ask
                ResourcePermission.evaluateWith dft rules access
            | ResourceAccess.Web _ -> ResourcePermission.evaluateWith PermissionDecision.Ask rules access
            | ResourceAccess.ToolCall _ -> ResourcePermission.evaluateWith PermissionDecision.Allow rules access

    /// Human-readable reason shown in the approval prompt.
    let private reasonFor (access: ResourceAccess) : string =
        match access with
        | ResourceAccess.Web(op, url) -> sprintf "The assistant wants to make a %s request to %s." (op.ToUpperInvariant()) url
        | ResourceAccess.File(op, path) -> sprintf "The assistant wants %s access to %s." op path
        | ResourceAccess.ToolCall name -> sprintf "The assistant wants to run the tool '%s'." name

    /// Resolve a permission request against the static rules (settings, sandbox, persisted
    /// grants) and, when the outcome is `Ask` (nothing pre-approved or pre-denied), prompt
    /// the user live over the WebSocket and await their answer. Returns the decision plus
    /// whether the user asked to remember the grant for the session. This is registered as
    /// the process-wide `PermissionGate.Prompt` so the session grain (which cannot
    /// reference the server) can resolve its tools' permission requests through it.
    let promptOutcome (sessionKey: string) (access: ResourceAccess) (reason: string) (forceConfirm: bool) : Task<PermissionOutcome> =
        task {
            match decide sessionKey forceConfirm access with
            | PermissionDecision.Allow -> return { Decision = PermissionDecision.Allow; RememberForSession = false }
            | PermissionDecision.Deny -> return { Decision = PermissionDecision.Deny; RememberForSession = false }
            | PermissionDecision.Ask ->
                let r = if String.IsNullOrWhiteSpace reason then reasonFor access else reason
                return! PermissionBroker.requestAsync sessionKey access r
        }

    /// Structured refusal handed back to the model when access is denied. It names the
    /// resource and how the user can grant access, so the agent can relay it usefully.
    let private denyResult (access: ResourceAccess) : string =
        let hint =
            match access with
            | ResourceAccess.Web(_, url) ->
                let host = ResourcePermission.hostOf url |> Option.defaultValue url
                sprintf "Add '%s' to the allowed web domains in Settings → Permissions to permit this." host
            | ResourceAccess.File(op, _) ->
                sprintf "Add this path to the allowed file paths in Settings → Permissions to permit %s access." op
            | ResourceAccess.ToolCall _ -> "This tool is blocked by a permission rule."
        PermissionDenied.format access (Some hint)

    /// Wrap a tool so every invocation is permission-checked before it runs. The check is
    /// routed through the tool's runtime context, which the session grain backs with the
    /// session's own granted permissions plus a live prompt when needed.
    let guard (tool: Tool) : Tool =
        { tool with
            Execute =
                fun ctx input ->
                    task {
                        let accesses = classifyAll ctx tool.Name input
                        let mutable denied = None
                        for access in accesses do
                            if Option.isNone denied then
                                let! ok = ctx.RequestPermission access (reasonFor access) false
                                if not ok then denied <- Some access
                        match denied with
                        | Some access -> return denyResult access
                        | None -> return! tool.Execute ctx input } }

    // Back the runtime's per-session permission context with the real decision logic
    // (settings, persisted grants, interactive prompt) so tools running inside a session
    // grain can resolve permission requests even though that layer cannot reference us.
    do PermissionGate.Prompt <- Some promptOutcome

    /// Dynamic, mid-execution permission check a tool can call when it discovers (from its
    /// own intermediate results) that it needs to touch a specific resource. Prompts the user
    /// live over the WebSocket when the access isn't already allowed/denied by a rule, and
    /// returns true only if access is granted. Use this from custom tools that decide what
    /// they need at runtime rather than from a fixed input shape.
    let requestPermissionAsync (ctx: ToolContext) (access: ResourceAccess) (reason: string) : Task<bool> =
        ctx.RequestPermission access reason false

    /// Like `requestPermissionAsync` but forces an interactive prompt even for paths inside
    /// the workspace sandbox (which are otherwise auto-allowed). Sensitive tools use this to
    /// confirm the SPECIFIC source/target resources they touch. A persisted or session grant
    /// still suppresses repeat prompts, and the global master switch (settings) still applies.
    let requestConfirmedAsync (ctx: ToolContext) (access: ResourceAccess) (reason: string) : Task<bool> =
        ctx.RequestPermission access reason true
