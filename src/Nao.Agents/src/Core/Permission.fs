namespace Nao.Agents

open System
open System.IO

[<RequireQualifiedAccess>]
type FileAction =
    | Read
    | Write
    | Delete
    | List

[<RequireQualifiedAccess>]
type WebAction =
    | Get
    | Head
    | Post
    | Put
    | Patch
    | Delete
    | Options
    | Connect

[<RequireQualifiedAccess>]
type MemoryAction =
    | Read
    | Write
    | Delete

[<RequireQualifiedAccess>]
type KnowledgeAction =
    | Retrieve
    | Index
    | Delete

[<RequireQualifiedAccess>]
type ArtifactAction =
    | Read
    | Publish
    | Delete

[<RequireQualifiedAccess>]
type AdministrationAction =
    | Read
    | Manage

/// A canonical sensitive action together with its concrete resource identifier.
[<RequireQualifiedAccess>]
type ResourceAccess =
    | File of action: FileAction * path: string
    | Web of action: WebAction * url: string
    | Tool of toolName: string
    | Memory of action: MemoryAction * memoryName: string
    | Knowledge of action: KnowledgeAction * sourceName: string
    | Model of modelName: string
    | Artifact of action: ArtifactAction * artifactName: string
    | Administration of action: AdministrationAction * capabilityName: string

[<RequireQualifiedAccess>]
module ResourceAccess =
    let private canonicalName parameterName value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg parameterName "Resource identity must be non-blank and have no surrounding whitespace."

        value

    let file action path =
        if String.IsNullOrWhiteSpace path then
            invalidArg (nameof path) "File resource path must be non-blank."

        ResourceAccess.File(action, Path.GetFullPath path)

    let web action url =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, uri when
            (uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps)
            && not (String.IsNullOrEmpty uri.Host)
            ->
            ResourceAccess.Web(action, uri.AbsoluteUri)
        | _ -> invalidArg (nameof url) "Web resource must be an absolute HTTP or HTTPS URL."

    let tool name =
        ResourceAccess.Tool(canonicalName (nameof name) name)

    let memory name action =
        ResourceAccess.Memory(action, canonicalName (nameof name) name)

    let knowledge name action =
        ResourceAccess.Knowledge(action, canonicalName (nameof name) name)

    let model name =
        ResourceAccess.Model(canonicalName (nameof name) name)

    let artifact name action =
        ResourceAccess.Artifact(action, canonicalName (nameof name) name)

    let administration name action =
        ResourceAccess.Administration(action, canonicalName (nameof name) name)

    let kind =
        function
        | ResourceAccess.File _ -> "file"
        | ResourceAccess.Web _ -> "web"
        | ResourceAccess.Tool _ -> "tool"
        | ResourceAccess.Memory _ -> "memory"
        | ResourceAccess.Knowledge _ -> "knowledge"
        | ResourceAccess.Model _ -> "model"
        | ResourceAccess.Artifact _ -> "artifact"
        | ResourceAccess.Administration _ -> "administration"

    let action =
        function
        | ResourceAccess.File(FileAction.Read, _) -> "read"
        | ResourceAccess.File(FileAction.Write, _) -> "write"
        | ResourceAccess.File(FileAction.Delete, _) -> "delete"
        | ResourceAccess.File(FileAction.List, _) -> "list"
        | ResourceAccess.Web(WebAction.Get, _) -> "get"
        | ResourceAccess.Web(WebAction.Head, _) -> "head"
        | ResourceAccess.Web(WebAction.Post, _) -> "post"
        | ResourceAccess.Web(WebAction.Put, _) -> "put"
        | ResourceAccess.Web(WebAction.Patch, _) -> "patch"
        | ResourceAccess.Web(WebAction.Delete, _) -> "delete"
        | ResourceAccess.Web(WebAction.Options, _) -> "options"
        | ResourceAccess.Web(WebAction.Connect, _) -> "connect"
        | ResourceAccess.Tool _ -> "invoke"
        | ResourceAccess.Memory(MemoryAction.Read, _) -> "read"
        | ResourceAccess.Memory(MemoryAction.Write, _) -> "write"
        | ResourceAccess.Memory(MemoryAction.Delete, _) -> "delete"
        | ResourceAccess.Knowledge(KnowledgeAction.Retrieve, _) -> "retrieve"
        | ResourceAccess.Knowledge(KnowledgeAction.Index, _) -> "index"
        | ResourceAccess.Knowledge(KnowledgeAction.Delete, _) -> "delete"
        | ResourceAccess.Model _ -> "invoke"
        | ResourceAccess.Artifact(ArtifactAction.Read, _) -> "read"
        | ResourceAccess.Artifact(ArtifactAction.Publish, _) -> "publish"
        | ResourceAccess.Artifact(ArtifactAction.Delete, _) -> "delete"
        | ResourceAccess.Administration(AdministrationAction.Read, _) -> "read"
        | ResourceAccess.Administration(AdministrationAction.Manage, _) -> "manage"

    let resource =
        function
        | ResourceAccess.File(_, value)
        | ResourceAccess.Web(_, value)
        | ResourceAccess.Memory(_, value)
        | ResourceAccess.Knowledge(_, value)
        | ResourceAccess.Artifact(_, value)
        | ResourceAccess.Administration(_, value)
        | ResourceAccess.Tool value
        | ResourceAccess.Model value -> value

    let tryCreate kind action resource =
        try
            match kind, action with
            | "file", "read" -> Some(file FileAction.Read resource)
            | "file", "write" -> Some(file FileAction.Write resource)
            | "file", "delete" -> Some(file FileAction.Delete resource)
            | "file", "list" -> Some(file FileAction.List resource)
            | "web", "get" -> Some(web WebAction.Get resource)
            | "web", "head" -> Some(web WebAction.Head resource)
            | "web", "post" -> Some(web WebAction.Post resource)
            | "web", "put" -> Some(web WebAction.Put resource)
            | "web", "patch" -> Some(web WebAction.Patch resource)
            | "web", "delete" -> Some(web WebAction.Delete resource)
            | "web", "options" -> Some(web WebAction.Options resource)
            | "web", "connect" -> Some(web WebAction.Connect resource)
            | "tool", "invoke" -> Some(tool resource)
            | "memory", "read" -> Some(memory resource MemoryAction.Read)
            | "memory", "write" -> Some(memory resource MemoryAction.Write)
            | "memory", "delete" -> Some(memory resource MemoryAction.Delete)
            | "knowledge", "retrieve" -> Some(knowledge resource KnowledgeAction.Retrieve)
            | "knowledge", "index" -> Some(knowledge resource KnowledgeAction.Index)
            | "knowledge", "delete" -> Some(knowledge resource KnowledgeAction.Delete)
            | "model", "invoke" -> Some(model resource)
            | "artifact", "read" -> Some(artifact resource ArtifactAction.Read)
            | "artifact", "publish" -> Some(artifact resource ArtifactAction.Publish)
            | "artifact", "delete" -> Some(artifact resource ArtifactAction.Delete)
            | "administration", "read" -> Some(administration resource AdministrationAction.Read)
            | "administration", "manage" -> Some(administration resource AdministrationAction.Manage)
            | _ -> None
        with :? ArgumentException ->
            None

    let isCanonical access =
        tryCreate (kind access) (action access) (resource access) = Some access

    /// Returns whether an approved concrete request authorizes another concrete request.
    let isCoveredBy approved requested =
        isCanonical approved
        && isCanonical requested
        && match approved, requested with
           | ResourceAccess.Web(approvedAction, approvedUrl), ResourceAccess.Web(requestedAction, requestedUrl) ->
               let approvedUri = Uri approvedUrl
               let requestedUri = Uri requestedUrl

               approvedAction = requestedAction
               && approvedUri.Scheme.Equals(requestedUri.Scheme, StringComparison.OrdinalIgnoreCase)
               && approvedUri.IdnHost.Equals(requestedUri.IdnHost, StringComparison.OrdinalIgnoreCase)
               && approvedUri.Port = requestedUri.Port
           | _ ->
               kind approved = kind requested
               && action approved = action requested
               && resource approved = resource requested

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

