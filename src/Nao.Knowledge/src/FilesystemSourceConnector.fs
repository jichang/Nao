namespace Nao.Knowledge

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type FilesystemSourceReader =
    { ReadAllBytesAsync: string -> CancellationToken -> Task<byte array> }

type FilesystemSourceConnectorConfig =
    { RootPath: string
      Include: string list
      Exclude: string list
      MaximumContentBytes: int64
      MaximumItems: int
      Reader: FilesystemSourceReader }

module FilesystemSourceReader =
    let system =
        { ReadAllBytesAsync = fun path cancellationToken -> File.ReadAllBytesAsync(path, cancellationToken) }

module FilesystemSourceConnector =
    let private normalizeRelative root path =
        Path.GetRelativePath(root, path).Replace('\\', '/')

    let private selected (config: FilesystemSourceConnectorConfig) relativePath =
        SourcePath.selected config.Include config.Exclude relativePath

    let private requireRequest (root: string) (request: SourceSyncRequest) (cancellationToken: CancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()

        if request.Source.Owner <> AuthorizationScope.identity request.Authorization then
            raise (UnauthorizedAccessException "Knowledge source belongs to another authorization identity.")

        let sourceRoot =
            if
                not request.Source.Uri.IsAbsoluteUri
                || not request.Source.Uri.IsFile
                || not (String.IsNullOrEmpty request.Source.Uri.Query)
                || not (String.IsNullOrEmpty request.Source.Uri.Fragment)
            then
                invalidArg (nameof request) "Filesystem source URI must identify the configured local directory."

            request.Source.Uri.LocalPath
            |> Path.GetFullPath
            |> _.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

        let configuredRoot =
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

        if not (String.Equals(sourceRoot, configuredRoot, StringComparison.Ordinal)) then
            invalidArg (nameof request) "Filesystem source URI does not match the configured root."

        if
            request.Mode <> SourceConnectorMode.Pull
            && request.Mode <> SourceConnectorMode.Snapshot
        then
            invalidArg (nameof request) "Filesystem connector supports pull and snapshot modes only."

        task {
            let! canIndex =
                request.Policy.AuthorizeAsync
                    request.Authorization
                    (ResourceAccess.knowledge (KnowledgeSourceId.value request.Source.Id) KnowledgeAction.Index)
                    cancellationToken

            let! canList =
                request.Policy.AuthorizeAsync
                    request.Authorization
                    (ResourceAccess.file FileAction.List root)
                    cancellationToken

            if not canIndex || not canList then
                raise (UnauthorizedAccessException "Filesystem synchronization was not authorized.")
        }

    let create (config: FilesystemSourceConnectorConfig) =
        if
            String.IsNullOrWhiteSpace config.RootPath
            || not (Path.IsPathFullyQualified config.RootPath)
            || not (Directory.Exists config.RootPath)
        then
            invalidArg (nameof config.RootPath) "Filesystem source root must be an existing absolute directory."

        if config.MaximumContentBytes <= 0L then
            invalidArg (nameof config.MaximumContentBytes) "Maximum content bytes must be positive."

        if config.MaximumItems <= 0 then
            invalidArg (nameof config.MaximumItems) "Maximum items must be positive."

        let root = Path.GetFullPath config.RootPath

        { Descriptor =
            { Connector = FilesystemSourceCheckpoint.connector
              Modes = Set.ofList [ SourceConnectorMode.Pull; SourceConnectorMode.Snapshot ] }
          SynchronizeAsync =
            fun request cancellationToken ->
                task {
                    do! requireRequest root request cancellationToken

                    let previous =
                        match request.Mode, request.Checkpoint with
                        | SourceConnectorMode.Pull, Some checkpoint -> FilesystemSourceCheckpoint.deserialize checkpoint
                        | _ -> []

                    let previousByPath =
                        previous |> Seq.map (fun item -> item.RelativePath, item) |> Map.ofSeq

                    let files =
                        Directory.EnumerateFiles(
                            root,
                            "*",
                            EnumerationOptions(
                                RecurseSubdirectories = true,
                                IgnoreInaccessible = false,
                                AttributesToSkip = FileAttributes.ReparsePoint
                            )
                        )
                        |> Seq.map (fun path -> path, normalizeRelative root path)
                        |> Seq.filter (snd >> selected config)
                        |> Seq.sortBy snd
                        |> Seq.truncate (config.MaximumItems + 1)
                        |> List.ofSeq

                    if files.Length > config.MaximumItems then
                        invalidOp $"Filesystem source exceeds the configured {config.MaximumItems}-item limit."

                    let mutable current = []
                    let mutable loaded = []
                    let mutable failures = []

                    let preservePrevious relativePath =
                        match previousByPath |> Map.tryFind relativePath with
                        | Some item -> current <- item :: current
                        | None -> ()

                    for path, relativePath in files do
                        cancellationToken.ThrowIfCancellationRequested()
                        let uri = Uri(path)
                        let info = FileInfo path

                        if info.Length > config.MaximumContentBytes then
                            preservePrevious relativePath

                            failures <-
                                { Uri = uri
                                  Kind = SourceItemFailureKind.ContentTooLarge
                                  Message = $"Content length {info.Length} exceeds limit {config.MaximumContentBytes}."
                                  Attempts = 0 }
                                :: failures
                        else
                            let! canRead =
                                request.Policy.AuthorizeAsync
                                    request.Authorization
                                    (ResourceAccess.file FileAction.Read path)
                                    cancellationToken

                            if not canRead then
                                preservePrevious relativePath

                                failures <-
                                    { Uri = uri
                                      Kind = SourceItemFailureKind.AccessDenied
                                      Message = "File read was not authorized."
                                      Attempts = 0 }
                                    :: failures
                            else
                                try
                                    let! bytes = config.Reader.ReadAllBytesAsync path cancellationToken

                                    if int64 bytes.Length > config.MaximumContentBytes then
                                        preservePrevious relativePath

                                        failures <-
                                            { Uri = uri
                                              Kind = SourceItemFailureKind.ContentTooLarge
                                              Message =
                                                $"Content length {bytes.Length} exceeds limit {config.MaximumContentBytes}."
                                              Attempts = 1 }
                                            :: failures
                                    else
                                        let hash = ContentHash.computeBytes bytes

                                        let itemId =
                                            previousByPath
                                            |> Map.tryFind relativePath
                                            |> Option.map (fun item -> item.ItemId |> SourceItemId.parse)
                                            |> Option.defaultWith (fun () ->
                                                SourceItemId.forPath request.Source.Id relativePath)

                                        let manifestItem =
                                            { ItemId = SourceItemId.value itemId
                                              RelativePath = relativePath
                                              ContentHash = ContentHash.value hash
                                              ModifiedAt = DateTimeOffset(info.LastWriteTimeUtc)
                                              ByteLength = int64 bytes.Length }

                                        let content =
                                            { ItemId = itemId
                                              Uri = uri
                                              RelativePath = relativePath
                                              MediaType = SourcePath.mediaType path
                                              Content = bytes
                                              ContentHash = hash
                                              ByteLength = int64 bytes.Length
                                              ModifiedAt = DateTimeOffset(info.LastWriteTimeUtc)
                                              Metadata = Map.empty }

                                        current <- manifestItem :: current
                                        loaded <- (manifestItem, content) :: loaded
                                with
                                | :? OperationCanceledException as error -> raise error
                                | :? UnauthorizedAccessException as error ->
                                    preservePrevious relativePath

                                    failures <-
                                        { Uri = uri
                                          Kind = SourceItemFailureKind.AccessDenied
                                          Message = error.Message
                                          Attempts = 1 }
                                        :: failures
                                | :? IOException as error ->
                                    preservePrevious relativePath

                                    failures <-
                                        { Uri = uri
                                          Kind = SourceItemFailureKind.Unavailable
                                          Message = error.Message
                                          Attempts = 1 }
                                        :: failures

                    let current, changes =
                        SourceChanges.detect
                            (fun relativePath -> Uri(Path.Combine(root, relativePath)))
                            previous
                            current
                            loaded

                    let checkpoint =
                        current
                        |> List.sortBy _.RelativePath
                        |> FilesystemSourceCheckpoint.serialize
                        |> SourceConnectorCheckpoint.create FilesystemSourceCheckpoint.connector

                    return
                        { SourceId = request.Source.Id
                          Connector = FilesystemSourceCheckpoint.connector
                          StartedAt = request.RequestedAt
                          CompletedAt = DateTimeOffset.UtcNow
                          Changes = changes
                          Failures = List.rev failures
                          Checkpoint = checkpoint }
                } }
