namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

module HttpSourceConnector =
    let private canonicalMediaType (value: string) =
        value.Split(';', 2, StringSplitOptions.TrimEntries).[0].ToLowerInvariant()

    let private validateUri argumentName (uri: Uri) =
        if
            isNull uri
            || not uri.IsAbsoluteUri
            || (uri.Scheme <> Uri.UriSchemeHttp && uri.Scheme <> Uri.UriSchemeHttps)
            || not (String.IsNullOrEmpty uri.UserInfo)
            || not (String.IsNullOrEmpty uri.Fragment)
        then
            invalidArg argumentName "HTTP source URI must be an absolute HTTP(S) URI without credentials or a fragment."

    let private requireNetworkAccess policy uri =
        match policy with
        | SourceNetworkPolicy.Denied -> raise (UnauthorizedAccessException "HTTP connector network access is denied.")
        | SourceNetworkPolicy.Allowlist allowed ->
            allowed |> List.iter (validateUri (nameof policy))

            if not (SourceNetworkPolicy.isAllowed uri policy) then
                raise (
                    UnauthorizedAccessException
                        $"HTTP destination origin is not allowlisted: {uri.GetLeftPart(UriPartial.Authority)}."
                )

    let private requireRequest (request: SourceSyncRequest) (cancellationToken: CancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()

        if request.Source.Owner <> AuthorizationScope.identity request.Authorization then
            raise (UnauthorizedAccessException "Knowledge source belongs to another authorization identity.")

        validateUri (nameof request) request.Source.Uri

        if
            request.Mode <> SourceConnectorMode.Pull
            && request.Mode <> SourceConnectorMode.Snapshot
        then
            invalidArg (nameof request) "HTTP connector supports pull and snapshot modes only."

    let private requireDestination request config uri cancellationToken =
        task {
            requireNetworkAccess request.Policy.Network uri

            let! canRead =
                request.Policy.AuthorizeAsync
                    request.Authorization
                    (ResourceAccess.web WebAction.Get uri.AbsoluteUri)
                    cancellationToken

            if not canRead then
                raise (UnauthorizedAccessException $"HTTP GET was not authorized: {uri.AbsoluteUri}.")

            let! robotsAllowed =
                config.Robots.IsAllowedAsync uri config.UserAgent request.Policy.Network cancellationToken

            if not robotsAllowed then
                raise (UnauthorizedAccessException $"Robots policy denied HTTP source: {uri.AbsoluteUri}.")
        }

    let private isRedirect statusCode =
        statusCode = 301
        || statusCode = 302
        || statusCode = 303
        || statusCode = 307
        || statusCode = 308

    let private validateResponseSize maximumBytes response =
        match response.ContentLength with
        | Some length when length < 0L -> invalidOp "HTTP response declared a negative content length."
        | Some length when length > maximumBytes -> invalidOp $"HTTP response exceeds the {maximumBytes}-byte limit."
        | _ -> ()

        if int64 response.Content.Length > maximumBytes then
            invalidOp $"HTTP response exceeds the {maximumBytes}-byte limit."

    let rec private fetch
        (config: HttpSourceConnectorConfig)
        (request: SourceSyncRequest)
        (previous: HttpSourceState option)
        (uri: Uri)
        (redirectCount: int)
        (cancellationToken: CancellationToken)
        =
        task {
            if redirectCount > config.MaximumRedirects then
                invalidOp $"HTTP source exceeded the {config.MaximumRedirects}-redirect limit."

            do! requireDestination request config uri cancellationToken

            let validator = previous |> Option.filter (fun state -> state.Uri = uri)

            let! response =
                config.Transport.SendAsync
                    { Uri = uri
                      IfNoneMatch = validator |> Option.bind _.ETag
                      IfModifiedSince = validator |> Option.bind _.LastModified
                      MaximumResponseBytes = config.MaximumResponseBytes
                      Network = request.Policy.Network }
                    cancellationToken

            if isNull response.Content then
                invalidOp "HTTP transport returned null content."

            validateResponseSize config.MaximumResponseBytes response

            if isRedirect response.StatusCode then
                let destination =
                    response.Location
                    |> Option.defaultWith (fun () -> invalidOp "HTTP redirect response omitted its Location.")
                    |> fun location -> Uri(uri, location)

                validateUri (nameof response.Location) destination
                return! fetch config request previous destination (redirectCount + 1) cancellationToken
            else
                return uri, response
        }

    let create (config: HttpSourceConnectorConfig) =
        if String.IsNullOrWhiteSpace config.UserAgent then
            invalidArg (nameof config.UserAgent) "HTTP connector user agent must be non-blank."

        if config.MaximumResponseBytes <= 0L then
            invalidArg (nameof config.MaximumResponseBytes) "Maximum response bytes must be positive."

        if config.MaximumRedirects < 0 then
            invalidArg (nameof config.MaximumRedirects) "Maximum redirects cannot be negative."

        let allowedMediaTypes = config.AllowedMediaTypes |> Set.map canonicalMediaType

        if Set.isEmpty allowedMediaTypes then
            invalidArg (nameof config.AllowedMediaTypes) "At least one HTTP media type must be allowed."

        { Descriptor =
            { Connector = HttpSourceCheckpoint.connector
              Modes = Set.ofList [ SourceConnectorMode.Pull; SourceConnectorMode.Snapshot ] }
          SynchronizeAsync =
            fun request cancellationToken ->
                task {
                    requireRequest request cancellationToken

                    let! canIndex =
                        request.Policy.AuthorizeAsync
                            request.Authorization
                            (ResourceAccess.knowledge (KnowledgeSourceId.value request.Source.Id) KnowledgeAction.Index)
                            cancellationToken

                    if not canIndex then
                        raise (UnauthorizedAccessException "HTTP knowledge indexing was not authorized.")

                    let previous =
                        match request.Mode, request.Checkpoint with
                        | SourceConnectorMode.Pull, Some checkpoint -> Some(HttpSourceCheckpoint.deserialize checkpoint)
                        | _ -> None

                    let! finalUri, response = fetch config request previous request.Source.Uri 0 cancellationToken

                    match response.StatusCode, previous with
                    | 304, Some state ->
                        let checkpoint =
                            state
                            |> HttpSourceCheckpoint.serialize
                            |> SourceConnectorCheckpoint.create HttpSourceCheckpoint.connector

                        return
                            { SourceId = request.Source.Id
                              Connector = HttpSourceCheckpoint.connector
                              StartedAt = request.RequestedAt
                              CompletedAt = DateTimeOffset.UtcNow
                              Changes = []
                              Failures = []
                              Checkpoint = checkpoint }
                    | 304, None -> return invalidOp "HTTP source returned Not Modified without prior state."
                    | status, _ when status < 200 || status >= 300 ->
                        return invalidOp $"HTTP source returned unsuccessful status {status}."
                    | _ ->
                        let mediaType =
                            response.MediaType
                            |> Option.filter (String.IsNullOrWhiteSpace >> not)
                            |> Option.map canonicalMediaType
                            |> Option.defaultWith (fun () -> invalidOp "HTTP response omitted its media type.")

                        if not (allowedMediaTypes.Contains mediaType) then
                            invalidOp $"HTTP response media type is not allowed: {mediaType}."

                        let contentHash = ContentHash.computeBytes response.Content
                        let itemId = SourceItemId.forPath request.Source.Id request.Source.Uri.AbsoluteUri
                        let modifiedAt = defaultArg response.LastModified request.RequestedAt

                        let content =
                            { ItemId = itemId
                              Uri = finalUri
                              RelativePath = finalUri.AbsoluteUri
                              MediaType = mediaType
                              Content = response.Content
                              ContentHash = contentHash
                              ByteLength = int64 response.Content.Length
                              ModifiedAt = modifiedAt
                              Metadata =
                                response.ETag
                                |> Option.map (fun etag -> Map.ofList [ "http.etag", etag ])
                                |> Option.defaultValue Map.empty }

                        let changes =
                            match previous with
                            | Some state when state.ContentHash = contentHash -> []
                            | Some _ -> [ SourceChange.Updated content ]
                            | None -> [ SourceChange.Added content ]

                        let checkpoint =
                            { ItemId = itemId
                              Uri = finalUri
                              ContentHash = contentHash
                              ETag = response.ETag
                              LastModified = response.LastModified }
                            |> HttpSourceCheckpoint.serialize
                            |> SourceConnectorCheckpoint.create HttpSourceCheckpoint.connector

                        return
                            { SourceId = request.Source.Id
                              Connector = HttpSourceCheckpoint.connector
                              StartedAt = request.RequestedAt
                              CompletedAt = DateTimeOffset.UtcNow
                              Changes = changes
                              Failures = []
                              Checkpoint = checkpoint }
                } }
