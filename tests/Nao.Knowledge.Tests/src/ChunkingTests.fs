namespace Nao.Knowledge.Tests

open System
open System.Threading
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Knowledge

[<TestClass>]
type ChunkingTests() =
    let lifecycle = KnowledgeLifecycle.active None false
    let parser = ComponentVersion.create "parser" "1"
    let documentId = KnowledgeDocumentId.parse "chunking-fixture"

    let documentVersionId =
        KnowledgeDocumentVersionId.derive documentId 1 (ContentHash.computeText "document")

    let chunkedAt = DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)

    let unit sequence kind text path metadata =
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
                None
                (Some(sequence + 1))
                (Some(sequence + 1))
                (Some path))
            parser
            []
            metadata
            lifecycle

    let units =
        [ unit 0 ContentUnitKind.Heading "Guide" "body/0" (Map.ofList [ "heading.level", "1" ])
          unit 1 ContentUnitKind.Paragraph "Overview" "body/1" Map.empty
          unit 2 ContentUnitKind.Table "A | B" "body/2" Map.empty
          unit 3 ContentUnitKind.Code "let value = 1" "body/3" Map.empty
          unit 4 ContentUnitKind.Heading "Details" "body/4" (Map.ofList [ "heading.level", "2" ])
          unit 5 ContentUnitKind.Paragraph "Explanation" "body/5" Map.empty ]

    let request contentUnits =
        { DocumentVersionId = documentVersionId
          Units = contentUnits
          Lifecycle = lifecycle
          ChunkedAt = chunkedAt }

    let chunk chunker contentUnits =
        chunker.ChunkAsync (request contentUnits) CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Structure chunking is deterministic and preserves parent-child provenance``() =
        let chunker = StructureAwareChunker.create { MaximumParentCharacters = 1000 }

        let first = chunk chunker units
        let second = chunk chunker units

        Assert.AreEqual(ChunkerContractVersion.current, chunker.Descriptor.ContractVersion)
        Assert.IsTrue(chunker.Descriptor.Deterministic)
        CollectionAssert.AreEqual(first.Chunks |> List.toArray, second.Chunks |> List.toArray)
        Assert.AreEqual(6, first.LeafChunks)
        Assert.AreEqual(2, first.ParentChunks)

        let parents =
            first.Chunks |> List.filter (fun item -> item.Role = KnowledgeChunkRole.Parent)

        let firstParent = parents.Head
        Assert.AreEqual("Guide\n\nOverview\n\nA | B\n\nlet value = 1", firstParent.Text)
        Assert.AreEqual(4, firstParent.ContentUnitIds.Length)
        Assert.AreEqual(4, firstParent.Locations.Length)

        let children =
            first.Chunks
            |> List.filter (fun item -> item.ParentChunkId = Some firstParent.Id)

        Assert.AreEqual(4, children.Length)

        CollectionAssert.AreEqual(
            [| ContentUnitKind.Heading
               ContentUnitKind.Paragraph
               ContentUnitKind.Table
               ContentUnitKind.Code |],
            children
            |> List.map (fun child ->
                units
                |> List.find (fun source -> child.ContentUnitIds = [ source.Id ])
                |> _.Kind)
            |> List.toArray
        )

        children
        |> List.iter (fun child ->
            Assert.AreEqual(KnowledgeChunkRole.Leaf, child.Role)
            Assert.AreEqual(1, child.Locations.Length)
            Assert.IsTrue(child.Sequence > firstParent.Sequence))

    [<TestMethod>]
    member _.``Parent bounds split only between structural units``() =
        let result =
            StructureAwareChunker.create { MaximumParentCharacters = 20 }
            |> fun chunker -> chunk chunker units

        Assert.AreEqual(units.Length, result.LeafChunks)
        Assert.AreEqual(3, result.ParentChunks)

        result.Chunks
        |> List.filter (fun item -> item.Role = KnowledgeChunkRole.Parent)
        |> List.iter (fun parent ->
            Assert.IsTrue(parent.ContentUnitIds.Length > 1)
            Assert.IsTrue(parent.Text.Length <= 20))

        result.Chunks
        |> List.filter (fun item -> item.Role = KnowledgeChunkRole.Leaf)
        |> List.iter (fun leaf -> Assert.AreEqual(1, leaf.ContentUnitIds.Length))

    [<TestMethod>]
    member _.``Pinned configuration participates in stable chunk identity``() =
        let wide = StructureAwareChunker.create { MaximumParentCharacters = 1000 }

        let narrow = StructureAwareChunker.create { MaximumParentCharacters = 20 }

        Assert.AreNotEqual(wide.Descriptor.ConfigurationHash, narrow.Descriptor.ConfigurationHash)

        let wideLeaf = (chunk wide [ units.Head ]).Chunks |> List.exactlyOne
        let narrowLeaf = (chunk narrow [ units.Head ]).Chunks |> List.exactlyOne

        Assert.AreNotEqual(wideLeaf.Id, narrowLeaf.Id)
        Assert.AreEqual(wide.Descriptor.ConfigurationHash, wideLeaf.ChunkerConfigurationHash)

    [<TestMethod>]
    member _.``Chunking rejects units from another version``() =
        let otherVersion =
            KnowledgeDocumentVersionId.derive documentId 2 (ContentHash.computeText "other")

        let foreign =
            ContentUnit.create
                otherVersion
                0
                ContentUnitKind.Paragraph
                "foreign"
                (SourceLocation.create 0L 7L 0 7 None None None None)
                parser
                []
                Map.empty
                lifecycle

        let chunker = StructureAwareChunker.create { MaximumParentCharacters = 1000 }

        Assert.ThrowsExactly<ArgumentException>(fun () -> chunk chunker [ units.Head; foreign ] |> ignore)
        |> ignore
