namespace Nao.Knowledge

open System

[<Struct>]
type EmbeddingContractVersion = private EmbeddingContractVersion of int

[<RequireQualifiedAccess>]
module EmbeddingContractVersion =
    let current = EmbeddingContractVersion 1
    let value (EmbeddingContractVersion value) = value

    let parse value =
        if value = 1 then
            current
        else
            invalidArg (nameof value) "Unsupported embedding contract version."

[<Struct>]
type EmbeddingProfileId = private EmbeddingProfileId of string

[<RequireQualifiedAccess>]
module EmbeddingProfileId =
    let internal derive value =
        EmbeddingProfileId("eprof_" + (value |> ContentHash.computeText |> ContentHash.value))

    let value (EmbeddingProfileId value) = value

    let tryParse (value: string) =
        if
            not (isNull value)
            && value.StartsWith("eprof_", StringComparison.Ordinal)
            && value.Length = 70
            && value[6..]
               |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')
        then
            Some(EmbeddingProfileId value)
        else
            None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid canonical embedding-profile ID.")

[<RequireQualifiedAccess>]
type EmbeddingNormalization =
    | None
    | UnitLength

[<RequireQualifiedAccess>]
type VectorDistanceMetric =
    | Cosine
    | DotProduct
    | Euclidean

type EmbeddingCapabilityLimits =
    { MaximumBatchSize: int
      MaximumInputCharactersPerItem: int
      MaximumInputTokensPerItem: int
      MaximumInputTokensPerBatch: int }

[<RequireQualifiedAccess>]
module EmbeddingCapabilityLimits =
    let create maximumBatchSize maximumInputCharactersPerItem maximumInputTokensPerItem maximumInputTokensPerBatch =
        let positive name value =
            if value <= 0 then
                invalidArg name "Embedding capability limits must be positive."

            value

        let limits =
            { MaximumBatchSize = positive (nameof maximumBatchSize) maximumBatchSize
              MaximumInputCharactersPerItem =
                positive (nameof maximumInputCharactersPerItem) maximumInputCharactersPerItem
              MaximumInputTokensPerItem = positive (nameof maximumInputTokensPerItem) maximumInputTokensPerItem
              MaximumInputTokensPerBatch = positive (nameof maximumInputTokensPerBatch) maximumInputTokensPerBatch }

        if limits.MaximumInputTokensPerBatch < limits.MaximumInputTokensPerItem then
            invalidArg
                (nameof maximumInputTokensPerBatch)
                "The batch token limit must accommodate at least one maximum-size input."

        limits

type EmbeddingCapabilityDescriptor =
    { ContractVersion: EmbeddingContractVersion
      ProfileId: EmbeddingProfileId
      Model: ComponentVersion
      Dimensions: int
      Normalization: EmbeddingNormalization
      DistanceMetric: VectorDistanceMetric
      Preprocessing: ComponentVersion
      Limits: EmbeddingCapabilityLimits }

[<RequireQualifiedAccess>]
module EmbeddingCapabilityDescriptor =
    let private normalizationKey =
        function
        | EmbeddingNormalization.None -> "none"
        | EmbeddingNormalization.UnitLength -> "unit-length"

    let private distanceMetricKey =
        function
        | VectorDistanceMetric.Cosine -> "cosine"
        | VectorDistanceMetric.DotProduct -> "dot-product"
        | VectorDistanceMetric.Euclidean -> "euclidean"

    let private build model dimensions normalization distanceMetric preprocessing limits =
        if dimensions <= 0 then
            invalidArg (nameof dimensions) "Embedding dimensions must be positive."

        let model = ComponentVersion.create model.ComponentId model.Version

        let preprocessing =
            ComponentVersion.create preprocessing.ComponentId preprocessing.Version

        let limits =
            EmbeddingCapabilityLimits.create
                limits.MaximumBatchSize
                limits.MaximumInputCharactersPerItem
                limits.MaximumInputTokensPerItem
                limits.MaximumInputTokensPerBatch

        let contractVersion = EmbeddingContractVersion.current

        let profileId =
            String.concat
                "\u001f"
                [ string (EmbeddingContractVersion.value contractVersion)
                  model.ComponentId
                  model.Version
                  string dimensions
                  normalizationKey normalization
                  distanceMetricKey distanceMetric
                  preprocessing.ComponentId
                  preprocessing.Version
                  string limits.MaximumBatchSize
                  string limits.MaximumInputCharactersPerItem
                  string limits.MaximumInputTokensPerItem
                  string limits.MaximumInputTokensPerBatch ]
            |> EmbeddingProfileId.derive

        { ContractVersion = contractVersion
          ProfileId = profileId
          Model = model
          Dimensions = dimensions
          Normalization = normalization
          DistanceMetric = distanceMetric
          Preprocessing = preprocessing
          Limits = limits }

    let create model dimensions normalization distanceMetric preprocessing limits =
        build model dimensions normalization distanceMetric preprocessing limits

    let validate descriptor =
        if descriptor.ContractVersion <> EmbeddingContractVersion.current then
            invalidArg (nameof descriptor) "Unsupported embedding capability contract version."

        let expected =
            build
                descriptor.Model
                descriptor.Dimensions
                descriptor.Normalization
                descriptor.DistanceMetric
                descriptor.Preprocessing
                descriptor.Limits

        if descriptor <> expected then
            invalidArg (nameof descriptor) "Embedding capability metadata does not match its stable profile identity."

        descriptor

type KnowledgeIndexEntry =
    { SchemaVersion: KnowledgeSchemaVersion
      Id: KnowledgeIndexEntryId
      IndexName: string
      ChunkId: KnowledgeChunkId
      DocumentVersionId: KnowledgeDocumentVersionId
      ContentHash: ContentHash
      Embedding: EmbeddingCapabilityDescriptor option
      IndexedAt: DateTimeOffset
      Derivation: DerivationStep list
      Lifecycle: KnowledgeLifecycle }

[<RequireQualifiedAccess>]
module KnowledgeIndexEntry =
    let create indexName (chunk: KnowledgeChunk) embedding indexedAt derivation =
        if String.IsNullOrWhiteSpace indexName || indexName <> indexName.Trim() then
            invalidArg (nameof indexName) "Index name must be non-blank and have no surrounding whitespace."

        let embedding = embedding |> Option.map EmbeddingCapabilityDescriptor.validate

        { SchemaVersion = KnowledgeSchemaVersion.current
          Id = KnowledgeIndexEntryId.derive indexName chunk.Id
          IndexName = indexName
          ChunkId = chunk.Id
          DocumentVersionId = chunk.DocumentVersionId
          ContentHash = chunk.ContentHash
          Embedding = embedding
          IndexedAt = indexedAt
          Derivation = derivation
          Lifecycle = chunk.Lifecycle }
