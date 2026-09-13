namespace Nao.Agents

open System

/// Pure evaluation of canonical resource-access requests against permission rules.
[<RequireQualifiedAccess>]
module ResourcePermission =
    /// Match an explicit glob pattern ('*' = any run of chars, '?' = exactly one).
    /// Resource identifiers remain case-sensitive; web hosts are normalized before matching.
    let glob (pattern: string) (text: string) : bool =
        let pl = pattern.Length
        let tl = text.Length
        let dp = Array2D.create (pl + 1) (tl + 1) false
        dp.[0, 0] <- true

        for i in 1..pl do
            if pattern.[i - 1] = '*' then
                dp.[i, 0] <- dp.[i - 1, 0]

        for i in 1..pl do
            for j in 1..tl do
                match pattern.[i - 1] with
                | '*' -> dp.[i, j] <- dp.[i - 1, j] || dp.[i, j - 1]
                | '?' -> dp.[i, j] <- dp.[i - 1, j - 1]
                | character -> dp.[i, j] <- dp.[i - 1, j - 1] && character = text.[j - 1]

        dp.[pl, tl]

    let originOf (url: string) : string option =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, uri when
            (uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps)
            && not (String.IsNullOrEmpty uri.Host)
            ->
            let port = if uri.IsDefaultPort then "" else ":" + string uri.Port

            Some(sprintf "%s://%s%s" (uri.Scheme.ToLowerInvariant()) (uri.IdnHost.ToLowerInvariant()) port)
        | _ -> None

    let private wildcardOrigin (pattern: string) =
        let separator = "://*."
        let index = pattern.IndexOf(separator, StringComparison.Ordinal)

        if index <= 0 then
            None
        else
            let scheme = pattern.Substring(0, index).ToLowerInvariant()
            let hostSuffix = pattern.Substring(index + separator.Length).ToLowerInvariant()

            if
                (scheme = Uri.UriSchemeHttp || scheme = Uri.UriSchemeHttps)
                && not (String.IsNullOrWhiteSpace hostSuffix)
                && not (hostSuffix.Contains('/'))
                && not (hostSuffix.Contains(':'))
                && not (hostSuffix.Contains('*'))
                && not (hostSuffix.Contains('?'))
                && hostSuffix = hostSuffix.Trim().TrimEnd('.')
            then
                Some(scheme, hostSuffix)
            else
                None

    let private canonicalOriginPattern pattern =
        if pattern = "*" then
            Some pattern
        else
            match wildcardOrigin pattern with
            | Some _ -> Some pattern
            | None ->
                match Uri.TryCreate(pattern, UriKind.Absolute) with
                | true, uri when
                    (uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps)
                    && uri.AbsolutePath = "/"
                    && String.IsNullOrEmpty uri.Query
                    && String.IsNullOrEmpty uri.Fragment
                    && String.IsNullOrEmpty uri.UserInfo
                    ->
                    originOf pattern
                | _ -> None

    /// Web rules match an exact scheme/host/port origin, `*`, or `scheme://*.domain`.
    let originMatches (pattern: string) (url: string) : bool =
        if String.IsNullOrWhiteSpace pattern || String.IsNullOrWhiteSpace url then
            false
        elif pattern = "*" then
            true
        else
            match wildcardOrigin pattern, originOf url with
            | Some(scheme, hostSuffix), Some requestedOrigin ->
                let requested = Uri requestedOrigin

                requested.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase)
                && requested.IsDefaultPort
                && requested.IdnHost.EndsWith("." + hostSuffix, StringComparison.OrdinalIgnoreCase)
            | None, Some requestedOrigin ->
                canonicalOriginPattern pattern
                |> Option.exists (fun configured ->
                    configured.Equals(requestedOrigin, StringComparison.OrdinalIgnoreCase))
            | _ -> false

    /// "*" matches every path. A plain path matches itself and descendants;
    /// wildcard syntax is matched against slash-normalized paths.
    let pathMatches (pattern: string) (path: string) : bool =
        if String.IsNullOrWhiteSpace pattern || String.IsNullOrWhiteSpace path then
            false
        elif pattern = "*" then
            true
        else
            let normalize (value: string) = value.Replace('\\', '/').TrimEnd('/')
            let pattern = normalize pattern
            let path = normalize path

            if pattern.Contains('*') || pattern.Contains('?') then
                glob pattern path
            else
                path = pattern || path.StartsWith(pattern + "/", StringComparison.Ordinal)

    let private nameMatches pattern name =
        not (String.IsNullOrWhiteSpace pattern)
        && not (String.IsNullOrWhiteSpace name)
        && (pattern = "*" || glob pattern name)

    let private actionsCover broader narrower =
        PermissionActions.covers broader narrower

    let tryTarget kind action pattern =
        if String.IsNullOrWhiteSpace pattern || pattern <> pattern.Trim() then
            None
        else
            match kind, action with
            | "file", "read" -> Some(PermissionTarget.File(pattern, PermissionActions.only [ FileAction.Read ]))
            | "file", "write" -> Some(PermissionTarget.File(pattern, PermissionActions.only [ FileAction.Write ]))
            | "file", "delete" -> Some(PermissionTarget.File(pattern, PermissionActions.only [ FileAction.Delete ]))
            | "file", "list" -> Some(PermissionTarget.File(pattern, PermissionActions.only [ FileAction.List ]))
            | "web", "get" when canonicalOriginPattern pattern |> Option.isSome ->
                Some(PermissionTarget.Web(pattern, PermissionActions.only [ WebAction.Get ]))
            | "web", "head" when canonicalOriginPattern pattern |> Option.isSome ->
                Some(PermissionTarget.Web(pattern, PermissionActions.only [ WebAction.Head ]))
            | "web", "post" when canonicalOriginPattern pattern |> Option.isSome ->
                Some(PermissionTarget.Web(pattern, PermissionActions.only [ WebAction.Post ]))
            | "web", "put" when canonicalOriginPattern pattern |> Option.isSome ->
                Some(PermissionTarget.Web(pattern, PermissionActions.only [ WebAction.Put ]))
            | "web", "patch" when canonicalOriginPattern pattern |> Option.isSome ->
                Some(PermissionTarget.Web(pattern, PermissionActions.only [ WebAction.Patch ]))
            | "web", "delete" when canonicalOriginPattern pattern |> Option.isSome ->
                Some(PermissionTarget.Web(pattern, PermissionActions.only [ WebAction.Delete ]))
            | "web", "options" when canonicalOriginPattern pattern |> Option.isSome ->
                Some(PermissionTarget.Web(pattern, PermissionActions.only [ WebAction.Options ]))
            | "web", "connect" when canonicalOriginPattern pattern |> Option.isSome ->
                Some(PermissionTarget.Web(pattern, PermissionActions.only [ WebAction.Connect ]))
            | "tool", "invoke" -> Some(PermissionTarget.Tool pattern)
            | "memory", "read" -> Some(PermissionTarget.Memory(pattern, PermissionActions.only [ MemoryAction.Read ]))
            | "memory", "write" -> Some(PermissionTarget.Memory(pattern, PermissionActions.only [ MemoryAction.Write ]))
            | "memory", "delete" ->
                Some(PermissionTarget.Memory(pattern, PermissionActions.only [ MemoryAction.Delete ]))
            | "knowledge", "retrieve" ->
                Some(PermissionTarget.Knowledge(pattern, PermissionActions.only [ KnowledgeAction.Retrieve ]))
            | "knowledge", "index" ->
                Some(PermissionTarget.Knowledge(pattern, PermissionActions.only [ KnowledgeAction.Index ]))
            | "knowledge", "delete" ->
                Some(PermissionTarget.Knowledge(pattern, PermissionActions.only [ KnowledgeAction.Delete ]))
            | "model", "invoke" -> Some(PermissionTarget.Model pattern)
            | "artifact", "read" ->
                Some(PermissionTarget.Artifact(pattern, PermissionActions.only [ ArtifactAction.Read ]))
            | "artifact", "publish" ->
                Some(PermissionTarget.Artifact(pattern, PermissionActions.only [ ArtifactAction.Publish ]))
            | "artifact", "delete" ->
                Some(PermissionTarget.Artifact(pattern, PermissionActions.only [ ArtifactAction.Delete ]))
            | "administration", "read" ->
                Some(PermissionTarget.Administration(pattern, PermissionActions.only [ AdministrationAction.Read ]))
            | "administration", "manage" ->
                Some(PermissionTarget.Administration(pattern, PermissionActions.only [ AdministrationAction.Manage ]))
            | _ -> None

    let targetKind =
        function
        | PermissionTarget.File _ -> "file"
        | PermissionTarget.Web _ -> "web"
        | PermissionTarget.Tool _ -> "tool"
        | PermissionTarget.Memory _ -> "memory"
        | PermissionTarget.Knowledge _ -> "knowledge"
        | PermissionTarget.Model _ -> "model"
        | PermissionTarget.Artifact _ -> "artifact"
        | PermissionTarget.Administration _ -> "administration"

    let targetResource =
        function
        | PermissionTarget.File(pattern, _)
        | PermissionTarget.Web(pattern, _)
        | PermissionTarget.Memory(pattern, _)
        | PermissionTarget.Knowledge(pattern, _)
        | PermissionTarget.Artifact(pattern, _)
        | PermissionTarget.Administration(pattern, _)
        | PermissionTarget.Tool pattern
        | PermissionTarget.Model pattern -> pattern

    /// Returns whether one rule target covers another target of the same resource class.
    let targetCovers broader narrower =
        match broader, narrower with
        | PermissionTarget.File(broaderPath, broaderActions), PermissionTarget.File(narrowerPath, narrowerActions) ->
            actionsCover broaderActions narrowerActions
            && pathMatches broaderPath narrowerPath
        | PermissionTarget.Web(broaderHost, broaderActions), PermissionTarget.Web(narrowerHost, narrowerActions) ->
            actionsCover broaderActions narrowerActions
            && if narrowerHost.Contains('*') then
                   broaderHost = "*" || broaderHost = narrowerHost
               else
                   originMatches broaderHost narrowerHost
        | PermissionTarget.Tool broaderName, PermissionTarget.Tool narrowerName -> nameMatches broaderName narrowerName
        | PermissionTarget.Memory(broaderName, broaderActions), PermissionTarget.Memory(narrowerName, narrowerActions) ->
            actionsCover broaderActions narrowerActions
            && nameMatches broaderName narrowerName
        | PermissionTarget.Knowledge(broaderName, broaderActions),
          PermissionTarget.Knowledge(narrowerName, narrowerActions) ->
            actionsCover broaderActions narrowerActions
            && nameMatches broaderName narrowerName
        | PermissionTarget.Model broaderName, PermissionTarget.Model narrowerName ->
            nameMatches broaderName narrowerName
        | PermissionTarget.Artifact(broaderName, broaderActions),
          PermissionTarget.Artifact(narrowerName, narrowerActions) ->
            actionsCover broaderActions narrowerActions
            && nameMatches broaderName narrowerName
        | PermissionTarget.Administration(broaderName, broaderActions),
          PermissionTarget.Administration(narrowerName, narrowerActions) ->
            actionsCover broaderActions narrowerActions
            && nameMatches broaderName narrowerName
        | _ -> false

    let targetForAccess =
        function
        | ResourceAccess.File(action, path) -> PermissionTarget.File(path, PermissionActions.only [ action ])
        | ResourceAccess.Web(action, url) ->
            let origin =
                originOf url
                |> Option.defaultWith (fun () -> invalidArg (nameof url) "Web access must contain a valid origin.")

            PermissionTarget.Web(origin, PermissionActions.only [ action ])
        | ResourceAccess.Tool name -> PermissionTarget.Tool name
        | ResourceAccess.Memory(action, name) -> PermissionTarget.Memory(name, PermissionActions.only [ action ])
        | ResourceAccess.Knowledge(action, name) -> PermissionTarget.Knowledge(name, PermissionActions.only [ action ])
        | ResourceAccess.Model name -> PermissionTarget.Model name
        | ResourceAccess.Artifact(action, name) -> PermissionTarget.Artifact(name, PermissionActions.only [ action ])
        | ResourceAccess.Administration(action, name) ->
            PermissionTarget.Administration(name, PermissionActions.only [ action ])

    let ruleMatches (access: ResourceAccess) (rule: PermissionRule) : bool =
        ResourceAccess.isCanonical access
        && match rule.AppliesTo, access with
           | PermissionTarget.File(pattern, actions), ResourceAccess.File(action, path) ->
               PermissionActions.contains action actions && pathMatches pattern path
           | PermissionTarget.Web(pattern, actions), ResourceAccess.Web(action, url) ->
               PermissionActions.contains action actions && originMatches pattern url
           | PermissionTarget.Tool pattern, ResourceAccess.Tool name -> nameMatches pattern name
           | PermissionTarget.Memory(pattern, actions), ResourceAccess.Memory(action, name) ->
               PermissionActions.contains action actions && nameMatches pattern name
           | PermissionTarget.Knowledge(pattern, actions), ResourceAccess.Knowledge(action, name) ->
               PermissionActions.contains action actions && nameMatches pattern name
           | PermissionTarget.Model pattern, ResourceAccess.Model name -> nameMatches pattern name
           | PermissionTarget.Artifact(pattern, actions), ResourceAccess.Artifact(action, name) ->
               PermissionActions.contains action actions && nameMatches pattern name
           | PermissionTarget.Administration(pattern, actions), ResourceAccess.Administration(action, name) ->
               PermissionActions.contains action actions && nameMatches pattern name
           | _ -> false

    let applicable (sessionKey: string) (rules: PermissionRule list) : PermissionRule list =
        rules
        |> List.filter (fun rule ->
            match rule.Scope with
            | RuleScope.Global -> true
            | RuleScope.Session key -> key = sessionKey)

    /// Explicit deny wins over allow, which wins over ask. No matching rule uses the caller's
    /// declared default; protected callers should use `evaluate`, whose default is deny.
    let evaluateWith defaultDecision rules access =
        let canonical = ResourceAccess.isCanonical access

        let matching =
            if canonical then
                rules |> List.filter (ruleMatches access)
            else
                []

        if not canonical then
            PermissionDecision.Deny
        elif matching |> List.exists (fun rule -> rule.Decision = PermissionDecision.Deny) then
            PermissionDecision.Deny
        elif matching |> List.exists (fun rule -> rule.Decision = PermissionDecision.Allow) then
            PermissionDecision.Allow
        elif matching |> List.exists (fun rule -> rule.Decision = PermissionDecision.Ask) then
            PermissionDecision.Ask
        else
            defaultDecision

    let evaluate rules access =
        evaluateWith PermissionDecision.Deny rules access
