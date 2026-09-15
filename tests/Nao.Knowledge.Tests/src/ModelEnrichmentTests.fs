namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type ModelEnrichmentTests() =
    let lifecycle = KnowledgeLifecycle.active None false
    let parser = ComponentVersion.create "parser" "1"
    let chunker = ComponentVersion.create "chunker" "1"
    let chunkerConfig = ContentHash.computeText "chunker-config"
    let documentId = KnowledgeDocumentId.parse "model-enrichment-fixture"

    let documentVersionId =
        KnowledgeDocumentVersionId.derive documentId 1 (ContentHash.computeText "document")

    let authorization =
        SecurityPrincipal.create (TenantId.parse "enrichment-tenant") (UserId.parse "enrichment-user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "enrichment-workspace") None
            |> Option.get

    let unit sequence text =
        ContentUnit.create
            documentVersionId
            sequence
            ContentUnitKind.Paragraph
            text
            (SourceLocation.create
                (int64 (sequence * 100))
                (int64 text.Length)
                (sequence * 100)
                text.Length
                None
                (Some(sequence + 1))
                (Some(sequence + 1))
                (Some $"body/{sequence}"))
            parser
            []
            Map.empty
            lifecycle

    let units =
        [ unit 0 "Nao is an agent platform."
          unit 1 "It provides grounded knowledge retrieval." ]

    let chunk =
        KnowledgeChunk.create
            documentVersionId
            0
            KnowledgeChunkRole.Leaf
            None
            (units |> List.map _.Id)
            (units |> List.map _.Text |> String.concat "\n\n")
            (units |> List.map _.Location)
            chunker
            chunkerConfig
            []
            Map.empty
            lifecycle

    let request (chunks: KnowledgeChunk list) =
        { Authorization = authorization
          DocumentVersionId = documentVersionId
          Units = units
          Chunks = chunks
          EnrichedAt = DateTimeOffset(2026, 9, 15, 1, 0, 0, TimeSpan.Zero) }

    let config (model: KnowledgeEnrichmentModel) =
        { Model = model
          MinimumConfidence = Confidence.create 0.5
          MaximumChunksPerRequest = 4
          MaximumEnrichmentsPerChunk = 8
          MaximumValueCharacters = 200
          MaximumEvidenceItems = 4 }

    let run (enricher: KnowledgeChunkEnricher) request =
        enricher.EnrichAsync request CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let descriptor =
        { Model = ComponentVersion.create "knowledge-enrichment-model" "2026-09"
          ConfigurationHash = ContentHash.computeText "prompt-v1"
          Deterministic = true }

    [<TestMethod>]
    member _.``Model enrichment preserves typed confidence evidence and provenance``() =
        let mutable calls = 0

        let model: KnowledgeEnrichmentModel =
            { Descriptor = descriptor
              EnrichAsync =
                fun scope inputs cancellationToken ->
                    cancellationToken.ThrowIfCancellationRequested()
                    Assert.AreEqual(authorization, scope)
                    calls <- calls + 1
                    let input = inputs |> List.exactlyOne
                    Assert.AreEqual(chunk.Id, input.ChunkId)
                    Assert.AreEqual(units |> List.map _.Id, input.Units |> List.map _.Id)

                    Task.FromResult
                        [ { ChunkId = input.ChunkId
                            Enrichments =
                              [ { Kind = KnowledgeEnrichmentKind.Entity "product"
                                  Value = "Nao"
                                  Confidence = 0.95
                                  Evidence =
                                    [ { ContentUnitId = units[0].Id
                                        Quote = "Nao" } ] }
                                { Kind = KnowledgeEnrichmentKind.Topic
                                  Value = "grounded knowledge retrieval"
                                  Confidence = 0.85
                                  Evidence =
                                    [ { ContentUnitId = units[1].Id
                                        Quote = "grounded knowledge retrieval" } ] }
                                { Kind = KnowledgeEnrichmentKind.Summary
                                  Value = "Nao provides grounded retrieval for agents."
                                  Confidence = 0.9
                                  Evidence =
                                    [ { ContentUnitId = units[0].Id
                                        Quote = "agent platform" }
                                      { ContentUnitId = units[1].Id
                                        Quote = "grounded knowledge retrieval" } ] }
                                { Kind = KnowledgeEnrichmentKind.Topic
                                  Value = "discarded"
                                  Confidence = 0.2
                                  Evidence =
                                    [ { ContentUnitId = units[0].Id
                                        Quote = "platform" } ] } ] } ] }

        let enricher = ModelAssistedEnricher.create (config model)
        let first = run enricher (request [ chunk ])
        let enriched = first.Chunks |> List.exactlyOne

        Assert.AreEqual(1, calls)
        Assert.AreEqual(3, enriched.Enrichments.Length)

        let entity = enriched.Enrichments[0]
        Assert.AreEqual(KnowledgeEnrichmentKind.Entity "product", entity.Kind)
        Assert.AreEqual("Nao", entity.Value)
        Assert.AreEqual(0.95, Confidence.value entity.Confidence, 0.0001)
        Assert.AreEqual(units[0].Location, entity.Evidence[0].Location)
        Assert.AreEqual(ContentHash.computeText "Nao", entity.Evidence[0].QuotedContentHash)
        Assert.AreEqual(descriptor.Model, entity.Generator)
        Assert.AreEqual(descriptor.ConfigurationHash, entity.GeneratorConfigurationHash)
        Assert.AreEqual(KnowledgeEnrichmentTrust.ModelGeneratedUntrusted, entity.Trust)
        Assert.AreEqual(DerivationStage.Enrichment, enriched.Derivation |> List.last |> _.Stage)

        let second = run enricher (request first.Chunks)
        Assert.AreEqual(1, calls)
        CollectionAssert.AreEqual(first.Chunks |> List.toArray, second.Chunks |> List.toArray)

    [<TestMethod>]
    member _.``Model enrichment rejects evidence outside exact source text``() =
        let model: KnowledgeEnrichmentModel =
            { Descriptor = descriptor
              EnrichAsync =
                fun _ inputs _ ->
                    Task.FromResult
                        [ { ChunkId = (inputs |> List.exactlyOne).ChunkId
                            Enrichments =
                              [ { Kind = KnowledgeEnrichmentKind.Topic
                                  Value = "unsupported"
                                  Confidence = 0.9
                                  Evidence =
                                    [ { ContentUnitId = units[0].Id
                                        Quote = "not in the source" } ] } ] } ] }

        let enricher = ModelAssistedEnricher.create (config model)

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> run enricher (request [ chunk ]) |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Model enrichment rejects mismatched and oversized responses``() =
        let unknownChunkId =
            KnowledgeChunkId.derive documentVersionId "unknown@1" 0 (ContentHash.computeText "unknown")

        let mismatched: KnowledgeEnrichmentModel =
            { Descriptor = descriptor
              EnrichAsync =
                fun _ _ _ ->
                    Task.FromResult
                        [ { ChunkId = unknownChunkId
                            Enrichments = [] } ] }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            request [ chunk ]
            |> run (ModelAssistedEnricher.create (config mismatched))
            |> ignore)
        |> ignore

        let strictConfig =
            { config mismatched with
                MaximumChunksPerRequest = 1 }

        let secondChunk =
            KnowledgeChunk.create
                documentVersionId
                1
                KnowledgeChunkRole.Leaf
                None
                [ units[1].Id ]
                units[1].Text
                [ units[1].Location ]
                chunker
                chunkerConfig
                []
                Map.empty
                lifecycle

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            request [ chunk; secondChunk ]
            |> run (ModelAssistedEnricher.create strictConfig)
            |> ignore)
        |> ignore
