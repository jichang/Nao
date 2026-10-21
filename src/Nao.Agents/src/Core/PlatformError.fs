namespace Nao.Agents

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

/// Stable platform-level classification for failures crossing public boundaries.
[<RequireQualifiedAccess>]
type PlatformErrorCategory =
    | InvalidInput
    | PermissionDenied
    | NotReady
    | ResourceExhausted
    | TransientDependency
    | PermanentDependency
    | InvalidOutput
    | InternalFailure
    | Cancelled

[<RequireQualifiedAccess>]
type ProviderErrorCategory =
    | Authentication
    | Authorization
    | Quota
    | RateLimit
    | Timeout
    | Overload
    | InvalidRequest
    | ContentRejection
    | Protocol
    | ServerFailure

/// Structured failure representation shared by versioned platform boundaries.
type PlatformFailure =
    { Category: PlatformErrorCategory
      ProviderCategory: ProviderErrorCategory option
      Message: string
      Retryable: bool
      CorrelationId: string option }

/// Typed exception transport for task-based APIs whose successful result has no failure branch.
exception PlatformFailureException of PlatformFailure

/// Public boundary where an unstructured exception entered the platform taxonomy.
[<RequireQualifiedAccess>]
type PlatformFailureBoundary =
    | Agent
    | Tool
    | Provider
    | Storage
    | Host

[<RequireQualifiedAccess>]
module PlatformFailure =
    let create category message retryable correlationId : PlatformFailure =
        { Category = category
          ProviderCategory = None
          Message = SecretRedaction.redact message
          Retryable = retryable
          CorrelationId = correlationId }

    /// Classify an HTTP response consistently at a provider boundary.
    let fromHttpStatus correlationId statusCode message =
        let category, providerCategory, retryable =
            match statusCode with
            | 400
            | 404 -> PlatformErrorCategory.InvalidInput, ProviderErrorCategory.InvalidRequest, false
            | 401 -> PlatformErrorCategory.PermissionDenied, ProviderErrorCategory.Authentication, false
            | 403 -> PlatformErrorCategory.PermissionDenied, ProviderErrorCategory.Authorization, false
            | 408 -> PlatformErrorCategory.TransientDependency, ProviderErrorCategory.Timeout, true
            | 422 -> PlatformErrorCategory.InvalidInput, ProviderErrorCategory.ContentRejection, false
            | 429 -> PlatformErrorCategory.ResourceExhausted, ProviderErrorCategory.RateLimit, true
            | 503 -> PlatformErrorCategory.TransientDependency, ProviderErrorCategory.Overload, true
            | status when status >= 500 ->
                PlatformErrorCategory.TransientDependency, ProviderErrorCategory.ServerFailure, true
            | _ -> PlatformErrorCategory.PermanentDependency, ProviderErrorCategory.Protocol, false

        { Category = category
          ProviderCategory = Some providerCategory
          Message = SecretRedaction.redact message
          Retryable = retryable
          CorrelationId = correlationId }

    /// Raise a structured failure through a task-based exception channel.
    let raiseException failure =
        raise (PlatformFailureException failure)

    /// Classify an exception consistently at any public platform boundary.
    let fromException boundary correlationId (error: exn) =
        match error with
        | PlatformFailureException failure ->
            { failure with
                Message = SecretRedaction.redact failure.Message
                CorrelationId = correlationId |> Option.orElse failure.CorrelationId }
        | _ ->
            let category, retryable =
                match error with
                | :? UnauthorizedAccessException -> PlatformErrorCategory.PermissionDenied, false
                | :? ArgumentException -> PlatformErrorCategory.InvalidInput, false
                | :? JsonException -> PlatformErrorCategory.InvalidOutput, false
                | :? TimeoutException
                | :? TaskCanceledException
                | :? IOException
                | :? HttpRequestException -> PlatformErrorCategory.TransientDependency, true
                | :? OperationCanceledException -> PlatformErrorCategory.Cancelled, false
                | _ ->
                    match boundary with
                    | PlatformFailureBoundary.Provider
                    | PlatformFailureBoundary.Storage -> PlatformErrorCategory.TransientDependency, true
                    | PlatformFailureBoundary.Agent
                    | PlatformFailureBoundary.Tool
                    | PlatformFailureBoundary.Host -> PlatformErrorCategory.InternalFailure, false

            let providerCategory =
                if boundary <> PlatformFailureBoundary.Provider then
                    None
                else
                    match error with
                    | :? TimeoutException
                    | :? TaskCanceledException -> Some ProviderErrorCategory.Timeout
                    | :? JsonException -> Some ProviderErrorCategory.Protocol
                    | :? ArgumentException -> Some ProviderErrorCategory.InvalidRequest
                    | :? HttpRequestException
                    | :? IOException -> Some ProviderErrorCategory.ServerFailure
                    | _ -> Some ProviderErrorCategory.ServerFailure

            { create category error.Message retryable correlationId with
                ProviderCategory = providerCategory }
