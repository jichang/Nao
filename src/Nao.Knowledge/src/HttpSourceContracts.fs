namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks

type HttpSourceRequest =
    { Uri: Uri
      IfNoneMatch: string option
      IfModifiedSince: DateTimeOffset option
      MaximumResponseBytes: int64
      Network: SourceNetworkPolicy }

type HttpSourceResponse =
    { StatusCode: int
      Location: Uri option
      MediaType: string option
      ContentLength: int64 option
      ETag: string option
      LastModified: DateTimeOffset option
      Content: byte array }

type HttpSourceTransport =
    { SendAsync: HttpSourceRequest -> CancellationToken -> Task<HttpSourceResponse> }

type HttpRobotsPolicy =
    { IsAllowedAsync: Uri -> string -> SourceNetworkPolicy -> CancellationToken -> Task<bool> }

type HttpSourceConnectorConfig =
    { UserAgent: string
      MaximumResponseBytes: int64
      MaximumRedirects: int
      AllowedMediaTypes: Set<string>
      Transport: HttpSourceTransport
      Robots: HttpRobotsPolicy }
