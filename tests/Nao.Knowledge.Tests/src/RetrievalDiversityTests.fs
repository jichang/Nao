namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Determinism"); TestCategory("FailClosed")>]
type RetrievalDiversityTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let namespaceName = KnowledgeIndexNamespace.parse "primary"

    let profile =
        EmbeddingCapabilityDescriptor.create
            (ComponentVersion.create "embedding-model" "1")
            2
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            (ComponentVersion.create "query-preprocessing" "1")
            (EmbeddingCapabilityLimits.create 8 100 50 200)

    let version suffix =
        let hash = ContentHash.computeText ("version-" + suffix)
        KnowledgeDocumentVersionId.derive (KnowledgeDocumentId.parse ("document-" + suffix)) 1 hash

    let document sourceId versionId text suffix =
        let contentHash = ContentHash.computeText text
        let chunkId = KnowledgeChunkId.derive versionId "test-chunker" 0 contentHash
        let now = DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero)

        KnowledgeIndexDocument.restore
            namespaceName
            (AuthorizationScope.identity authorization)
            KnowledgeIndexAccess.ownerOnly
            sourceId
            (KnowledgeDocumentId.parse ("document-" + KnowledgeDocumentVersionId.value versionId))
            1
            versionId
            chunkId
            KnowledgeChunkRole.Leaf
            None
            [ SourceLocation.create 0L (int64 text.Length) 0 text.Length None (Some 1) (Some 1) None ]
            contentHash
            text
            (Map.ofList [ "test.suffix", suffix ])
            (TrustAssessment.create KnowledgeTrust.OrganizationVerified "test" now)
            (TrustAssessment.create KnowledgeTrust.OrganizationVerified "test" now)
            (Some now)
            now
            now

    let fusion candidates maximumCandidates =
        let normalized =
            KnowledgeRetrievalRequest.create
                "query"
                authorization
                namespaceName
                KnowledgeIndexFilter.none
                KnowledgeTaskIntent.KnowledgeSeeking
                KnowledgeRetrievalStrategy.Hybrid
                KnowledgeQueryTransformationStrategy.None
                (KnowledgeRetrievalLimits.create 100 200 1 maximumCandidates maximumCandidates)
                (KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 1.0) 0 1 10 0.10m)
            |> KnowledgeQueryNormalizer.normalize

        let planned =
            KnowledgeQueryUnderstanding.planAsync None normalized CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let lexicalIndex: KnowledgeLexicalIndex =
            { ApplyAsync = fun _ _ _ -> invalidOp "Mutation is not used by this test."
              QueryAsync =
                fun _ _ _ ->
                    candidates
                    |> List.mapi (fun index candidate -> LexicalIndexHit.create candidate (100.0 - float index))
                    |> Task.FromResult }

        let lexical =
            match
                KnowledgeLexicalRetrievalExecutor.executeAsync lexicalIndex planned CancellationToken.None
                |> _.GetAwaiter().GetResult()
            with
            | KnowledgeLexicalRetrievalResult.Retrieved value -> value
            | KnowledgeLexicalRetrievalResult.Skipped _ -> invalidOp "Test retrieval was unexpectedly skipped."

        let embedder =
            { Profile = profile
              EmbedQueriesAsync =
                fun _ request _ ->
                    let input = KnowledgeQueryEmbeddingRequest.inputs request |> List.exactlyOne

                    KnowledgeQueryEmbeddingResult.create
                        [ KnowledgeQueryEmbedding.create (KnowledgeQueryEmbeddingInput.contentHash input) [| 1.0; 0.0 |] ]
                        (KnowledgeQueryEmbeddingUsage.create 1 1 0.01m)
                    |> Task.FromResult }

        let vectorIndex: KnowledgeVectorIndex =
            { ApplyAsync = fun _ _ _ -> invalidOp "Mutation is not used by this test."
              QueryAsync =
                fun _ _ _ ->
                    candidates
                    |> List.mapi (fun index candidate ->
                        VectorIndexHit.create candidate profile.ProfileId (1.0 - float index / 10.0))
                    |> Task.FromResult }

        let vector =
            match
                KnowledgeVectorRetrievalExecutor.executeAsync embedder vectorIndex planned CancellationToken.None
                |> _.GetAwaiter().GetResult()
            with
            | KnowledgeVectorRetrievalResult.Retrieved value -> value
            | KnowledgeVectorRetrievalResult.Skipped _ -> invalidOp "Test retrieval was unexpectedly skipped."

        KnowledgeReciprocalRankFusionExecutor.fuse KnowledgeReciprocalRankFusionConfig.defaultConfig lexical vector

    [<TestMethod>]
    member _.``Selection suppresses exact duplicates and enforces source document and total diversity limits``() =
        let sourceOne = KnowledgeSourceId.parse "source-one"
        let versionOne = version "one"
        let first = document sourceOne versionOne "duplicate content" "first"

        let exactDuplicate =
            document (KnowledgeSourceId.parse "source-two") (version "two") "duplicate content" "duplicate"

        let sameSource =
            document sourceOne (version "three") "same source content" "same-source"

        let sameVersion =
            document (KnowledgeSourceId.parse "source-three") versionOne "same version content" "same-version"

        let secondSelected =
            document (KnowledgeSourceId.parse "source-four") (version "four") "selected content" "selected"

        let overLimit =
            document (KnowledgeSourceId.parse "source-five") (version "five") "over limit content" "over-limit"

        let fused =
            fusion [ first; exactDuplicate; sameSource; sameVersion; secondSelected; overLimit ] 6

        let selection =
            KnowledgeRetrievalDiversitySelector.select
                (KnowledgeRetrievalDiversityConfig.create 2 (Some 1) (Some 1) true)
                fused

        Assert.AreEqual(
            KnowledgeRetrievalDiversitySelector.descriptor,
            KnowledgeRetrievalDiversitySelection.selector selection
        )

        Assert.AreSame(fused, KnowledgeRetrievalDiversitySelection.fusion selection)

        Assert.AreSame(
            KnowledgeReciprocalRankFusion.request fused,
            KnowledgeRetrievalDiversitySelection.request selection
        )

        let selected = KnowledgeRetrievalDiversitySelection.candidates selection
        Assert.AreEqual(2, selected.Length)
        Assert.AreSame(first, selected[0] |> KnowledgeSelectedCandidate.document)
        Assert.AreEqual(1, selected[0] |> KnowledgeSelectedCandidate.rank)
        Assert.AreSame(secondSelected, selected[1] |> KnowledgeSelectedCandidate.document)
        Assert.AreEqual(2, selected[1] |> KnowledgeSelectedCandidate.rank)

        let outcomes =
            KnowledgeRetrievalDiversitySelection.decisions selection
            |> List.map KnowledgeCandidateSelectionDecision.outcome

        match outcomes with
        | [ KnowledgeCandidateSelectionOutcome.Selected 1
            KnowledgeCandidateSelectionOutcome.Suppressed(KnowledgeCandidateSuppressionReason.ExactContentDuplicate selectedChunkId)
            KnowledgeCandidateSelectionOutcome.Suppressed(KnowledgeCandidateSuppressionReason.SourceLimit actualSource)
            KnowledgeCandidateSelectionOutcome.Suppressed(KnowledgeCandidateSuppressionReason.DocumentVersionLimit actualVersion)
            KnowledgeCandidateSelectionOutcome.Selected 2
            KnowledgeCandidateSelectionOutcome.Suppressed KnowledgeCandidateSuppressionReason.CandidateLimit ] ->
            Assert.AreEqual(KnowledgeIndexDocument.chunkId first, selectedChunkId)
            Assert.AreEqual(sourceOne, actualSource)
            Assert.AreEqual(versionOne, actualVersion)
        | _ -> Assert.Fail $"Unexpected diversity outcomes: {outcomes}"

    [<TestMethod>]
    member _.``Unrestricted selection preserves fused order and duplicate content``() =
        let first =
            document (KnowledgeSourceId.parse "source-one") (version "one") "same content" "first"

        let second =
            document (KnowledgeSourceId.parse "source-two") (version "two") "same content" "second"

        let fused = fusion [ first; second ] 2

        let selection =
            KnowledgeRetrievalDiversitySelector.select (KnowledgeRetrievalDiversityConfig.unrestricted 2) fused

        let selected = KnowledgeRetrievalDiversitySelection.candidates selection
        Assert.AreEqual(2, selected.Length)

        List.zip (KnowledgeReciprocalRankFusion.candidates fused) selected
        |> List.iteri (fun index (fusedCandidate, selectedCandidate) ->
            Assert.AreSame(fusedCandidate, KnowledgeSelectedCandidate.candidate selectedCandidate)
            Assert.AreEqual(index + 1, KnowledgeSelectedCandidate.rank selectedCandidate))

    [<TestMethod>]
    member _.``Diversity configuration and request bounds fail closed``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeRetrievalDiversityConfig.create 0 None None true |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeRetrievalDiversityConfig.create 2 (Some 3) None true |> ignore)
        |> ignore

        let candidate =
            document (KnowledgeSourceId.parse "source") (version "version") "content" "candidate"

        let fused = fusion [ candidate ] 1

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeRetrievalDiversitySelector.select (KnowledgeRetrievalDiversityConfig.unrestricted 2) fused
            |> ignore)
        |> ignore
