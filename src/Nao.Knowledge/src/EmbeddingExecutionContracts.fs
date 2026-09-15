namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<Struct>]
type EmbeddingBatchId = private EmbeddingBatchId of string

type EmbeddingInput =
    { ChunkId: KnowledgeChunkId
      ContentHash: ContentHash
      Text: string }

[<RequireQualifiedAccess>]
module EmbeddingInput =
    let ofChunk (chunk: KnowledgeChunk) =
        { ChunkId = chunk.Id
          ContentHash = chunk.ContentHash
          Text = chunk.Text }

[<RequireQualifiedAccess>]
module EmbeddingBatchId =
    let deriveReferences profileId references =
        if isNull (box references) || List.isEmpty references then
            invalidArg (nameof references) "An embedding batch requires at least one input."

        let identity =
            references
            |> List.collect (fun (chunkId, contentHash) ->
                [ KnowledgeChunkId.value chunkId; ContentHash.value contentHash ])
            |> fun parts -> EmbeddingProfileId.value profileId :: parts
            |> String.concat "\u001f"

        EmbeddingBatchId("ebatch_" + (identity |> ContentHash.computeText |> ContentHash.value))

    let derive profileId (inputs: EmbeddingInput list) =
        inputs
        |> List.map (fun input -> input.ChunkId, input.ContentHash)
        |> deriveReferences profileId

    let value (EmbeddingBatchId value) = value

    let tryParse (value: string) =
        if
            not (isNull value)
            && value.StartsWith("ebatch_", StringComparison.Ordinal)
            && value.Length = 71
            && value[7..]
               |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')
        then
            Some(EmbeddingBatchId value)
        else
            None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid canonical embedding-batch ID.")

type EmbeddingProvenanceInput =
    private
        { ChunkId: KnowledgeChunkId
          ContentHash: ContentHash
          CharacterCount: int
          TokenCount: int }

[<RequireQualifiedAccess>]
module EmbeddingProvenanceInput =
    let create chunkId contentHash characterCount tokenCount =
        if characterCount < 0 then
            invalidArg (nameof characterCount) "Embedding provenance character count cannot be negative."

        if tokenCount < 0 then
            invalidArg (nameof tokenCount) "Embedding provenance token count cannot be negative."

        { ChunkId = chunkId
          ContentHash = contentHash
          CharacterCount = characterCount
          TokenCount = tokenCount }

    let chunkId value = value.ChunkId
    let contentHash value = value.ContentHash
    let characterCount value = value.CharacterCount
    let tokenCount value = value.TokenCount