[<RequireQualifiedAccess>]
type PermissionActions<'action when 'action: comparison> =
    | All
    | Only of Set<'action>

[<RequireQualifiedAccess>]
module PermissionActions =
    let all = PermissionActions.All

    let only actions =
        let actions = Set.ofSeq actions

        if actions.IsEmpty then
            invalidArg (nameof actions) "An explicit permission action set cannot be empty."

        PermissionActions.Only actions

    let contains action =
        function
        | PermissionActions.All -> true
        | PermissionActions.Only actions -> Set.contains action actions

    let covers broader narrower =
        match broader, narrower with
        | PermissionActions.All, _ -> true
        | PermissionActions.Only _, PermissionActions.All -> false
        | PermissionActions.Only broaderActions, PermissionActions.Only narrowerActions ->
            Set.isSubset narrowerActions broaderActions

/// A typed pattern matched by a permission rule.
[<RequireQualifiedAccess>]
type PermissionTarget =
    | File of pathPattern: string * actions: PermissionActions<FileAction>
    | Web of hostPattern: string * actions: PermissionActions<WebAction>
    | Tool of namePattern: string
    | Memory of namePattern: string * actions: PermissionActions<MemoryAction>
    | Knowledge of namePattern: string * actions: PermissionActions<KnowledgeAction>
    | Model of namePattern: string
    | Artifact of namePattern: string * actions: PermissionActions<ArtifactAction>
    | Administration of namePattern: string * actions: PermissionActions<AdministrationAction>

/// A single allow, deny, or ask rule.
type PermissionRule =
    { Id: string
      AppliesTo: PermissionTarget
      Decision: PermissionDecision
      Scope: RuleScope
      CreatedAt: DateTimeOffset }
