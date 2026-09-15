namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<Struct>]
type EmbeddingCacheKey = private EmbeddingCacheKey of string

[<RequireQualifiedAccess>]
module EmbeddingCacheKey =
    let derive contentHash profileId =
        String.concat "\u001f" [ ContentHash.value contentHash; EmbeddingProfileId.value profileId ]
        |> ContentHash.computeText
        |> ContentHash.value
        |> fun hash -> EmbeddingCacheKey("ecache_" + hash)

    let value (EmbeddingCacheKey value) = value

    let tryParse (value: string) =
        if
            not (isNull value)
            && value.StartsWith("ecache_", StringComparison.Ordinal)
            && value.Length = 71
            && value[7..]
               |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')
        then
            Some(EmbeddingCacheKey value)
        else
            None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid canonical embedding-cache key.")

type EmbeddingCacheEntry =
    private
        { Version: int
          Key: EmbeddingCacheKey
          Owner: AuthorizationIdentity
          ContentHash: ContentHash
          Profile: EmbeddingCapabilityDescriptor
          Values: float array
          GeneratedAt: DateTimeOffset
          CachedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module EmbeddingCacheEntry =
    let currentVersion = 1

    let private build
        (owner: AuthorizationIdentity)
        (profile: EmbeddingCapabilityDescriptor)
        (contentHash: ContentHash)
        (values: float array)
        (generatedAt: DateTimeOffset)
        (cachedAt: DateTimeOffset)
        =
        let profile = EmbeddingCapabilityDescriptor.validate profile

        if isNull values || values.Length <> profile.Dimensions then
            invalidArg (nameof values) "Cached embedding dimensions do not match the embedding profile."

        if values |> Array.exists (Double.IsFinite >> not) then
            invalidArg (nameof values) "Cached embedding contains a non-finite value."

        { Version = currentVersion
          Key = EmbeddingCacheKey.derive contentHash profile.ProfileId
          Owner = owner
          ContentHash = contentHash
          Profile = profile
          Values = Array.copy values
          GeneratedAt = generatedAt
          CachedAt = cachedAt }

    let create owner (profile: EmbeddingCapabilityDescriptor) (generated: GeneratedEmbedding) cachedAt =
        if generated.ProfileId <> profile.ProfileId then
            invalidArg (nameof generated) "Generated embedding does not match the cache profile."

        build owner profile generated.ContentHash generated.Values generated.GeneratedAt cachedAt

    let restore version key owner profile contentHash values generatedAt cachedAt =
        if version <> currentVersion then
            invalidArg (nameof version) "Unsupported embedding-cache entry version."

        let restored = build owner profile contentHash values generatedAt cachedAt

        if restored.Key <> key then
            invalidArg (nameof key) "Embedding-cache key does not match its content and profile."

        restored

    let version value = value.Version
    let key value = value.Key
    let owner value = value.Owner
    let contentHash value = value.ContentHash
    let profile value = value.Profile
    let values value = Array.copy value.Values
    let generatedAt value = value.GeneratedAt
    let cachedAt value = value.CachedAt

    let internal clone (value: EmbeddingCacheEntry) =
        { value with
            Values = Array.copy value.Values }

    let internal equivalent first second =
        first.Key = second.Key
        && first.Owner = second.Owner
        && first.ContentHash = second.ContentHash
        && first.Profile = second.Profile
        && first.Values = second.Values

type KnowledgeEmbeddingCache =
    { GetManyAsync:
        AuthorizationScope
            -> EmbeddingCacheKey list
            -> CancellationToken
            -> Task<Map<EmbeddingCacheKey, EmbeddingCacheEntry>>
      SaveBatchAsync: AuthorizationScope -> EmbeddingCacheEntry list -> CancellationToken -> Task }

type CachedEmbeddingExecutionMetrics =
    { InputCount: int
      UniqueContentCount: int
      CacheHits: int
      CacheMisses: int
      Provider: EmbeddingExecutionMetrics }

type CachedEmbeddingExecutionResult =
    { Embeddings: GeneratedEmbedding list
      Metrics: CachedEmbeddingExecutionMetrics }

type CachedEmbeddingExecutor =
    { Descriptor: EmbeddingCapabilityDescriptor
      MaximumInputsPerExecution: int
      EmbedAsync: EmbeddingExecutionRequest -> CancellationToken -> Task<CachedEmbeddingExecutionResult> }
