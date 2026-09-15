namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass>]
type ChunkingTests() =
    let lifecycle = KnowledgeLifecycle.active None false
    let parser = ComponentVersion.create "parser" "1"
    let documentId = KnowledgeDocumentId.parse "chunking-fixture"

    let documentVersionId =
        KnowledgeDocumentVersionId.derive documentId 1 (ContentHash.computeText "document")

    let chunkedAt = DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)

    let authorization =
        SecurityPrincipal.create (TenantId.parse "chunk-tenant") (UserId.parse "chunk-user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "chunk-workspace") None
            |> Option.get

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
        { Authorization = authorization
          DocumentVersionId = documentVersionId
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
        Assert.AreEqual(6, first.Metrics.LeafChunks)
        Assert.AreEqual(2, first.Metrics.ParentChunks)

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

        Assert.AreEqual(units.Length, result.Metrics.LeafChunks)
        Assert.AreEqual(3, result.Metrics.ParentChunks)

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

    [<TestMethod>]
    member _.``Token windows overlap deterministically across content units``() =
        let sourceUnits =
            [ unit 0 ContentUnitKind.Paragraph "one two" "body/0" Map.empty
              unit 1 ContentUnitKind.Code "three four five" "body/1" Map.empty ]

        let chunker =
            TokenWindowChunker.create
                { MaximumTokens = 3
                  OverlapTokens = 1
                  Tokenizer = UnicodeWordTokenizer.create () }

        let first = chunk chunker sourceUnits
        let second = chunk chunker sourceUnits

        Assert.AreEqual(ChunkerContractVersion.current, chunker.Descriptor.ContractVersion)
        Assert.IsTrue(chunker.Descriptor.Deterministic)
        Assert.AreEqual(0, first.Metrics.ParentChunks)
        Assert.AreEqual(2, first.Metrics.LeafChunks)
        Assert.AreEqual(2, first.Metrics.OutputChunks)
        Assert.AreEqual(14, first.Metrics.CharacterSizes.Minimum)
        Assert.AreEqual(15, first.Metrics.CharacterSizes.Maximum)
        Assert.AreEqual(14.5, first.Metrics.CharacterSizes.Mean, 0.0001)
        Assert.AreEqual(14, first.Metrics.CharacterSizes.P50)
        Assert.AreEqual(15, first.Metrics.CharacterSizes.P95)
        Assert.AreEqual(3, first.Metrics.TokenSizes.Value.Minimum)
        Assert.AreEqual(3, first.Metrics.TokenSizes.Value.Maximum)
        Assert.AreEqual(0.0, first.Metrics.TruncatedUnitRate)
        Assert.AreEqual(0.0, first.Metrics.TruncatedCharacterRate)
        CollectionAssert.AreEqual(first.Chunks |> List.toArray, second.Chunks |> List.toArray)

        let firstWindow = first.Chunks[0]
        let secondWindow = first.Chunks[1]

        Assert.AreEqual("one two\n\nthree", firstWindow.Text)
        Assert.AreEqual("three four five", secondWindow.Text)

        CollectionAssert.AreEqual(
            sourceUnits |> List.map _.Id |> List.toArray,
            firstWindow.ContentUnitIds |> List.toArray
        )

        CollectionAssert.AreEqual([| sourceUnits[1].Id |], secondWindow.ContentUnitIds |> List.toArray)
        Assert.AreEqual(sourceUnits[1].Location, firstWindow.Locations[1])
        Assert.AreEqual(sourceUnits[1].Location, secondWindow.Locations[0])
        Assert.AreEqual("0", firstWindow.Metadata["token.start"])
        Assert.AreEqual("3", firstWindow.Metadata["token.count"])
        Assert.AreEqual("2", secondWindow.Metadata["token.start"])

    [<TestMethod>]
    member _.``Token-window configuration and tokenizer identity determine chunk IDs``() =
        let tokenizer = UnicodeWordTokenizer.create ()

        let first =
            TokenWindowChunker.create
                { MaximumTokens = 3
                  OverlapTokens = 1
                  Tokenizer = tokenizer }

        let second =
            TokenWindowChunker.create
                { MaximumTokens = 4
                  OverlapTokens = 1
                  Tokenizer = tokenizer }

        let firstChunk = (chunk first [ units[5] ]).Chunks |> List.exactlyOne
        let secondChunk = (chunk second [ units[5] ]).Chunks |> List.exactlyOne

        Assert.AreNotEqual(first.Descriptor.ConfigurationHash, second.Descriptor.ConfigurationHash)
        Assert.AreNotEqual(firstChunk.Id, secondChunk.Id)
        Assert.AreEqual("nao.tokenizer.unicode-word", firstChunk.Metadata["tokenizer.component"])
        Assert.AreEqual("1", firstChunk.Metadata["tokenizer.version"])

    [<TestMethod>]
    member _.``Token windows reject invalid overlap and tokenizer spans``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            TokenWindowChunker.create
                { MaximumTokens = 3
                  OverlapTokens = 3
                  Tokenizer = UnicodeWordTokenizer.create () }
            |> ignore)
        |> ignore

        let invalidTokenizer =
            { Descriptor =
                { Component = ComponentVersion.create "invalid-tokenizer" "1"
                  ConfigurationHash = ContentHash.computeText "invalid"
                  Deterministic = true }
              Tokenize =
                fun _ ->
                    [ { CharacterOffset = 100
                        CharacterLength = 1 } ] }

        let chunker =
            TokenWindowChunker.create
                { MaximumTokens = 3
                  OverlapTokens = 1
                  Tokenizer = invalidTokenizer }

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> chunk chunker [ units.Head ] |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Chunking metrics report explicit truncation rates``() =
        let sourceUnits =
            [ unit 0 ContentUnitKind.Paragraph "one two" "body/0" Map.empty
              unit 1 ContentUnitKind.Paragraph "three four five" "body/1" Map.empty ]

        let result =
            TokenWindowChunker.create
                { MaximumTokens = 3
                  OverlapTokens = 1
                  Tokenizer = UnicodeWordTokenizer.create () }
            |> fun chunker -> chunk chunker sourceUnits

        let metrics =
            ChunkingMeasurement.measure sourceUnits result.Chunks (Some [ 3; 3 ]) 1 4L

        Assert.AreEqual(2, metrics.InputUnits)
        Assert.AreEqual(22L, metrics.InputCharacters)
        Assert.AreEqual(1, metrics.TruncatedUnits)
        Assert.AreEqual(4L, metrics.TruncatedCharacters)
        Assert.AreEqual(0.5, metrics.TruncatedUnitRate, 0.0001)
        Assert.AreEqual(4.0 / 22.0, metrics.TruncatedCharacterRate, 0.0001)

    [<TestMethod>]
    member _.``Semantic chunking creates boundaries from pinned model similarity``() =
        let vectors =
            Map.ofList
                [ "Guide", [| 1.0; 0.0 |]
                  "Overview", [| 0.9; 0.1 |]
                  "A | B", [| 0.0; 1.0 |]
                  "let value = 1", [| 0.0; 0.9 |]
                  "Details", [| -1.0; 0.0 |]
                  "Explanation", [| -0.9; 0.1 |] ]

        let mutable requests = []

        let model =
            { Descriptor =
                { Model = ComponentVersion.create "semantic-boundary-model" "2026-09"
                  Dimensions = 2
                  ConfigurationHash = ContentHash.computeText "fixture-v1"
                  Deterministic = true }
              EmbedAsync =
                fun scope texts cancellationToken ->
                    cancellationToken.ThrowIfCancellationRequested()
                    Assert.AreEqual(authorization, scope)
                    requests <- texts :: requests
                    texts |> List.map (fun text -> vectors[text]) |> Task.FromResult }

        let chunker =
            SemanticChunker.create
                { BoundarySimilarityThreshold = 0.8
                  MaximumUnitsPerChunk = 10
                  Model = model }

        let first = chunk chunker units
        let second = chunk chunker units

        Assert.IsTrue(chunker.Descriptor.Deterministic)
        Assert.AreEqual(3, first.Metrics.LeafChunks)
        Assert.AreEqual(0, first.Metrics.ParentChunks)
        Assert.AreEqual(2, requests.Length)
        CollectionAssert.AreEqual(first.Chunks |> List.toArray, second.Chunks |> List.toArray)

        CollectionAssert.AreEqual(
            [| "Guide\n\nOverview"; "A | B\n\nlet value = 1"; "Details\n\nExplanation" |],
            first.Chunks |> List.map _.Text |> List.toArray
        )

        let middle = first.Chunks[1]
        CollectionAssert.AreEqual([| units[2].Id; units[3].Id |], middle.ContentUnitIds |> List.toArray)
        CollectionAssert.AreEqual([| units[2].Location; units[3].Location |], middle.Locations |> List.toArray)
        Assert.AreEqual("semantic-boundary-model", middle.Metadata["semantic.model"])
        Assert.AreEqual("2", middle.Metadata["semantic.unit-count"])

    [<TestMethod>]
    member _.``Semantic maximum units and model configuration affect stable chunks``() =
        let model configuration =
            { Descriptor =
                { Model = ComponentVersion.create "constant-boundary-model" "1"
                  Dimensions = 2
                  ConfigurationHash = ContentHash.computeText configuration
                  Deterministic = true }
              EmbedAsync = fun _ texts _ -> texts |> List.map (fun _ -> [| 1.0; 0.0 |]) |> Task.FromResult }

        let create maximumUnits semanticModel =
            SemanticChunker.create
                { BoundarySimilarityThreshold = -1.0
                  MaximumUnitsPerChunk = maximumUnits
                  Model = semanticModel }

        let first = create 2 (model "a")
        let second = create 3 (model "a")
        let third = create 2 (model "b")

        let firstResult = chunk first units
        let secondResult = chunk second units
        let thirdResult = chunk third units

        Assert.AreEqual(3, firstResult.Metrics.LeafChunks)
        Assert.AreEqual(2, secondResult.Metrics.LeafChunks)
        Assert.AreNotEqual(first.Descriptor.ConfigurationHash, second.Descriptor.ConfigurationHash)
        Assert.AreNotEqual(first.Descriptor.ConfigurationHash, third.Descriptor.ConfigurationHash)
        Assert.AreNotEqual(firstResult.Chunks[0].Id, thirdResult.Chunks[0].Id)

    [<TestMethod>]
    member _.``Semantic chunking rejects malformed model vectors``() =
        let malformedModel =
            { Descriptor =
                { Model = ComponentVersion.create "malformed-boundary-model" "1"
                  Dimensions = 2
                  ConfigurationHash = ContentHash.computeText "malformed"
                  Deterministic = true }
              EmbedAsync = fun _ _ _ -> Task.FromResult [ [| Double.NaN; 0.0 |] ] }

        let chunker =
            SemanticChunker.create
                { BoundarySimilarityThreshold = 0.5
                  MaximumUnitsPerChunk = 2
                  Model = malformedModel }

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> chunk chunker [ units.Head ] |> ignore)
        |> ignore
