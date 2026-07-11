namespace Nao.Agents

open System

/// A sensitive action a tool or agent wants to perform, together with the specific
/// resource it targets. The permission system decides whether to allow it. This is the
/// resource-level companion to the capability-level `Permission` model: where `Permission`
/// asks "may this agent use tool X?", `ResourceAccess` asks "may this run touch THIS path
/// or THIS url?".
[<RequireQualifiedAccess>]
type ResourceAccess =
    /// Filesystem access at an absolute path. Operation is "read", "write", "delete", "list".
    | File of operation: string * path: string
    /// Network access to a URL. Operation is the HTTP method ("GET"/"POST"/…) or "fetch".
    | Web of operation: string * url: string
    /// Invoke a named tool whose resource semantics the guard cannot introspect.
    | ToolCall of toolName: string

/// The outcome of evaluating an access request.
[<RequireQualifiedAccess>]
type PermissionDecision =
    /// Permitted — proceed.
    | Allow
    /// Refused — block the action.
    | Deny
    /// Needs interactive user approval. Reserved for the interactive-prompt phase; until
    /// that channel exists, callers treat `Ask` as `Deny`.
    | Ask

/// How broadly a granted rule applies.
[<RequireQualifiedAccess>]
type RuleScope =
    /// Applies only within one session (the grain key "userId/sessionId"). This is the
    /// default when a user grants access in response to a single request.
    | Session of sessionKey: string
    /// Applies to every session — the user explicitly opted to remember the grant globally.
    | Global

/// The class of resource a rule matches.
[<RequireQualifiedAccess>]
type ResourceKind =
    | File
    | Web
    | Tool

/// A single allow/deny rule the user granted.
type PermissionRule =
    { /// Stable identifier (used to revoke).
      Id: string
      Kind: ResourceKind
      /// Match pattern.
      ///  • File: an absolute path prefix — matches the path itself and anything under it
      ///    (e.g. "/home/me/project" matches "/home/me/project/sub/a.txt"). Globs allowed.
      ///  • Web: a host or host suffix — "example.com" matches "example.com" and any
      ///    subdomain "*.example.com". A bare "*" matches everything. Globs allowed.
      ///  • Tool: the tool name, a glob, or "*".
      Pattern: string
      /// Operations this rule covers, lowercased (e.g. ["read"; "write"]). Empty = any.
      Operations: string list
      Decision: PermissionDecision
      Scope: RuleScope
      CreatedAt: DateTimeOffset }

/// Pure evaluation of resource-access requests against a set of granted rules. No IO — the
/// persistence and enforcement layers live in the server; this module is the testable core.
[<RequireQualifiedAccess>]
module ResourcePermission =

    /// Match a glob pattern ('*' = any run of chars, '?' = exactly one) against text,
    /// case-insensitively. Used for both path and host patterns.
    let glob (pattern: string) (text: string) : bool =
        let p = pattern
        let t = text
        let pl = p.Length
        let tl = t.Length
        // Classic two-row dynamic-programming wildcard match.
        let dp = Array2D.create (pl + 1) (tl + 1) false
        dp.[0, 0] <- true
        for i in 1..pl do
            if p.[i - 1] = '*' then dp.[i, 0] <- dp.[i - 1, 0]
        for i in 1..pl do
            for j in 1..tl do
                match p.[i - 1] with
                | '*' -> dp.[i, j] <- dp.[i - 1, j] || dp.[i, j - 1]
                | '?' -> dp.[i, j] <- dp.[i - 1, j - 1]
                | c -> dp.[i, j] <- dp.[i - 1, j - 1] && (Char.ToLowerInvariant c = Char.ToLowerInvariant t.[j - 1])
        dp.[pl, tl]

    let private normHost (h: string) =
        h.Trim().TrimEnd('.').ToLowerInvariant()

    /// Best-effort extraction of the host from a URL string. Accepts both fully-qualified
    /// URLs ("https://a.example.com/x") and bare host forms ("a.example.com/x").
    let hostOf (url: string) : string option =
        let raw = url.Trim()
        let tryParse (s: string) =
            match Uri.TryCreate(s, UriKind.Absolute) with
            | true, u when not (String.IsNullOrEmpty u.Host) -> Some(normHost u.Host)
            | _ -> None
        match tryParse raw with
        | Some h -> Some h
        | None -> tryParse ("http://" + raw)

    /// Does a host match a web pattern? "*" = any; "example.com" matches that host and any
    /// subdomain; otherwise a glob over the host.
    let hostMatches (pattern: string) (host: string) : bool =
        let p = normHost pattern
        let h = normHost host
        if p = "*" || p = "" then true
        else h = p || h.EndsWith("." + p, StringComparison.Ordinal) || glob p h

    /// Does a path match a file pattern? "*" = any; otherwise the pattern matches the path
    /// itself, anything beneath it (prefix), or a glob.
    let pathMatches (pattern: string) (path: string) : bool =
        if pattern = "*" then true
        else
            let norm (s: string) = s.Replace('\\', '/').TrimEnd('/')
            let p = norm pattern
            let x = norm path
            x = p || x.StartsWith(p + "/", StringComparison.Ordinal) || glob pattern path

    let private opMatches (rule: PermissionRule) (op: string) =
        match rule.Operations with
        | [] -> true
        | ops -> ops |> List.contains (op.Trim().ToLowerInvariant())

    /// Does a rule match an access request? (Scope is filtered by the caller via `applicable`.)
    let ruleMatches (access: ResourceAccess) (rule: PermissionRule) : bool =
        match rule.Kind, access with
        | ResourceKind.File, ResourceAccess.File(op, path) -> opMatches rule op && pathMatches rule.Pattern path
        | ResourceKind.Web, ResourceAccess.Web(op, url) ->
            opMatches rule op
            && (match hostOf url with
                | Some h -> hostMatches rule.Pattern h
                | None -> rule.Pattern = "*")
        | ResourceKind.Tool, ResourceAccess.ToolCall name ->
            rule.Pattern = "*" || rule.Pattern = name || glob rule.Pattern name
        | _ -> false

    /// Keep only the rules that apply to the given session — every Global rule, plus the
    /// Session rules whose key matches.
    let applicable (sessionKey: string) (rules: PermissionRule list) : PermissionRule list =
        rules
        |> List.filter (fun r ->
            match r.Scope with
            | RuleScope.Global -> true
            | RuleScope.Session k -> k = sessionKey)

    /// Evaluate an access request against rules, using `defaultDecision` when nothing
    /// matches. Precedence among matching rules: an explicit Deny always wins, then Allow,
    /// then Ask. The caller chooses the default per resource class (e.g. Deny for web,
    /// Allow for unknown tool calls).
    let evaluateWith
        (defaultDecision: PermissionDecision)
        (rules: PermissionRule list)
        (access: ResourceAccess)
        : PermissionDecision =
        let matching = rules |> List.filter (ruleMatches access)
        if matching |> List.exists (fun r -> r.Decision = PermissionDecision.Deny) then
            PermissionDecision.Deny
        elif matching |> List.exists (fun r -> r.Decision = PermissionDecision.Allow) then
            PermissionDecision.Allow
        elif matching |> List.exists (fun r -> r.Decision = PermissionDecision.Ask) then
            PermissionDecision.Ask
        else
            defaultDecision

    /// Evaluate with a strict allowlist default (Deny when nothing matches).
    let evaluate (rules: PermissionRule list) (access: ResourceAccess) : PermissionDecision =
        evaluateWith PermissionDecision.Deny rules access
