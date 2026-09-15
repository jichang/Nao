namespace Nao.Knowledge.Tests

open System
open System.Threading
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass>]
type MetadataExtractionTests() =
    let lifecycle = KnowledgeLifecycle.active None false
    let parser = ComponentVersion.create "parser" "1"
    let chunker = ComponentVersion.create "chunker" "1"
    let chunkerConfig = ContentHash.computeText "chunker-config"
    let documentId = KnowledgeDocumentId.parse "metadata-fixture"

    let documentVersionId =
        KnowledgeDocumentVersionId.derive documentId 1 (ContentHash.computeText "document")

    let authorization =
        SecurityPrincipal.create (TenantId.parse "metadata-tenant") (UserId.parse "metadata-user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "metadata-workspace") None
            |> Option.get

    let unit sequence kind text page path metadata =
        ContentUnit.create
            documentVersionId
            sequence
            kind
            text
            (SourceLocation.create
                (int64 (sequence * 100))
                (int64 text.Length)
                (sequence * 100)
                text.Length
                page
                (Some(sequence + 1))
                (Some(sequence + 1))
                path)
            parser
            []
            metadata
            lifecycle

    let units =
        [ unit 0 ContentUnitKind.Heading "Guide" (Some 1) (Some "body/0") (Map.ofList [ "heading.level", "1" ])
          unit
              1
              ContentUnitKind.Paragraph
              "Overview"
              (Some 1)
              (Some "body/1")
              (Map.ofList [ "links", "https://example.test/a\u001fhttps://example.test/b" ])
          unit 2 ContentUnitKind.Heading "Details" (Some 2) (Some "body/2") (Map.ofList [ "heading.level", "2" ])
          unit
              3
              ContentUnitKind.Code
              "let value = 1"
              (Some 2)
              (Some "body/3")
              (Map.ofList [ "links", "https://example.test/b" ]) ]

    let chunk sequence (sourceUnits: ContentUnit list) metadata =
        KnowledgeChunk.create
            documentVersionId
            sequence
            KnowledgeChunkRole.Leaf
            None
            (sourceUnits |> List.map _.Id)
            (sourceUnits |> List.map _.Text |> String.concat "\n\n")
            (sourceUnits |> List.map _.Location)
            chunker
            chunkerConfig
            []
            metadata
            lifecycle

    let request (chunks: KnowledgeChunk list) =
        { Authorization = authorization
          DocumentVersionId = documentVersionId
          Units = units
          Chunks = chunks
          EnrichedAt = DateTimeOffset(2026, 9, 15, 1, 0, 0, TimeSpan.Zero) }

    let enrich request =
        (DeterministicMetadataEnricher.create ()).EnrichAsync request CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Metadata extraction derives heading source kind and link facets``() =
        let firstChunk = chunk 0 units[0..1] Map.empty
        let secondChunk = chunk 1 [ units[3] ] Map.empty
        let result = enrich (request [ firstChunk; secondChunk ])

        Assert.AreEqual(2, result.EnrichedChunks)

        let first = result.Chunks[0]
        Assert.AreEqual("heading\u001fparagraph", first.Metadata["derived.content-kinds"])
        Assert.AreEqual("Guide", first.Metadata["derived.heading-path"])
        Assert.AreEqual("1", first.Metadata["derived.source-pages"])
        Assert.AreEqual("body/0\u001fbody/1", first.Metadata["derived.source-paths"])
        Assert.AreEqual("https://example.test/a\u001fhttps://example.test/b", first.Metadata["derived.links"])
        Assert.AreEqual("2", first.Metadata["derived.unit-count"])

        let second = result.Chunks[1]
        Assert.AreEqual("Guide\u001fDetails", second.Metadata["derived.heading-path"])
        Assert.AreEqual("code", second.Metadata["derived.content-kinds"])
        Assert.AreEqual("2", second.Metadata["derived.source-pages"])
        Assert.AreEqual(DerivationStage.Enrichment, second.Derivation |> List.last |> _.Stage)

    [<TestMethod>]
    member _.``Metadata extraction is deterministic and idempotent``() =
        let original = chunk 0 units Map.empty
        let first = enrich (request [ original ])
        let second = enrich (request first.Chunks)

        CollectionAssert.AreEqual(first.Chunks |> List.toArray, second.Chunks |> List.toArray)
        Assert.IsTrue((DeterministicMetadataEnricher.create ()).Descriptor.Deterministic)

    [<TestMethod>]
    member _.``Metadata extraction rejects reserved metadata and foreign units``() =
        let reserved =
            chunk 0 [ units.Head ] (Map.ofList [ "derived.unit-count", "forged" ])

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> enrich (request [ reserved ]) |> ignore)
        |> ignore

        let foreignVersion =
            KnowledgeDocumentVersionId.derive documentId 2 (ContentHash.computeText "foreign")

        let foreign =
            ContentUnit.create
                foreignVersion
                0
                ContentUnitKind.Paragraph
                "foreign"
                (SourceLocation.create 0L 7L 0 7 None None None None)
                parser
                []
                Map.empty
                lifecycle

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            enrich
                { request [] with
                    Units = units @ [ foreign ] }
            |> ignore)
        |> ignore
