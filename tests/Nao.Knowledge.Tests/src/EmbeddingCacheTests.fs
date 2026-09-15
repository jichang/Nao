namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type EmbeddingCacheTests() =
    let now = DateTimeOffset(2026, 9, 15, 5, 0, 0, TimeSpan.Zero)

    let authorization tenant =
        SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "cache-user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "cache-workspace") None
            |> Option.get

    let ownerAuthorization = authorization "cache-owner"

    let documentVersionId =
        KnowledgeDocumentVersionId.derive
            (KnowledgeDocumentId.parse "cache-document")
            1
            (ContentHash.computeText "cache-document")

    let profile preprocessingVersion =
        EmbeddingCapabilityDescriptor.create
            (ComponentVersion.create "cache-model" "2026-09")
            2
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            (ComponentVersion.create "cache-preprocessing" preprocessingVersion)
            (EmbeddingCapabilityLimits.create 8 1000 256 1024)

    let input sequence text =
        let contentHash = ContentHash.computeText text

        { ChunkId = KnowledgeChunkId.derive documentVersionId "cache-fixture" sequence contentHash
          ContentHash = contentHash
          Text = text }

    let executor descriptor onCall =
        let value: EmbeddingExecutor =
            { Descriptor = descriptor
              MaximumInputsPerExecution = 10
              EmbedAsync =
                fun request cancellationToken ->
                    task {
                        cancellationToken.ThrowIfCancellationRequested()
                        onCall request

                        let embeddings =
                            request.Inputs
                            |> List.map (fun item ->
                                { ChunkId = item.ChunkId
                                  ContentHash = item.ContentHash
                                  ProfileId = descriptor.ProfileId
                                  Values = [| float item.Text.Length; 1.0 |]
                                  GeneratedAt = now })

                        return
                            { Embeddings = embeddings
                              Metrics =
                                { InputCount = embeddings.Length
                                  BatchCount = if List.isEmpty embeddings then 0 else 1
                                  ProviderAttempts = if List.isEmpty embeddings then 0 else 1
                                  InputTokens = embeddings.Length
                                  EstimatedCostUsd = decimal embeddings.Length / 1000m } }
                    } }

        value

    let run
        (executor: CachedEmbeddingExecutor)
        (authorization: AuthorizationScope)
        (inputs: EmbeddingInput list)
        : CachedEmbeddingExecutionResult =
        executor.EmbedAsync
            { Authorization = authorization
              Inputs = inputs }
            CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Content cache deduplicates equal text across chunks and preserves output identity``() =
        let cache = KnowledgeEmbeddingCaches.inMemory ()
        let descriptor = profile "1"
        let mutable providerCalls = 0
        let mutable providerInputs = []

        let providerExecutor =
            executor descriptor (fun request ->
                providerCalls <- providerCalls + 1
                providerInputs <- request.Inputs :: providerInputs)

        let cached =
            CachedEmbeddingExecution.create cache providerExecutor (fun () -> now.AddMinutes 1.0)

        let inputs = [ input 0 "same text"; input 1 "same text"; input 2 "different" ]
        let first = run cached ownerAuthorization inputs

        Assert.AreEqual(1, providerCalls)
        Assert.AreEqual(2, providerInputs.Head.Length)
        Assert.AreEqual(3, first.Metrics.InputCount)
        Assert.AreEqual(2, first.Metrics.UniqueContentCount)
        Assert.AreEqual(0, first.Metrics.CacheHits)
        Assert.AreEqual(2, first.Metrics.CacheMisses)
        Assert.AreEqual(2, first.Metrics.Provider.InputCount)

        CollectionAssert.AreEqual(
            inputs |> List.map _.ChunkId |> List.toArray,
            first.Embeddings |> List.map _.ChunkId |> List.toArray
        )

        CollectionAssert.AreEqual(first.Embeddings[0].Values, first.Embeddings[1].Values)
        first.Embeddings[0].Values[ 0 ] <- 999.0

        let second = run cached ownerAuthorization inputs
        Assert.AreEqual(1, providerCalls)
        Assert.AreEqual(2, second.Metrics.CacheHits)
        Assert.AreEqual(0, second.Metrics.CacheMisses)
        Assert.AreEqual(0, second.Metrics.Provider.ProviderAttempts)
        CollectionAssert.AreEqual([| 9.0; 1.0 |], second.Embeddings[0].Values)

    [<TestMethod>]
    member _.``Cache partitions by authorization and preprocessing profile``() =
        let cache = KnowledgeEmbeddingCaches.inMemory ()
        let firstProfile = profile "1"
        let secondProfile = profile "2"
        let mutable firstCalls = 0
        let mutable secondCalls = 0

        let first =
            CachedEmbeddingExecution.create
                cache
                (executor firstProfile (fun _ -> firstCalls <- firstCalls + 1))
                (fun () -> now)

        let second =
            CachedEmbeddingExecution.create
                cache
                (executor secondProfile (fun _ -> secondCalls <- secondCalls + 1))
                (fun () -> now)

        let value = input 0 "profile scoped"
        run first ownerAuthorization [ value ] |> ignore
        run first ownerAuthorization [ value ] |> ignore
        run second ownerAuthorization [ value ] |> ignore
        run first (authorization "other-owner") [ value ] |> ignore

        Assert.AreEqual(2, firstCalls)
        Assert.AreEqual(1, secondCalls)

        let firstKey = EmbeddingCacheKey.derive value.ContentHash firstProfile.ProfileId
        let secondKey = EmbeddingCacheKey.derive value.ContentHash secondProfile.ProfileId
        Assert.AreNotEqual(firstKey, secondKey)
        Assert.AreEqual(firstKey, firstKey |> EmbeddingCacheKey.value |> EmbeddingCacheKey.parse)

    [<TestMethod>]
    member _.``Provider failure does not populate cache``() =
        let cache = KnowledgeEmbeddingCaches.inMemory ()
        let descriptor = profile "1"
        let mutable attempts = 0

        let failing: EmbeddingExecutor =
            { Descriptor = descriptor
              MaximumInputsPerExecution = 10
              EmbedAsync =
                fun _ _ ->
                    attempts <- attempts + 1
                    Task.FromException<EmbeddingExecutionResult>(InvalidOperationException "provider failed") }

        let cached = CachedEmbeddingExecution.create cache failing (fun () -> now)

        let value = input 0 "not cached"

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> run cached ownerAuthorization [ value ] |> ignore)
        |> ignore

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> run cached ownerAuthorization [ value ] |> ignore)
        |> ignore

        Assert.AreEqual(2, attempts)

    [<TestMethod>]
    member _.``Cache rejects generated vectors from another profile``() =
        let cache = KnowledgeEmbeddingCaches.inMemory ()
        let expected = profile "1"
        let other = profile "2"

        let dishonest: EmbeddingExecutor =
            { Descriptor = expected
              MaximumInputsPerExecution = 10
              EmbedAsync = (executor other ignore).EmbedAsync }

        let cached = CachedEmbeddingExecution.create cache dishonest (fun () -> now)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            run cached ownerAuthorization [ input 0 "wrong profile" ] |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Cache entry restoration validates durable version key and dimensions``() =
        let descriptor = profile "1"
        let source = input 0 "restored"

        let generated =
            { ChunkId = source.ChunkId
              ContentHash = source.ContentHash
              ProfileId = descriptor.ProfileId
              Values = [| 8.0; 1.0 |]
              GeneratedAt = now }

        let entry =
            EmbeddingCacheEntry.create
                (AuthorizationScope.identity ownerAuthorization)
                descriptor
                generated
                (now.AddMinutes 1.0)

        let restored =
            EmbeddingCacheEntry.restore
                (EmbeddingCacheEntry.version entry)
                (EmbeddingCacheEntry.key entry)
                (EmbeddingCacheEntry.owner entry)
                (EmbeddingCacheEntry.profile entry)
                (EmbeddingCacheEntry.contentHash entry)
                (EmbeddingCacheEntry.values entry)
                (EmbeddingCacheEntry.generatedAt entry)
                (EmbeddingCacheEntry.cachedAt entry)

        CollectionAssert.AreEqual(EmbeddingCacheEntry.values entry, EmbeddingCacheEntry.values restored)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            EmbeddingCacheEntry.restore
                2
                (EmbeddingCacheEntry.key entry)
                (EmbeddingCacheEntry.owner entry)
                descriptor
                generated.ContentHash
                generated.Values
                generated.GeneratedAt
                now
            |> ignore)
        |> ignore
