namespace Nao.Agents

open System

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

    let private opMatches operations (op: string) =
        match operations with
        | [] -> true
        | ops -> ops |> List.contains (op.Trim().ToLowerInvariant())

    /// Returns whether one rule target covers another target of the same resource class.
    let targetCovers broader narrower =
        match broader, narrower with
        | PermissionTarget.File(broaderPath, _), PermissionTarget.File(narrowerPath, _) ->
            pathMatches broaderPath narrowerPath
        | PermissionTarget.Web(broaderHost, _), PermissionTarget.Web(narrowerHost, _) ->
            hostMatches broaderHost narrowerHost
        | PermissionTarget.Tool broaderName, PermissionTarget.Tool narrowerName ->
            broaderName = "*" || glob broaderName narrowerName
        | _ -> false

    /// Does a rule match an access request? (Scope is filtered by the caller via `applicable`.)
    let ruleMatches (access: ResourceAccess) (rule: PermissionRule) : bool =
        match rule.AppliesTo, access with
        | PermissionTarget.File(pattern, operations), ResourceAccess.File(op, path) ->
            opMatches operations op && pathMatches pattern path
        | PermissionTarget.Web(pattern, operations), ResourceAccess.Web(op, url) ->
            opMatches operations op
            && (match hostOf url with
                | Some h -> hostMatches pattern h
                | None -> pattern = "*")
        | PermissionTarget.Tool pattern, ResourceAccess.ToolCall name ->
            pattern = "*" || glob pattern name
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
