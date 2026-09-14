namespace Nao.Knowledge

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Nao.Agents

module GitSourceConnector =
    let private validateRepositoryUri (uri: Uri) =
        if isNull uri || not uri.IsAbsoluteUri || not (String.IsNullOrEmpty uri.Fragment) then
            invalidArg (nameof uri) "Git repository URI must be absolute and cannot contain a fragment."

        if uri.IsFile then
            if not (String.IsNullOrEmpty uri.Query) then
                invalidArg (nameof uri) "Local Git repository URI cannot contain a query."
        elif uri.Scheme <> Uri.UriSchemeHttps || not (String.IsNullOrEmpty uri.UserInfo) then
            invalidArg (nameof uri) "Remote Git repository URI must use HTTPS without embedded credentials."

    let private canonicalPath path =
        if
            String.IsNullOrWhiteSpace path
            || path <> path.Trim()
            || path.Contains('\\')
            || Path.IsPathRooted path
            || path.Split('/')
               |> Array.exists (fun segment -> segment = "." || segment = ".." || segment = ".git")
        then
            invalidArg (nameof path) "Git file path must be canonical, relative, and outside Git metadata."

        path

    let private itemUri (repositoryUri: Uri) (path: string) =
        let builder = UriBuilder repositoryUri
        builder.Fragment <- Uri.EscapeDataString path
        builder.Uri

    let private requireRepositoryAccess (request: SourceSyncRequest) (cancellationToken: CancellationToken) =
        task {
            let uri = request.Source.Uri

            if uri.IsFile then
                let root =
                    Path.GetFullPath(uri.LocalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

                let! allowed =
                    request.Policy.AuthorizeAsync
                        request.Authorization
                        (ResourceAccess.file FileAction.List root)
                        cancellationToken

                if not allowed then
                    raise (UnauthorizedAccessException "Git repository listing was not authorized.")

                return Some root
            else
                match request.Policy.Network with
                | SourceNetworkPolicy.Allowlist allowed -> allowed |> List.iter validateRepositoryUri
                | SourceNetworkPolicy.Denied -> ()

                if not (SourceNetworkPolicy.isAllowed uri request.Policy.Network) then
                    raise (UnauthorizedAccessException "Git repository network destination is not allowlisted.")

                let! allowed =
                    request.Policy.AuthorizeAsync
                        request.Authorization
                        (ResourceAccess.web WebAction.Get uri.AbsoluteUri)
                        cancellationToken

                if not allowed then
                    raise (UnauthorizedAccessException "Git repository fetch was not authorized.")

                return None
        }

    let create (config: GitSourceConnectorConfig) =
        if
            String.IsNullOrWhiteSpace config.Reference
            || config.Reference <> config.Reference.Trim()
        then
            invalidArg (nameof config.Reference) "Git reference must be canonical and non-blank."

        if config.MaximumBlobBytes <= 0L then
            invalidArg (nameof config.MaximumBlobBytes) "Maximum Git blob bytes must be positive."

        if config.MaximumItems <= 0 then
            invalidArg (nameof config.MaximumItems) "Maximum Git items must be positive."

        { Descriptor =
            { Connector = GitSourceCheckpoint.connector
              Modes = Set.ofList [ SourceConnectorMode.Pull; SourceConnectorMode.Snapshot ] }
          SynchronizeAsync =
            fun request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    validateRepositoryUri request.Source.Uri

                    if request.Source.Owner <> AuthorizationScope.identity request.Authorization then
                        raise (
                            UnauthorizedAccessException "Knowledge source belongs to another authorization identity."
                        )

                    if
                        request.Mode <> SourceConnectorMode.Pull
                        && request.Mode <> SourceConnectorMode.Snapshot
                    then
                        invalidArg (nameof request) "Git connector supports pull and snapshot modes only."

                    let! canIndex =
                        request.Policy.AuthorizeAsync
                            request.Authorization
                            (ResourceAccess.knowledge (KnowledgeSourceId.value request.Source.Id) KnowledgeAction.Index)
                            cancellationToken

                    if not canIndex then
                        raise (UnauthorizedAccessException "Git knowledge indexing was not authorized.")

                    let! localRoot = requireRepositoryAccess request cancellationToken

                    let previous =
                        match request.Mode, request.Checkpoint with
                        | SourceConnectorMode.Pull, Some checkpoint -> Some(GitSourceCheckpoint.deserialize checkpoint)
                        | _ -> None

                    let! result =
                        config.Repository.ReadAsync
                            { RepositoryUri = request.Source.Uri
                              Reference = config.Reference
                              PreviousCommit = previous |> Option.map _.CommitId
                              MaximumBlobBytes = config.MaximumBlobBytes
                              MaximumItems = config.MaximumItems
                              Network = request.Policy.Network }
                            cancellationToken

                    match result, previous with
                    | GitSourceReadResult.Unchanged commitId, Some state when commitId = state.CommitId ->
                        let checkpoint =
                            state
                            |> GitSourceCheckpoint.serialize
                            |> SourceConnectorCheckpoint.create GitSourceCheckpoint.connector

                        return
                            { SourceId = request.Source.Id
                              Connector = GitSourceCheckpoint.connector
                              StartedAt = request.RequestedAt
                              CompletedAt = DateTimeOffset.UtcNow
                              Changes = []
                              Failures = []
                              Checkpoint = checkpoint }
                    | GitSourceReadResult.Unchanged _, _ ->
                        return invalidOp "Git reader reported unchanged state without the matching prior commit."
                    | GitSourceReadResult.Snapshot snapshot, _ ->
                        let selected =
                            snapshot.Files
                            |> List.map (fun file -> canonicalPath file.Path, file)
                            |> List.filter (fun (path, _) -> SourcePath.selected config.Include config.Exclude path)

                        if (selected |> List.distinctBy fst |> List.length) <> selected.Length then
                            invalidOp "Git snapshot contains duplicate canonical paths."

                        if selected.Length > config.MaximumItems then
                            invalidOp $"Git snapshot exceeds the configured {config.MaximumItems}-item limit."

                        let previousItems = previous |> Option.map _.Items |> Option.defaultValue []

                        let previousByPath =
                            previousItems |> Seq.map (fun item -> item.RelativePath, item) |> Map.ofSeq

                        let mutable current = []
                        let mutable loaded = []

                        for path, file in selected do
                            cancellationToken.ThrowIfCancellationRequested()

                            if isNull file.Content then
                                invalidOp $"Git reader returned null content for '{path}'."

                            if int64 file.Content.Length > config.MaximumBlobBytes then
                                invalidOp $"Git blob '{path}' exceeds the configured byte limit."

                            match localRoot with
                            | Some root ->
                                let fullPath = Path.GetFullPath(Path.Combine(root, path))

                                if
                                    not (
                                        fullPath.StartsWith(
                                            root + string Path.DirectorySeparatorChar,
                                            StringComparison.Ordinal
                                        )
                                    )
                                then
                                    invalidOp "Git path escaped the repository root."

                                let! canRead =
                                    request.Policy.AuthorizeAsync
                                        request.Authorization
                                        (ResourceAccess.file FileAction.Read fullPath)
                                        cancellationToken

                                if not canRead then
                                    raise (UnauthorizedAccessException $"Git file read was not authorized: {path}.")
                            | None -> ()

                            let hash = ContentHash.computeBytes file.Content

                            let itemId =
                                previousByPath
                                |> Map.tryFind path
                                |> Option.map (_.ItemId >> SourceItemId.parse)
                                |> Option.defaultWith (fun () -> SourceItemId.forPath request.Source.Id path)

                            let manifest =
                                { ItemId = SourceItemId.value itemId
                                  RelativePath = path
                                  ContentHash = ContentHash.value hash
                                  ModifiedAt = snapshot.CommittedAt
                                  ByteLength = int64 file.Content.Length }

                            let content =
                                { ItemId = itemId
                                  Uri = itemUri request.Source.Uri path
                                  RelativePath = path
                                  MediaType = file.MediaType |> Option.defaultValue (SourcePath.mediaType path)
                                  Content = file.Content
                                  ContentHash = hash
                                  ByteLength = int64 file.Content.Length
                                  ModifiedAt = snapshot.CommittedAt
                                  Metadata =
                                    Map.ofList
                                        [ "git.commit", GitCommitId.value snapshot.CommitId
                                          "git.reference", config.Reference ] }

                            current <- manifest :: current
                            loaded <- (manifest, content) :: loaded

                        let current, changes =
                            SourceChanges.detect (itemUri request.Source.Uri) previousItems current loaded

                        match previous with
                        | Some state when state.CommitId = snapshot.CommitId && not (List.isEmpty changes) ->
                            invalidOp "Git reader returned changed content for an immutable commit."
                        | _ -> ()

                        let checkpoint =
                            { CommitId = snapshot.CommitId
                              Items = current }
                            |> GitSourceCheckpoint.serialize
                            |> SourceConnectorCheckpoint.create GitSourceCheckpoint.connector

                        return
                            { SourceId = request.Source.Id
                              Connector = GitSourceCheckpoint.connector
                              StartedAt = request.RequestedAt
                              CompletedAt = DateTimeOffset.UtcNow
                              Changes = changes
                              Failures = []
                              Checkpoint = checkpoint }
                } }
