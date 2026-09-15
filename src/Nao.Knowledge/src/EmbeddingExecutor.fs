namespace Nao.Knowledge

open System
open System.Threading
open Nao.Agents

[<RequireQualifiedAccess>]
module EmbeddingExecution =
    let private invalidOutput message =
        PlatformFailure.create PlatformErrorCategory.InvalidOutput message false None
        |> PlatformFailure.raiseException

    let private validateRetry =
        function
        | EmbeddingRetryPolicy.None -> ()
        | EmbeddingRetryPolicy.Fixed(maxRetries, delay) ->
            if maxRetries < 0 || delay < TimeSpan.Zero then
                invalidArg "Retry" "Fixed retry count and delay cannot be negative."
        | EmbeddingRetryPolicy.Exponential(maxRetries, initialDelay, maximumDelay) ->
            if maxRetries < 0 || initialDelay < TimeSpan.Zero || maximumDelay < initialDelay then
                invalidArg "Retry" "Exponential retry values are invalid."

    let private retryDelay policy retriesCompleted =
        match policy with
        | EmbeddingRetryPolicy.None -> None
        | EmbeddingRetryPolicy.Fixed(maxRetries, delay) -> if retriesCompleted < maxRetries then Some delay else None
        | EmbeddingRetryPolicy.Exponential(maxRetries, initialDelay, maximumDelay) ->
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

    let private prepareInputs
        (descriptor: EmbeddingCapabilityDescriptor)
        (countTokens: string -> int)
        (maximumInputs: int)
        (inputs: EmbeddingInput list)
        =
        if isNull (box inputs) then
            nullArg (nameof inputs)

        if inputs.Length > maximumInputs then
            invalidArg (nameof inputs) $"Embedding execution exceeds the configured {maximumInputs}-input limit."

        if (inputs |> List.map _.ChunkId |> Set.ofList).Count <> inputs.Length then
            invalidArg (nameof inputs) "Embedding inputs cannot contain duplicate chunks."

        inputs
        |> List.map (fun (input: EmbeddingInput) ->
            if isNull input.Text then
                invalidArg (nameof inputs) "Embedding input text cannot be null."

            if input.ContentHash <> ContentHash.computeText input.Text then
                invalidArg (nameof inputs) "Embedding input content hash does not match its text."

            if input.Text.Length > descriptor.Limits.MaximumInputCharactersPerItem then
                invalidArg (nameof inputs) "Embedding input exceeds the provider character limit."

            let tokenCount = countTokens input.Text

            if tokenCount < 0 then
                invalidOp "Embedding provider token counting returned a negative value."

            if tokenCount > descriptor.Limits.MaximumInputTokensPerItem then
                invalidArg (nameof inputs) "Embedding input exceeds the provider token limit."

            input, tokenCount)

    let private batch (descriptor: EmbeddingCapabilityDescriptor) (inputs: (EmbeddingInput * int) list) =
        let finish current batches =
            if List.isEmpty current then
                batches
            else
                List.rev current :: batches

        let rec loop current currentTokens batches remaining =
            match remaining with
            | [] -> finish current batches |> List.rev
            | ((_, tokenCount) as input) :: tail ->
                let exceedsCount = current.Length >= descriptor.Limits.MaximumBatchSize

                let exceedsTokens =
                    not (List.isEmpty current)
                    && currentTokens + tokenCount > descriptor.Limits.MaximumInputTokensPerBatch

                if exceedsCount || exceedsTokens then
                    loop [ input ] tokenCount (finish current batches) tail
                else
                    loop (input :: current) (currentTokens + tokenCount) batches tail

        loop [] 0 [] inputs

    let private validateResponse
        (descriptor: EmbeddingCapabilityDescriptor)
        (expectedInputs: (EmbeddingInput * int) list)
        (response: EmbeddingProviderResponse)
        =
        if response.Usage.InputTokens < 0 || response.Usage.EstimatedCostUsd < 0m then
            invalidOutput "Embedding provider returned negative usage."

        if response.Embeddings.Length <> expectedInputs.Length then
            invalidOutput "Embedding provider returned an unexpected number of vectors."

        let expected =
            expectedInputs |> List.map (fun (input, _) -> input.ChunkId) |> Set.ofList

        let actual = response.Embeddings |> List.map _.ChunkId |> Set.ofList

        if actual.Count <> response.Embeddings.Length || actual <> expected then
            invalidOutput "Embedding provider returned duplicate or unexpected chunk identities."

        response.Embeddings
        |> List.iter (fun embedding ->
            let input =
                expectedInputs
                |> List.find (fun (candidate, _) -> candidate.ChunkId = embedding.ChunkId)
                |> fst

            if
                embedding.ContentHash <> input.ContentHash
                || embedding.ProfileId <> descriptor.ProfileId
                || embedding.Values.Length <> descriptor.Dimensions
                || embedding.Values |> Array.exists (Double.IsFinite >> not)
            then
                invalidOutput "Embedding provider returned a vector that does not match the requested capability.")

        let byChunkId =
            response.Embeddings |> List.map (fun item -> item.ChunkId, item) |> Map.ofList

        { response with
            Embeddings = expectedInputs |> List.map (fun (input, _) -> byChunkId[input.ChunkId]) }

    let private executeBatch
        (config: EmbeddingExecutionConfig)
        (provider: KnowledgeEmbeddingProvider)
        (authorization: AuthorizationScope)
        (batchOrdinal: int)
        (inputs: (EmbeddingInput * int) list)
        (cancellationToken: CancellationToken)
        =
        task {
            let startedAt = config.Clock()
            let providerInputs = inputs |> List.map fst
            let batchId = EmbeddingBatchId.derive provider.Descriptor.ProfileId providerInputs
            let preflightInputTokens = inputs |> List.sumBy snd

            let instrumentation =
                ExecutionInstrumentation.beginEmbeddingCall
                    { ProfileId = EmbeddingProfileId.value provider.Descriptor.ProfileId
                      BatchId = EmbeddingBatchId.value batchId
                      InputCount = inputs.Length
                      PreflightInputTokens = preflightInputTokens
                      StartedAt = startedAt }

            try
                let mutable attempts = 0
                let mutable retriesCompleted = 0
                let mutable response: EmbeddingProviderResponse option = None

                while response.IsNone do
                    cancellationToken.ThrowIfCancellationRequested()
                    attempts <- attempts + 1

                    do!
                        config.Throttle.AcquireAsync
                            authorization
                            { BatchId = batchId
                              ProfileId = provider.Descriptor.ProfileId
                              InputCount = inputs.Length
                              InputTokens = preflightInputTokens
                              Attempt = attempts }
                            cancellationToken

                    let! execution =
                        task {
                            try
                                let! value =
                                    provider.EmbedBatchAsync
                                        authorization
                                        { Id = batchId
                                          Inputs = providerInputs }
                                        cancellationToken

                                return Ok(validateResponse provider.Descriptor inputs value)
                            with
                            | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested ->
                                return raise error
                            | error -> return Error error
                        }

                    match execution with
                    | Ok value -> response <- Some value
                    | Error error ->
                        let failure =
                            PlatformFailure.fromException PlatformFailureBoundary.Provider None error

                        match
                            if failure.Retryable then
                                retryDelay config.Retry retriesCompleted
                            else
                                None
                        with
                        | Some delay ->
                            retriesCompleted <- retriesCompleted + 1
                            do! config.DelayAsync delay cancellationToken
                        | None -> PlatformFailure.raiseException failure

                let value = response.Value
                let completedAt = config.Clock()

                let provenanceInputs =
                    inputs
                    |> List.map (fun (input, tokenCount) ->
                        EmbeddingProvenanceInput.create input.ChunkId input.ContentHash input.Text.Length tokenCount)

                let provenance =
                    EmbeddingProvenanceRecord.create
                        batchId
                        provider.Descriptor.ProfileId
                        provenanceInputs
                        value.Usage.InputTokens
                        value.Usage.EstimatedCostUsd
                        attempts
                        startedAt
                        completedAt

                do! config.ProvenanceRecorder.RecordAsync authorization provenance cancellationToken

                do!
                    config.CostAccountant.RecordAsync
                        authorization
                        { Version = EmbeddingCostRecord.currentVersion
                          BatchId = batchId
                          ProfileId = provider.Descriptor.ProfileId
                          ChunkIds = inputs |> List.map (fun (input, _) -> input.ChunkId)
                          InputTokens = value.Usage.InputTokens
                          EstimatedCostUsd = value.Usage.EstimatedCostUsd
                          ProviderAttempts = attempts
                          CompletedAt = completedAt }
                        cancellationToken

                ExecutionInstrumentation.completeEmbeddingCall
                    instrumentation
                    value.Usage.InputTokens
                    value.Usage.EstimatedCostUsd
                    attempts
                    completedAt

                return batchOrdinal, attempts, value
            with error ->
                let failure =
                    PlatformFailure.fromException PlatformFailureBoundary.Provider None error

                ExecutionInstrumentation.failEmbeddingCall instrumentation failure.Category
                return raise error
        }

    let create (provider: KnowledgeEmbeddingProvider) (config: EmbeddingExecutionConfig) =
        if isNull (box provider) then
            nullArg (nameof provider)

        EmbeddingCapabilityDescriptor.validate provider.Descriptor |> ignore
        validateRetry config.Retry

        if config.MaximumInputsPerExecution <= 0 then
            invalidArg (nameof config.MaximumInputsPerExecution) "Maximum inputs per execution must be positive."

        if
            isNull (box config.Throttle)
            || isNull (box config.CostAccountant)
            || isNull (box config.ProvenanceRecorder)
        then
            invalidArg (nameof config) "Embedding throttle, cost accountant, and provenance recorder are required."

        let executor: EmbeddingExecutor =
            { Descriptor = provider.Descriptor
              MaximumInputsPerExecution = config.MaximumInputsPerExecution
              EmbedAsync =
                fun (request: EmbeddingExecutionRequest) (cancellationToken: CancellationToken) ->
                    task {
                        cancellationToken.ThrowIfCancellationRequested()

                        let prepared =
                            prepareInputs
                                provider.Descriptor
                                provider.CountTokens
                                config.MaximumInputsPerExecution
                                request.Inputs

                        let batches = batch provider.Descriptor prepared
                        let results = ResizeArray<int * int * EmbeddingProviderResponse>()

                        for batchOrdinal, inputs in batches |> List.indexed do
                            let! result =
                                executeBatch config provider request.Authorization batchOrdinal inputs cancellationToken

                            results.Add result

                        let ordered = results |> Seq.sortBy (fun (ordinal, _, _) -> ordinal) |> List.ofSeq

                        return
                            { Embeddings = ordered |> List.collect (fun (_, _, response) -> response.Embeddings)
                              Metrics =
                                { InputCount = request.Inputs.Length
                                  BatchCount = batches.Length
                                  ProviderAttempts = ordered |> List.sumBy (fun (_, attempts, _) -> attempts)
                                  InputTokens =
                                    ordered |> List.sumBy (fun (_, _, response) -> response.Usage.InputTokens)
                                  EstimatedCostUsd =
                                    ordered |> List.sumBy (fun (_, _, response) -> response.Usage.EstimatedCostUsd) } }
                    } }

        executor
