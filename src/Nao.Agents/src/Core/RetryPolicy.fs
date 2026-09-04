namespace Nao.Agents

/// Retry policy for resilient execution across lifecycle and observability flows.
[<RequireQualifiedAccess>]
type RetryPolicy =
    /// No retries
    | None
    /// Fixed delay between retries
    | Fixed of maxRetries: int * delayMs: int
    /// Exponential backoff
    | ExponentialBackoff of maxRetries: int * initialDelayMs: int * maxDelayMs: int
    /// Custom retry logic
    | Custom of shouldRetry: (int -> exn -> bool) * getDelay: (int -> int)
