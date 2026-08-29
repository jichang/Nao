namespace Nao.Agents

open System
open System.IO

/// A sensitive action a tool or agent wants to perform, together with the resource it targets.
[<RequireQualifiedAccess>]
type ResourceAccess =
    /// Filesystem access at an absolute path. Operation is "read", "write", "delete", or "list".
    | File of operation: string * path: string
    /// Network access to a URL. Operation is the HTTP method or "fetch".
    | Web of operation: string * url: string
    /// Invocation of a named tool whose resource semantics are opaque.
    | ToolCall of toolName: string

/// Comparison and normalization operations for concrete resource-access requests.
[<RequireQualifiedAccess>]
module ResourceAccess =
    let private sameOperation left right =
        String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

    let private samePath left right =
        if left = "*" then true
        elif String.IsNullOrWhiteSpace left || String.IsNullOrWhiteSpace right then
            String.Equals(left, right, StringComparison.Ordinal)
        else
            let normalize path =
                Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            String.Equals(normalize left, normalize right, StringComparison.Ordinal)

    let private sameHost left right =
        let normalizeHost (value: string) =
            let raw = value.Trim()
            let tryParse candidate =
                match Uri.TryCreate(candidate, UriKind.Absolute) with
                | true, uri when not (String.IsNullOrEmpty uri.Host) ->
                    Some(uri.Host.TrimEnd('.').ToLowerInvariant())
                | _ -> None
            tryParse raw |> Option.orElseWith (fun () -> tryParse ("http://" + raw))

        match normalizeHost left, normalizeHost right with
        | Some approved, Some requested ->
            approved = "*" || String.Equals(approved, requested, StringComparison.OrdinalIgnoreCase)
        | _ -> left = "*" || String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

    /// Returns whether an approved request authorizes another concrete request.
    let isCoveredBy approved requested =
        match approved, requested with
        | ResourceAccess.File(approvedOperation, approvedPath), ResourceAccess.File(requestedOperation, requestedPath) ->
            sameOperation approvedOperation requestedOperation && samePath approvedPath requestedPath
        | ResourceAccess.Web(approvedOperation, approvedUrl), ResourceAccess.Web(requestedOperation, requestedUrl) ->
            sameOperation approvedOperation requestedOperation && sameHost approvedUrl requestedUrl
        | ResourceAccess.ToolCall approvedName, ResourceAccess.ToolCall requestedName ->
            approvedName = "*" || String.Equals(approvedName, requestedName, StringComparison.OrdinalIgnoreCase)
        | _ -> false

/// The outcome of evaluating an access request.
[<RequireQualifiedAccess>]
type PermissionDecision =
    | Allow
    | Deny
    | Ask

/// How broadly a granted rule applies.
[<RequireQualifiedAccess>]
type RuleScope =
    | Session of sessionKey: string
    | Global

/// A typed pattern matched by a permission rule.
[<RequireQualifiedAccess>]
type PermissionTarget =
    | File of pathPattern: string * operations: string list
    | Web of hostPattern: string * operations: string list
    | Tool of namePattern: string

/// A single allow, deny, or ask rule.
type PermissionRule =
    { Id: string
      AppliesTo: PermissionTarget
      Decision: PermissionDecision
      Scope: RuleScope
      CreatedAt: DateTimeOffset }
