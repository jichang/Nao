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

/// Structured failure representation shared by versioned platform boundaries.
type PlatformFailure =
    { Category: PlatformErrorCategory
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
          Message = message
          Retryable = retryable
          CorrelationId = correlationId }

    /// Classify an HTTP response consistently at a provider boundary.
    let fromHttpStatus correlationId statusCode message =
        let category, retryable =
            match statusCode with
            | 400
            | 422 -> PlatformErrorCategory.InvalidInput, false
            | 401
            | 403 -> PlatformErrorCategory.PermissionDenied, false
            | 408
            | 429 -> PlatformErrorCategory.ResourceExhausted, true
            | 404 -> PlatformErrorCategory.PermanentDependency, false
            | status when status >= 500 -> PlatformErrorCategory.TransientDependency, true
            | _ -> PlatformErrorCategory.PermanentDependency, false

        create category message retryable correlationId

    /// Raise a structured failure through a task-based exception channel.
    let raiseException failure =
        raise (PlatformFailureException failure)

    /// Classify an exception consistently at any public platform boundary.
    let fromException boundary correlationId (error: exn) =
        match error with
        | PlatformFailureException failure ->
            { failure with
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

            create category error.Message retryable correlationId
