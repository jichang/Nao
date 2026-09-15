namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type EmbeddingMigrationTests() =
    let now = DateTimeOffset(2026, 9, 16, 5, 0, 0, TimeSpan.Zero)

    let authorization tenant =
        SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "migration-user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "migration-workspace") None
            |> Option.get

    let ownerAuthorization = authorization "migration-owner"

    let documentVersionId =
        KnowledgeDocumentVersionId.derive
            (KnowledgeDocumentId.parse "migration-document")
            1
            (ContentHash.computeText "migration-document")

    let profile modelVersion dimensions =
        EmbeddingCapabilityDescriptor.create
            (ComponentVersion.create "migration-model" modelVersion)
            dimensions
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            (ComponentVersion.create "migration-preprocessing" "1")
            (EmbeddingCapabilityLimits.create 8 1000 256 1024)

    let input sequence text =
        let contentHash = ContentHash.computeText text

        { ChunkId = KnowledgeChunkId.derive documentVersionId "migration-fixture" sequence contentHash
          ContentHash = contentHash
          Text = text }

    let executor descriptor maximumInputs onCall =
        let value: EmbeddingExecutor =
            { Descriptor = descriptor
              MaximumInputsPerExecution = maximumInputs
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
                                  Values =
                                    Array.init descriptor.Dimensions (fun index -> float (item.Text.Length + index))
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

    let cached cache descriptor maximumInputs onCall =
        executor descriptor maximumInputs onCall
        |> fun value -> CachedEmbeddingExecution.create cache value (fun () -> now)

    let embed (executor: CachedEmbeddingExecutor) authorization inputs =
        executor.EmbedAsync
            { Authorization = authorization
              Inputs = inputs }
            CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let migrate (runner: EmbeddingMigrationRunner) source target inputs =
        runner.RunAsync
            { Authorization = ownerAuthorization
              Source = source
              Target = target
              Inputs = inputs
              RequestedAt = now }
            CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let read (reader: EmbeddingUpgradeReader) mode (inputs: EmbeddingInput list) =
        let references: EmbeddingContentReference list =
            inputs
            |> List.map (fun (input: EmbeddingInput) ->
                { ChunkId = input.ChunkId
                  ContentHash = input.ContentHash })

        reader.ResolveAsync
            { Authorization = ownerAuthorization
              Inputs = references
              Mode = mode }
            CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Migration identity is stable for canonical unique content``() =
        let source = profile "1" 2
        let target = profile "2" 3

        let hashes =
            [ ContentHash.computeText "b"; ContentHash.computeText "a" ]
            |> List.sortBy ContentHash.value

        let first =
            EmbeddingMigrationCheckpoint.create
                (AuthorizationScope.identity ownerAuthorization)
                source
                target
                hashes
                now

        let second =
            EmbeddingMigrationCheckpoint.create
                (AuthorizationScope.identity ownerAuthorization)
                source
                target
                hashes
                (now.AddHours 1.0)

        Assert.AreEqual(EmbeddingMigrationCheckpoint.id first, EmbeddingMigrationCheckpoint.id second)

        Assert.AreEqual(
            EmbeddingMigrationCheckpoint.id first,
            first
            |> EmbeddingMigrationCheckpoint.id
            |> EmbeddingMigrationId.value
            |> EmbeddingMigrationId.parse
        )

        let restored =
            EmbeddingMigrationCheckpoint.restore
                (EmbeddingMigrationCheckpoint.version first)
                (EmbeddingMigrationCheckpoint.id first)
                (EmbeddingMigrationCheckpoint.owner first)
                (EmbeddingMigrationCheckpoint.source first)
                (EmbeddingMigrationCheckpoint.target first)
                (EmbeddingMigrationCheckpoint.planHash first)
                (EmbeddingMigrationCheckpoint.contentHashes first)
                (EmbeddingMigrationCheckpoint.nextContentIndex first)
                (EmbeddingMigrationCheckpoint.revision first)
                (EmbeddingMigrationCheckpoint.startedAt first)
                (EmbeddingMigrationCheckpoint.updatedAt first)
                (EmbeddingMigrationCheckpoint.completedAt first)

        Assert.AreEqual(first, restored)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            EmbeddingMigrationCheckpoint.create
                (AuthorizationScope.identity ownerAuthorization)
                source
                target
                (List.rev hashes)
                now
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Migration resumes after target cache persistence without repeating provider work``() =
        let cache = KnowledgeEmbeddingCaches.inMemory ()
        let innerStore = KnowledgeEmbeddingMigrationStores.inMemory ()
        let source = profile "1" 2
        let target = profile "2" 3
        let inputs = [ input 0 "alpha"; input 1 "bravo"; input 2 "charlie" ]
        let sourceExecutor = cached cache source 10 ignore
        embed sourceExecutor ownerAuthorization inputs |> ignore

        let mutable targetCalls = 0

        let targetExecutor = cached cache target 2 (fun _ -> targetCalls <- targetCalls + 1)

        let mutable failCheckpoint = true

        let interruptingStore: KnowledgeEmbeddingMigrationStore =
            { LoadAsync = innerStore.LoadAsync
              SaveAsync =
                fun authorization expected checkpoint cancellationToken ->
                    if failCheckpoint then
                        failCheckpoint <- false
                        Task.FromException(InvalidOperationException "checkpoint unavailable")
                    else
                        innerStore.SaveAsync authorization expected checkpoint cancellationToken }

        let runner =
            EmbeddingMigration.create cache interruptingStore (fun () -> now.AddMinutes 1.0)

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> migrate runner source targetExecutor inputs |> ignore)
        |> ignore

        let outcome = migrate runner source targetExecutor inputs

        Assert.AreEqual(2, targetCalls)
        Assert.AreEqual(3, outcome.MigratedContentCount)
        Assert.AreEqual(2, EmbeddingMigrationCheckpoint.revision outcome.Checkpoint)
        Assert.IsTrue(EmbeddingMigrationCheckpoint.completedAt outcome.Checkpoint |> Option.isSome)
        Assert.AreEqual(0, outcome.ExecutionMetrics.Head.CacheMisses)
        Assert.AreEqual(2, outcome.ExecutionMetrics.Head.CacheHits)

    [<TestMethod>]
    member _.``Migration refuses incomplete source profile``() =
        let cache = KnowledgeEmbeddingCaches.inMemory ()
        let source = profile "1" 2
        let target = profile "2" 3
        let inputs = [ input 0 "present"; input 1 "missing" ]

        embed (cached cache source 10 ignore) ownerAuthorization [ inputs.Head ]
        |> ignore

        let runner =
            EmbeddingMigration.create cache (KnowledgeEmbeddingMigrationStores.inMemory ()) (fun () -> now)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            migrate runner source (cached cache target 10 ignore) inputs |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Target-first routing returns one complete profile and falls back as a unit``() =
        let cache = KnowledgeEmbeddingCaches.inMemory ()
        let source = profile "1" 2
        let target = profile "2" 3
        let inputs = [ input 0 "first"; input 1 "second" ]
        embed (cached cache source 10 ignore) ownerAuthorization inputs |> ignore

        embed (cached cache target 10 ignore) ownerAuthorization [ inputs.Head ]
        |> ignore

        let reader = EmbeddingUpgradeReads.create cache source target
        let fallback = read reader EmbeddingReadMode.TargetFirst inputs

        Assert.AreEqual(source.ProfileId, fallback.Profile.ProfileId)
        Assert.IsTrue(fallback.Embeddings |> List.forall (fun value -> value.Values.Length = 2))

        embed (cached cache target 10 ignore) ownerAuthorization inputs |> ignore
        let upgraded = read reader EmbeddingReadMode.TargetFirst inputs

        Assert.AreEqual(target.ProfileId, upgraded.Profile.ProfileId)
        Assert.IsTrue(upgraded.Embeddings |> List.forall (fun value -> value.Values.Length = 3))

    [<TestMethod>]
    member _.``Explicit cutover fails closed when a target vector is missing``() =
        let cache = KnowledgeEmbeddingCaches.inMemory ()
        let source = profile "1" 2
        let target = profile "2" 3
        let inputs = [ input 0 "first"; input 1 "second" ]
        embed (cached cache source 10 ignore) ownerAuthorization inputs |> ignore
        let reader = EmbeddingUpgradeReads.create cache source target

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            read reader EmbeddingReadMode.TargetOnly inputs |> ignore)
        |> ignore

        let sourceResult = read reader EmbeddingReadMode.SourceOnly inputs
        Assert.AreEqual(source.ProfileId, sourceResult.Profile.ProfileId)

    [<TestMethod>]
    member _.``Upgrade reads expand shared content without duplicate cache lookups``() =
        let cache = KnowledgeEmbeddingCaches.inMemory ()
        let source = profile "1" 2
        let target = profile "2" 3
        let inputs = [ input 0 "shared"; input 1 "shared" ]
        embed (cached cache source 10 ignore) ownerAuthorization inputs |> ignore

        let result =
            read (EmbeddingUpgradeReads.create cache source target) EmbeddingReadMode.SourceOnly inputs

        Assert.AreEqual(2, result.Embeddings.Length)

        CollectionAssert.AreEqual(
            inputs |> List.map _.ChunkId |> List.toArray,
            result.Embeddings |> List.map _.ChunkId |> List.toArray
        )

    [<TestMethod>]
    member _.``Migration state and cache are isolated by authorization identity``() =
        let cache = KnowledgeEmbeddingCaches.inMemory ()
        let store = KnowledgeEmbeddingMigrationStores.inMemory ()
        let source = profile "1" 2
        let target = profile "2" 3
        let inputs = [ input 0 "private" ]
        embed (cached cache source 10 ignore) ownerAuthorization inputs |> ignore
        let runner = EmbeddingMigration.create cache store (fun () -> now)
        let completed = migrate runner source (cached cache target 10 ignore) inputs
        let otherAuthorization = authorization "other-migration-owner"

        let loaded =
            store.LoadAsync
                otherAuthorization
                (EmbeddingMigrationCheckpoint.id completed.Checkpoint)
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.IsTrue(loaded.IsNone)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            runner.RunAsync
                { Authorization = otherAuthorization
                  Source = source
                  Target = cached cache target 10 ignore
                  Inputs = inputs
                  RequestedAt = now }
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore
