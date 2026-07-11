namespace Nao.Agents

open System
open System.Threading.Tasks

/// Circuit breaker state
[<RequireQualifiedAccess>]
type CircuitState =
    | Closed
    | Open of until: DateTimeOffset
    | HalfOpen

/// Circuit breaker configuration
type CircuitBreakerConfig =
    { /// Number of failures before opening the circuit
      FailureThreshold: int
      /// Duration the circuit stays open before transitioning to half-open
      OpenDuration: TimeSpan
      /// Number of successes in half-open state to close the circuit
      SuccessThreshold: int }

    static member Default =
        { FailureThreshold = 5
          OpenDuration = TimeSpan.FromSeconds 30.0
          SuccessThreshold = 2 }

/// Fallback strategy when primary execution fails
[<RequireQualifiedAccess>]
type FallbackStrategy =
    /// Return a default value
    | DefaultValue of string
    /// Try an alternative provider/agent
    | Alternative of execute: (string -> Task<string>)
    /// Return cached result if available
    | Cached
    /// No fallback — propagate the error
    | None

/// Resilience configuration combining retry, circuit breaker, and fallback
type ResilienceConfig =
    { RetryPolicy: RetryPolicy
      CircuitBreaker: CircuitBreakerConfig option
      Fallback: FallbackStrategy
      /// Timeout per individual attempt
      AttemptTimeout: TimeSpan option }

    static member Default =
        { RetryPolicy = RetryPolicy.ExponentialBackoff (3, 1000, 30000)
          CircuitBreaker = Some CircuitBreakerConfig.Default
          Fallback = FallbackStrategy.None
          AttemptTimeout = None }

    static member NoResilience =
        { RetryPolicy = RetryPolicy.None
          CircuitBreaker = None
          Fallback = FallbackStrategy.None
          AttemptTimeout = None }

/// Mutable circuit breaker state tracker
type CircuitBreaker(config: CircuitBreakerConfig) =
    let mutable failures = 0
    let mutable successes = 0
    let mutable state = CircuitState.Closed
    let mutable openedAt = DateTimeOffset.MinValue

    member _.State = state

    member _.RecordSuccess() =
        match state with
        | CircuitState.HalfOpen ->
            successes <- successes + 1
            if successes >= config.SuccessThreshold then
                state <- CircuitState.Closed
                failures <- 0
                successes <- 0
        | CircuitState.Closed ->
            failures <- 0
        | _ -> ()

    member _.RecordFailure() =
        match state with
        | CircuitState.Closed ->
            failures <- failures + 1
            if failures >= config.FailureThreshold then
                state <- CircuitState.Open (DateTimeOffset.UtcNow + config.OpenDuration)
                openedAt <- DateTimeOffset.UtcNow
        | CircuitState.HalfOpen ->
            state <- CircuitState.Open (DateTimeOffset.UtcNow + config.OpenDuration)
            successes <- 0
        | _ -> ()

    member _.CanExecute() =
        match state with
        | CircuitState.Closed -> true
        | CircuitState.HalfOpen -> true
        | CircuitState.Open until ->
            if DateTimeOffset.UtcNow >= until then
                state <- CircuitState.HalfOpen
                successes <- 0
                true
            else false

/// Resilience module for executing with retry, circuit breaker, and fallback
module Resilience =

    let private getDelay (policy: RetryPolicy) (attempt: int) : int =
        match policy with
        | RetryPolicy.None -> 0
        | RetryPolicy.Fixed (_, delayMs) -> delayMs
        | RetryPolicy.ExponentialBackoff (_, initialDelayMs, maxDelayMs) ->
            min maxDelayMs (initialDelayMs * (pown 2 attempt))
        | RetryPolicy.Custom (_, getDelay) -> getDelay attempt

    let private shouldRetry (policy: RetryPolicy) (attempt: int) (ex: exn) : bool =
        match policy with
        | RetryPolicy.None -> false
        | RetryPolicy.Fixed (maxRetries, _) -> attempt < maxRetries
        | RetryPolicy.ExponentialBackoff (maxRetries, _, _) -> attempt < maxRetries
        | RetryPolicy.Custom (shouldRetry, _) -> shouldRetry attempt ex

    /// Execute with resilience policies applied
    let executeAsync (config: ResilienceConfig) (breaker: CircuitBreaker option) (execute: string -> Task<string>) (input: string) : Task<Result<string, string>> =
        task {
            // Check circuit breaker
            match breaker with
            | Some cb when not (cb.CanExecute()) ->
                match config.Fallback with
                | FallbackStrategy.DefaultValue v -> return Ok v
                | FallbackStrategy.Alternative alt ->
                    let! result = alt input
                    return Ok result
                | _ -> return Error "Circuit breaker is open"
            | _ ->
                let mutable attempt = 0
                let mutable lastError = ""
                let mutable success = false
                let mutable result = ""
                let mutable finalError : string option = None

                while not success && finalError.IsNone do
                    try
                        let! output = execute input
                        result <- output
                        success <- true
                        breaker |> Option.iter (fun cb -> cb.RecordSuccess())
                    with ex ->
                        lastError <- ex.Message
                        breaker |> Option.iter (fun cb -> cb.RecordFailure())
                        if shouldRetry config.RetryPolicy attempt ex then
                            let delay = getDelay config.RetryPolicy attempt
                            do! Task.Delay(delay)
                            attempt <- attempt + 1
                        else
                            // Try fallback
                            match config.Fallback with
                            | FallbackStrategy.DefaultValue v ->
                                result <- v
                                success <- true
                            | FallbackStrategy.Alternative alt ->
                                try
                                    let! altResult = alt input
                                    result <- altResult
                                    success <- true
                                with altEx ->
                                    finalError <- Some (sprintf "Primary: %s; Fallback: %s" lastError altEx.Message)
                            | _ ->
                                finalError <- Some lastError

                match finalError with
                | Some err -> return Error err
                | None -> return Ok result
        }
