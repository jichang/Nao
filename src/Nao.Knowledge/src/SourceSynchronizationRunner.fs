namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

module SourceSynchronizationRunner =
    let private validateRetry =
        function
        | SourceRetryPolicy.None -> ()
        | SourceRetryPolicy.Fixed(maxRetries, delay) ->
            if maxRetries < 0 || delay < TimeSpan.Zero then
                invalidArg "Retry" "Fixed retry count and delay cannot be negative."
        | SourceRetryPolicy.Exponential(maxRetries, initialDelay, maximumDelay) ->
            if maxRetries < 0 || initialDelay < TimeSpan.Zero || maximumDelay < initialDelay then
                invalidArg "Retry" "Exponential retry values are invalid."

    let private retryDelay policy retriesCompleted =
        match policy with
        | SourceRetryPolicy.None -> None
        | SourceRetryPolicy.Fixed(maxRetries, delay) -> if retriesCompleted < maxRetries then Some delay else None
        | SourceRetryPolicy.Exponential(maxRetries, initialDelay, maximumDelay) ->
            if retriesCompleted >= maxRetries then
                None
            else
                let multiplier = 1L <<< min retriesCompleted 30

                let ticks =
                    if initialDelay.Ticks = 0L || multiplier <= maximumDelay.Ticks / initialDelay.Ticks then
                        initialDelay.Ticks * multiplier
                    else
                        maximumDelay.Ticks

                Some(TimeSpan.FromTicks ticks)

    let private quarantineBatch
        (config: SourceResilienceConfig)
        (request: SourceSyncRequest)
        (attempts: int)
        (batch: SourceSyncBatch)
        (cancellationToken: CancellationToken)
        =
        task {
            for failure in batch.Failures do
                let item =
                    { Version = QuarantinedSourceItem.currentVersion
                      Id = SourceFailureId.create ()
                      Owner = AuthorizationScope.identity request.Authorization
                      SourceId = batch.SourceId
                      Connector = batch.Connector
                      Uri = failure.Uri
                      Kind = failure.Kind
                      Message = SecretRedaction.redact failure.Message
                      ConnectorAttempts = failure.Attempts
                      SyncAttempts = attempts
                      QuarantinedAt = config.Clock() }

                do! config.FailureStore.SaveQuarantinedAsync request.Authorization item cancellationToken
        }

    let private deadLetter
        (config: SourceResilienceConfig)
        (connector: PullSourceConnector)
        (request: SourceSyncRequest)
        (failure: PlatformFailure)
        (attempts: int)
        (cancellationToken: CancellationToken)
        =
        task {
            let item =
                { Version = DeadLetteredSourceSync.currentVersion
                  Id = SourceFailureId.create ()
                  Owner = AuthorizationScope.identity request.Authorization
                  SourceId = request.Source.Id
                  Connector = connector.Descriptor.Connector
                  Mode = request.Mode
                  Failure = failure
                  Attempts = attempts
                  FailedAt = config.Clock() }

            do! config.FailureStore.SaveDeadLetteredAsync request.Authorization item cancellationToken

            return SourceSyncOutcome.DeadLettered item
        }

    let private run
        (config: SourceResilienceConfig)
        (connector: PullSourceConnector)
        (request: SourceSyncRequest)
        (cancellationToken: CancellationToken)
        =
        task {
            let mutable attempts = 0
            let mutable retriesCompleted = 0
            let mutable outcome = None

            while outcome.IsNone do
                cancellationToken.ThrowIfCancellationRequested()
                attempts <- attempts + 1

                let! execution =
                    task {
                        try
                            let! batch = connector.SynchronizeAsync request cancellationToken

                            return Ok batch
                        with
                        | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested ->
                            return raise error
                        | error -> return Error error
                    }

                match execution with
                | Ok batch ->
                    do! quarantineBatch config request attempts batch cancellationToken
                    outcome <- Some(SourceSyncOutcome.Completed batch)
                | Error error ->
                    let failure = PlatformFailure.fromException PlatformFailureBoundary.Host None error

                    match
                        if failure.Retryable then
                            retryDelay config.Retry retriesCompleted
                        else
                            None
                    with
                    | Some delay ->
                        retriesCompleted <- retriesCompleted + 1
                        do! config.DelayAsync delay cancellationToken
                    | None ->
                        let! deadLettered = deadLetter config connector request failure attempts cancellationToken

                        outcome <- Some deadLettered

            return outcome.Value
        }

    let create (config: SourceResilienceConfig) =
        validateRetry config.Retry

        if config.MaximumConcurrency <= 0 then
            invalidArg (nameof config.MaximumConcurrency) "Maximum concurrency must be positive."

        if config.MaximumPending < config.MaximumConcurrency then
            invalidArg (nameof config.MaximumPending) "Maximum pending work must be at least maximum concurrency."

        let runOne = run config

        { RunAsync = fun connector request cancellationToken -> runOne connector request cancellationToken
          RunManyAsync =
            fun work cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    if work.Length > config.MaximumPending then
                        invalidOp
                            $"Source synchronization backlog exceeds the configured {config.MaximumPending}-item limit."

                    use concurrency = new SemaphoreSlim(config.MaximumConcurrency)

                    let execute item =
                        task {
                            do! concurrency.WaitAsync cancellationToken

                            try
                                return! runOne item.Connector item.Request cancellationToken
                            finally
                                concurrency.Release() |> ignore
                        }

                    let! outcomes = work |> List.map execute |> Task.WhenAll

                    return outcomes |> List.ofArray
                } }