type EmbeddingProvenanceRecord =
    private
        { Version: int
          BatchId: EmbeddingBatchId
          ProfileId: EmbeddingProfileId
          Inputs: EmbeddingProvenanceInput list
          ProviderInputTokens: int
          EstimatedCostUsd: decimal
          ProviderAttempts: int
          StartedAt: DateTimeOffset
          CompletedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module EmbeddingProvenanceRecord =
    let currentVersion = 1

    let private build
        batchId
        profileId
        inputs
        providerInputTokens
        estimatedCostUsd
        providerAttempts
        startedAt
        completedAt
        =
        if isNull (box inputs) || List.isEmpty inputs then
            invalidArg (nameof inputs) "Embedding provenance requires at least one input."

        if
            (inputs |> List.map EmbeddingProvenanceInput.chunkId |> Set.ofList).Count
            <> inputs.Length
        then
            invalidArg (nameof inputs) "Embedding provenance cannot contain duplicate chunk identities."

        let expectedBatchId =
            inputs
            |> List.map (fun input ->
                EmbeddingProvenanceInput.chunkId input, EmbeddingProvenanceInput.contentHash input)
            |> EmbeddingBatchId.deriveReferences profileId

        if batchId <> expectedBatchId then
            invalidArg (nameof batchId) "Embedding provenance batch identity does not match its ordered inputs."

        if providerInputTokens < 0 || estimatedCostUsd < 0m then
            invalidArg (nameof providerInputTokens) "Embedding provenance usage cannot be negative."

        if providerAttempts <= 0 then
            invalidArg (nameof providerAttempts) "Embedding provenance requires at least one provider attempt."

        if completedAt < startedAt then
            invalidArg (nameof completedAt) "Embedding provenance completion precedes its start."

        { Version = currentVersion
          BatchId = batchId
          ProfileId = profileId
          Inputs = inputs
          ProviderInputTokens = providerInputTokens
          EstimatedCostUsd = estimatedCostUsd
          ProviderAttempts = providerAttempts
          StartedAt = startedAt
          CompletedAt = completedAt }

    let create batchId profileId inputs providerInputTokens estimatedCostUsd providerAttempts startedAt completedAt =
        build batchId profileId inputs providerInputTokens estimatedCostUsd providerAttempts startedAt completedAt

    let restore
        version
        batchId
        profileId
        inputs
        providerInputTokens
        estimatedCostUsd
        providerAttempts
        startedAt
        completedAt
        =
        if version <> currentVersion then
            invalidArg (nameof version) "Unsupported embedding provenance version."

        build batchId profileId inputs providerInputTokens estimatedCostUsd providerAttempts startedAt completedAt

    let version value = value.Version
    let batchId value = value.BatchId
    let profileId value = value.ProfileId
    let inputs value = value.Inputs
    let providerInputTokens value = value.ProviderInputTokens
    let estimatedCostUsd value = value.EstimatedCostUsd
    let providerAttempts value = value.ProviderAttempts
    let startedAt value = value.StartedAt
    let completedAt value = value.CompletedAt

type EmbeddingProvenanceRecorder =
    { RecordAsync: AuthorizationScope -> EmbeddingProvenanceRecord -> CancellationToken -> Task }

type EmbeddingProviderBatch =
    { Id: EmbeddingBatchId
      Inputs: EmbeddingInput list }

type GeneratedEmbedding =
    { ChunkId: KnowledgeChunkId
      ContentHash: ContentHash
      ProfileId: EmbeddingProfileId
      Values: float array
      GeneratedAt: DateTimeOffset }

type EmbeddingProviderUsage =
    { InputTokens: int
      EstimatedCostUsd: decimal }

type EmbeddingProviderResponse =
    { Embeddings: GeneratedEmbedding list
      Usage: EmbeddingProviderUsage }

type KnowledgeEmbeddingProvider =
    { Descriptor: EmbeddingCapabilityDescriptor
      CountTokens: string -> int
      EmbedBatchAsync:
          AuthorizationScope -> EmbeddingProviderBatch -> CancellationToken -> Task<EmbeddingProviderResponse>
      Dispose: unit -> unit }

[<RequireQualifiedAccess>]
type EmbeddingRetryPolicy =
    | None
    | Fixed of maxRetries: int * delay: TimeSpan
    | Exponential of maxRetries: int * initialDelay: TimeSpan * maximumDelay: TimeSpan

type EmbeddingThrottleRequest =
    { BatchId: EmbeddingBatchId
      ProfileId: EmbeddingProfileId
      InputCount: int
      InputTokens: int
      Attempt: int }

type EmbeddingThrottle =
    { AcquireAsync: AuthorizationScope -> EmbeddingThrottleRequest -> CancellationToken -> Task }

type EmbeddingCostRecord =
    { Version: int
      BatchId: EmbeddingBatchId
      ProfileId: EmbeddingProfileId
      ChunkIds: KnowledgeChunkId list
      InputTokens: int
      EstimatedCostUsd: decimal
      ProviderAttempts: int
      CompletedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module EmbeddingCostRecord =
    let currentVersion = 1

type EmbeddingCostAccountant =
    { RecordAsync: AuthorizationScope -> EmbeddingCostRecord -> CancellationToken -> Task }

type EmbeddingExecutionConfig =
    { Retry: EmbeddingRetryPolicy
      MaximumInputsPerExecution: int
      Throttle: EmbeddingThrottle
      CostAccountant: EmbeddingCostAccountant
      ProvenanceRecorder: EmbeddingProvenanceRecorder
      DelayAsync: TimeSpan -> CancellationToken -> Task
      Clock: unit -> DateTimeOffset }

type EmbeddingExecutionRequest =
    { Authorization: AuthorizationScope
      Inputs: EmbeddingInput list }

type EmbeddingExecutionMetrics =
    { InputCount: int
      BatchCount: int
      ProviderAttempts: int
      InputTokens: int
      EstimatedCostUsd: decimal }

type EmbeddingExecutionResult =
    { Embeddings: GeneratedEmbedding list
      Metrics: EmbeddingExecutionMetrics }

type EmbeddingExecutor =
    { Descriptor: EmbeddingCapabilityDescriptor
      MaximumInputsPerExecution: int
      EmbedAsync: EmbeddingExecutionRequest -> CancellationToken -> Task<EmbeddingExecutionResult> }
