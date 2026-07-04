namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Agents

/// Defines a stage in a lifecycle pipeline (issue-to-deployment style)
type PipelineStage =
    { /// Stage name
      Name: string
      /// Description of what this stage does
      Description: string
      /// The agent or function that executes this stage
      Execute: string -> Task<string>
      /// Validation to run after stage completion
      Validate: string -> Task<Result<unit, string>>
      /// Retry policy for this stage (use RetryPolicy.None for no retries)
      Retry: RetryPolicy }

/// Result of a single pipeline stage execution
type StageResult =
    { StageName: string
      Input: string
      Output: string
      Success: bool
      Error: string option
      DurationMs: int64
      RetryCount: int }

/// Full lifecycle pipeline result
type LifecyclePipelineResult =
    { Stages: StageResult list
      FinalOutput: string option
      Success: bool
      TotalDurationMs: int64
      FailedStage: string option }

/// Executes a multi-stage lifecycle pipeline with validation and retry
module LifecyclePipeline =

    /// Get max retries from a retry policy
    let private maxRetries (policy: RetryPolicy) =
        match policy with
        | RetryPolicy.None -> 0
        | RetryPolicy.Fixed (max, _) -> max
        | RetryPolicy.ExponentialBackoff (max, _, _) -> max
        | RetryPolicy.Custom _ -> Int32.MaxValue // custom decides

    /// Get delay for an attempt from a retry policy
    let private getDelay (policy: RetryPolicy) (attempt: int) =
        match policy with
        | RetryPolicy.None -> 0
        | RetryPolicy.Fixed (_, delayMs) -> delayMs
        | RetryPolicy.ExponentialBackoff (_, initialDelayMs, maxDelayMs) ->
            min maxDelayMs (initialDelayMs * (pown 2 attempt))
        | RetryPolicy.Custom (_, getDelay) -> getDelay attempt

    /// Check if should retry
    let private shouldRetry (policy: RetryPolicy) (attempt: int) =
        match policy with
        | RetryPolicy.None -> false
        | RetryPolicy.Fixed (max, _) -> attempt < max
        | RetryPolicy.ExponentialBackoff (max, _, _) -> attempt < max
        | RetryPolicy.Custom (shouldRetry, _) -> shouldRetry attempt (exn "retry")

    /// Run a single stage with retry logic
    let private runStageAsync (stage: PipelineStage) (input: string) : Task<StageResult> =
        task {
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let mutable attempt = 0
            let mutable lastError = None
            let mutable output = ""
            let mutable success = false
            let maxR = maxRetries stage.Retry

            while not success && attempt <= maxR do
                try
                    let! result = stage.Execute input
                    match! stage.Validate result with
                    | Ok () ->
                        output <- result
                        success <- true
                    | Error msg ->
                        lastError <- Some msg
                        if shouldRetry stage.Retry attempt then
                            let delay = getDelay stage.Retry attempt
                            if delay > 0 then do! Task.Delay(delay)
                            attempt <- attempt + 1
                        else
                            attempt <- maxR + 1
                with ex ->
                    lastError <- Some ex.Message
                    if shouldRetry stage.Retry attempt then
                        let delay = getDelay stage.Retry attempt
                        if delay > 0 then do! Task.Delay(delay)
                        attempt <- attempt + 1
                    else
                        attempt <- maxR + 1

            sw.Stop()
            return
                { StageName = stage.Name
                  Input = input
                  Output = output
                  Success = success
                  Error = lastError
                  DurationMs = sw.ElapsedMilliseconds
                  RetryCount = attempt }
        }

    /// Execute the full pipeline, halting on first stage failure
    let executeAsync (stages: PipelineStage list) (initialInput: string) : Task<LifecyclePipelineResult> =
        task {
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let results = ResizeArray<StageResult>()
            let mutable currentInput = initialInput
            let mutable failed = false
            let mutable failedStage = None

            for stage in stages do
                if not failed then
                    let! result = runStageAsync stage currentInput
                    results.Add(result)
                    if result.Success then
                        currentInput <- result.Output
                    else
                        failed <- true
                        failedStage <- Some stage.Name

            sw.Stop()
            return
                { Stages = results |> Seq.toList
                  FinalOutput = if failed then None else Some currentInput
                  Success = not failed
                  TotalDurationMs = sw.ElapsedMilliseconds
                  FailedStage = failedStage }
        }

    /// Create a simple stage from an agent
    let stageFromAgent (name: string) (agent: IAgent) : PipelineStage =
        { Name = name
          Description = agent.Id.Description
          Execute = agent.RunAsync
          Validate = fun _ -> Task.FromResult(Ok ())
          Retry = RetryPolicy.None }

    /// Create a stage with validation and default retry
    let stageWithValidation (name: string) (execute: string -> Task<string>) (validate: string -> Task<Result<unit, string>>) : PipelineStage =
        { Name = name
          Description = name
          Execute = execute
          Validate = validate
          Retry = RetryPolicy.Fixed (2, 100) }
