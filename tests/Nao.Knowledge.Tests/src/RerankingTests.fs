namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type RerankingTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let namespaceName = KnowledgeIndexNamespace.parse "primary"

    let authorityThenCurrent =
        KnowledgeContextSourcePreferencePolicy.create KnowledgeContextSourcePreferencePriority.AuthorityThenCurrent

    let profile =
        EmbeddingCapabilityDescriptor.create
            (ComponentVersion.create "embedding-model" "1")
            2
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            (ComponentVersion.create "query-preprocessing" "1")
            (EmbeddingCapabilityLimits.create 8 100 50 200)

    let documentVersionWithTrustFrom
        sourceId
        documentId
        documentVersion
        versionContent
        sourceTrust
        versionTrust
        sourceModifiedAt
        ingestedAt
        sequence
        text
        =
        let contentHash = ContentHash.computeText text

        let versionId =
            KnowledgeDocumentVersionId.derive documentId documentVersion (ContentHash.computeText versionContent)

        KnowledgeIndexDocument.restore
            namespaceName
            (AuthorizationScope.identity authorization)
            KnowledgeIndexAccess.ownerOnly
            sourceId
            documentId
            documentVersion
            versionId
            (KnowledgeChunkId.derive versionId "test-chunker" sequence contentHash)
            KnowledgeChunkRole.Leaf
            None
            [ SourceLocation.create 0L (int64 text.Length) 0 text.Length None (Some 1) (Some 1) None ]
            contentHash
            text
            Map.empty
            sourceTrust
            versionTrust
            sourceModifiedAt
            ingestedAt
            ingestedAt

    let documentVersionFrom sourceId documentId documentVersion versionContent sequence text =
        let now = DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero)
        let trust = TrustAssessment.create KnowledgeTrust.OrganizationVerified "test" now

        documentVersionWithTrustFrom
            sourceId
            documentId
            documentVersion
            versionContent
            trust
            trust
            (Some now)
            now
            sequence
            text

    let documentFrom sourceId sequence text =
        documentVersionFrom
            sourceId
            (KnowledgeDocumentId.parse ("document-" + KnowledgeSourceId.value sourceId))
            1
            "version"
            sequence
            text

    let document sequence text =
        documentFrom (KnowledgeSourceId.parse "source") sequence text

    let candidates (documents: KnowledgeIndexDocument list) =
        let request =
            KnowledgeRetrievalRequest.create
                "query"
                authorization
                namespaceName
                KnowledgeIndexFilter.none
                KnowledgeTaskIntent.KnowledgeSeeking
                KnowledgeRetrievalStrategy.Hybrid
                KnowledgeQueryTransformationStrategy.None
                (KnowledgeRetrievalLimits.create 100 200 1 documents.Length documents.Length)
                (KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 2.0) 0 1 10 0.01m)

        let understanding =
            request
            |> KnowledgeQueryNormalizer.normalize
            |> fun normalized -> KnowledgeQueryUnderstanding.planAsync None normalized CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let lexicalIndex: KnowledgeLexicalIndex =
            { ApplyAsync = fun _ _ _ -> invalidOp "Mutation is not used by this test."
              QueryAsync =
                fun _ _ _ ->
                    documents
                    |> List.mapi (fun index value -> LexicalIndexHit.create value (10.0 - float index))
                    |> Task.FromResult }

        let embedder: KnowledgeQueryEmbedder =
            { Profile = profile
              EmbedQueriesAsync =
                fun _ embeddingRequest _ ->
                    let input =
                        KnowledgeQueryEmbeddingRequest.inputs embeddingRequest |> List.exactlyOne

                    KnowledgeQueryEmbeddingResult.create
                        [ KnowledgeQueryEmbedding.create (KnowledgeQueryEmbeddingInput.contentHash input) [| 1.0; 0.0 |] ]
                        (KnowledgeQueryEmbeddingUsage.create 1 1 0m)
                    |> Task.FromResult }

        let vectorIndex: KnowledgeVectorIndex =
            { ApplyAsync = fun _ _ _ -> invalidOp "Mutation is not used by this test."
              QueryAsync =
                fun _ _ _ ->
                    documents
                    |> List.mapi (fun index value ->
                        VectorIndexHit.create value profile.ProfileId (1.0 - float index / 10.0))
                    |> Task.FromResult }

        let lexical =
            match
                KnowledgeLexicalRetrievalExecutor.executeAsync lexicalIndex understanding CancellationToken.None
                |> _.GetAwaiter().GetResult()
            with
            | KnowledgeLexicalRetrievalResult.Retrieved value -> value
            | KnowledgeLexicalRetrievalResult.Skipped _ -> invalidOp "Unexpected retrieval skip."

        let vector =
            match
                KnowledgeVectorRetrievalExecutor.executeAsync embedder vectorIndex understanding CancellationToken.None
                |> _.GetAwaiter().GetResult()
            with
            | KnowledgeVectorRetrievalResult.Retrieved value -> value
            | KnowledgeVectorRetrievalResult.Skipped _ -> invalidOp "Unexpected retrieval skip."

        KnowledgeReciprocalRankFusionExecutor.fuse KnowledgeReciprocalRankFusionConfig.defaultConfig lexical vector
        |> KnowledgeRetrievalDiversitySelector.select (KnowledgeRetrievalDiversityConfig.unrestricted documents.Length)
        |> fun selection -> KnowledgeRetrievalCandidateAssembler.assemble selection None None

    [<TestMethod>]
    member _.``Deterministic score reranker records configuration and score changes``() =
        let first = document 0 "first"
        let second = document 1 "second"
        let retrievalCandidates = candidates [ first; second ]

        let request =
            KnowledgeRerankRequest.create
                retrievalCandidates
                2
                (KnowledgeRerankBudget.create (TimeSpan.FromSeconds 1.0) 0 0 0m)

        let reranking =
            KnowledgeRerankingExecutor.executeAsync
                (KnowledgeScoreBasedReranker.create KnowledgeScoreBasedRerankerConfig.defaultConfig)
                request
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let descriptor = KnowledgeReranking.reranker reranking
        Assert.AreEqual(KnowledgeRerankerKind.ScoreBased, descriptor.Kind)
        Assert.IsTrue(descriptor.Deterministic)
        Assert.IsTrue(descriptor.Model.IsNone)
        Assert.AreEqual(64, descriptor.ConfigurationHash |> ContentHash.value |> String.length)

        let reranked = KnowledgeReranking.candidates reranking
        Assert.AreEqual(2, reranked.Length)
        Assert.AreSame(first, reranked[0] |> KnowledgeRerankedCandidate.document)
        Assert.AreEqual(1, reranked[0] |> KnowledgeRerankedCandidate.originalRank)
        Assert.AreEqual(1, reranked[0] |> KnowledgeRerankedCandidate.rank)

        Assert.IsTrue(
            (reranked[0] |> KnowledgeRerankedCandidate.score) > (reranked[1] |> KnowledgeRerankedCandidate.score)
        )

        Assert.AreEqual(KnowledgeRerankerUsage.zero, KnowledgeReranking.usage reranking)

    [<TestMethod>]
    member _.``Model reranker receives exact authorization and must score every candidate``() =
        let first = document 0 "first"
        let second = document 1 "second"
        let retrievalCandidates = candidates [ first; second ]

        let request =
            KnowledgeRerankRequest.create
                retrievalCandidates
                1
                (KnowledgeRerankBudget.create (TimeSpan.FromSeconds 1.0) 1 10 0.01m)

        let descriptor =
            { Component = ComponentVersion.create "cross-encoder-reranker" "1"
              Kind = KnowledgeRerankerKind.CrossEncoder
              Model = Some(ComponentVersion.create "cross-encoder-model" "1")
              ConfigurationHash = ContentHash.computeText "cross-encoder-config"
              Deterministic = true }

        let reranker: KnowledgeReranker =
            { Descriptor = descriptor
              RerankAsync =
                fun actualAuthorization actualRequest cancellationToken ->
                    cancellationToken.ThrowIfCancellationRequested()
                    Assert.AreSame(authorization, actualAuthorization)
                    Assert.AreSame(request, actualRequest)

                    KnowledgeRerankerOutput.create
                        [ KnowledgeRerankScore.create (KnowledgeIndexDocument.chunkId first) 0.1
                          KnowledgeRerankScore.create (KnowledgeIndexDocument.chunkId second) 0.9 ]
                        (KnowledgeRerankerUsage.create 1 5 0 0.01m)
                    |> Task.FromResult }

        let reranking =
            KnowledgeRerankingExecutor.executeAsync reranker request CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let top = KnowledgeReranking.candidates reranking |> List.exactlyOne
        Assert.AreSame(second, KnowledgeRerankedCandidate.document top)
        Assert.AreEqual(2, KnowledgeRerankedCandidate.originalRank top)
        Assert.AreEqual(1, KnowledgeRerankedCandidate.rank top)
        Assert.AreEqual(0.9, KnowledgeRerankedCandidate.score top)
        Assert.AreEqual(descriptor, KnowledgeReranking.reranker reranking)

        let incomplete: KnowledgeReranker =
            { reranker with
                RerankAsync =
                    fun _ _ _ ->
                        KnowledgeRerankerOutput.create
                            [ KnowledgeRerankScore.create (KnowledgeIndexDocument.chunkId first) 1.0 ]
                            KnowledgeRerankerUsage.zero
                        |> Task.FromResult }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeRerankingExecutor.executeAsync incomplete request CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Cross encoder adapter binds query candidates limits model and usage``() =
        let first = document 0 "first"
        let second = document 1 "second"
        let retrievalCandidates = candidates [ first; second ]

        let request =
            KnowledgeRerankRequest.create
                retrievalCandidates
                2
                (KnowledgeRerankBudget.create (TimeSpan.FromSeconds 1.0) 2 20 0.02m)

        let model = ComponentVersion.create "cross-encoder-model" "1"
        let configurationHash = ContentHash.computeText "cross-encoder-config"

        let config = KnowledgeModelRerankerConfig.create model configurationHash 100 100

        let scorer: KnowledgeCrossEncoderScorer =
            { ScoreAsync =
                fun actualAuthorization modelRequest cancellationToken ->
                    cancellationToken.ThrowIfCancellationRequested()
                    Assert.AreSame(authorization, actualAuthorization)
                    Assert.AreEqual("query", KnowledgeModelRerankRequest.query modelRequest)
                    Assert.AreEqual(2, KnowledgeModelRerankRequest.maximumModelCalls modelRequest)
                    Assert.AreEqual(20, KnowledgeModelRerankRequest.maximumTokens modelRequest)
                    Assert.AreEqual(0.02m, KnowledgeModelRerankRequest.maximumCostUsd modelRequest)

                    let inputs = KnowledgeModelRerankRequest.candidates modelRequest
                    Assert.AreEqual([ "first"; "second" ], inputs |> List.map KnowledgeModelRerankCandidate.text)

                    KnowledgeRerankerOutput.create
                        [ KnowledgeRerankScore.create (KnowledgeIndexDocument.chunkId first) 0.2
                          KnowledgeRerankScore.create (KnowledgeIndexDocument.chunkId second) 0.8 ]
                        (KnowledgeRerankerUsage.create 1 8 0 0.01m)
                    |> Task.FromResult }

        let reranker = KnowledgeCrossEncoderReranker.create config true scorer

        let result =
            KnowledgeRerankingExecutor.executeAsync reranker request CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let descriptor = KnowledgeReranking.reranker result
        Assert.AreEqual(KnowledgeRerankerKind.CrossEncoder, descriptor.Kind)
        Assert.AreEqual(Some model, descriptor.Model)
        Assert.AreEqual(configurationHash, descriptor.ConfigurationHash)
        Assert.IsTrue(descriptor.Deterministic)

        Assert.AreSame(
            second,
            result
            |> KnowledgeReranking.candidates
            |> List.head
            |> KnowledgeRerankedCandidate.document
        )

    [<TestMethod>]
    member _.``Language model adapter is optional non-deterministic and requires metered usage``() =
        let first = document 0 "first"
        let retrievalCandidates = candidates [ first ]

        let request =
            KnowledgeRerankRequest.create
                retrievalCandidates
                1
                (KnowledgeRerankBudget.create (TimeSpan.FromSeconds 1.0) 1 10 0.01m)

        let config =
            KnowledgeModelRerankerConfig.create
                (ComponentVersion.create "language-model" "1")
                (ContentHash.computeText "language-model-config")
                100
                100

        let unmetered: KnowledgeLanguageModelReranker =
            { ScoreAsync =
                fun _ _ _ ->
                    KnowledgeRerankerOutput.create
                        [ KnowledgeRerankScore.create (KnowledgeIndexDocument.chunkId first) 1.0 ]
                        KnowledgeRerankerUsage.zero
                    |> Task.FromResult }

        let reranker = KnowledgeLanguageModelRerankerAdapter.create config unmetered
        Assert.AreEqual(KnowledgeRerankerKind.LanguageModel, reranker.Descriptor.Kind)
        Assert.IsFalse(reranker.Descriptor.Deterministic)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeRerankingExecutor.executeAsync reranker request CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let metered: KnowledgeLanguageModelReranker =
            { ScoreAsync =
                fun _ modelRequest _ ->
                    Assert.AreEqual(
                        "first",
                        modelRequest
                        |> KnowledgeModelRerankRequest.candidates
                        |> List.exactlyOne
                        |> KnowledgeModelRerankCandidate.text
                    )

                    KnowledgeRerankerOutput.create
                        [ KnowledgeRerankScore.create (KnowledgeIndexDocument.chunkId first) 0.7 ]
                        (KnowledgeRerankerUsage.create 1 4 2 0.01m)
                    |> Task.FromResult }

        let result =
            KnowledgeRerankingExecutor.executeAsync
                (KnowledgeLanguageModelRerankerAdapter.create config metered)
                request
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(6L, result |> KnowledgeReranking.usage |> KnowledgeRerankerUsage.totalTokens)

    [<TestMethod>]
    member _.``Context assembly reauthorizes and enforces exact token and source limits``() =
        let first = documentFrom (KnowledgeSourceId.parse "first-source") 0 "first evidence"

        let second =
            documentFrom (KnowledgeSourceId.parse "first-source") 1 "second evidence"

        let third =
            documentFrom (KnowledgeSourceId.parse "second-source") 2 "third evidence"

        let retrievalCandidates = candidates [ first; second; third ]

        let reranking =
            KnowledgeRerankingExecutor.executeAsync
                (KnowledgeScoreBasedReranker.create KnowledgeScoreBasedRerankerConfig.defaultConfig)
                (KnowledgeRerankRequest.create
                    retrievalCandidates
                    3
                    (KnowledgeRerankBudget.create (TimeSpan.FromSeconds 1.0) 0 0 0m))
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let tokenizer = UnicodeWordTokenizer.create ()

        let result =
            KnowledgeContextAssembler.assemble
                authorization
                (KnowledgeContextAssemblyConfig.create tokenizer authorityThenCurrent 40 3 1 2)
                reranking

        match result with
        | KnowledgeContextAssemblyResult.Insufficient(reason, _) ->
            Assert.Fail $"Context assembly was unexpectedly insufficient: {reason}"
        | KnowledgeContextAssemblyResult.Assembled context ->
            Assert.AreEqual(KnowledgeContextAssembler.descriptor, KnowledgeAssembledContext.assembler context)
            Assert.AreEqual(2, KnowledgeAssembledContext.items context |> List.length)
            Assert.IsTrue(KnowledgeAssembledContext.tokenCount context <= 40)

            let text = KnowledgeAssembledContext.text context
            StringAssert.Contains(text, KnowledgeChunkId.value (KnowledgeIndexDocument.chunkId first))
            StringAssert.Contains(text, KnowledgeChunkId.value (KnowledgeIndexDocument.chunkId third))
            Assert.IsFalse(text.Contains(KnowledgeChunkId.value (KnowledgeIndexDocument.chunkId second)))

            context
            |> KnowledgeAssembledContext.items
            |> List.iter (fun item ->
                let citation = KnowledgeContextItem.citation item

                let document =
                    item |> KnowledgeContextItem.candidate |> KnowledgeRerankedCandidate.document

                let statement =
                    text.Substring(
                        KnowledgeContextCitation.statementOffset citation,
                        KnowledgeContextCitation.statementLength citation
                    )

                Assert.AreEqual(KnowledgeContextItem.text item, statement)

                Assert.AreEqual(
                    KnowledgeIndexDocument.documentId document,
                    KnowledgeContextCitation.documentId citation
                )

                Assert.AreEqual(
                    KnowledgeIndexDocument.documentVersion document,
                    KnowledgeContextCitation.documentVersion citation
                )

                Assert.AreEqual(ContentHash.computeText statement, KnowledgeContextCitation.quotedContentHash citation)
                Assert.IsFalse(KnowledgeContextCitation.locations citation |> List.isEmpty)
                StringAssert.Contains(text, KnowledgeContextCitation.marker citation))

            match
                context
                |> KnowledgeAssembledContext.decisions
                |> List.map KnowledgeContextCandidateDecision.outcome
            with
            | [ KnowledgeContextCandidateOutcome.Included 1
                KnowledgeContextCandidateOutcome.Suppressed(KnowledgeContextSuppressionReason.SourceLimit _)
                KnowledgeContextCandidateOutcome.Included 2 ] -> ()
            | outcomes -> Assert.Fail $"Unexpected context decisions: {outcomes}"

        let otherAuthorization =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "other-user") []
            |> fun principal ->
                AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
                |> Option.get

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeContextAssembler.assemble
                otherAuthorization
                (KnowledgeContextAssemblyConfig.create tokenizer authorityThenCurrent 20 3 1 1)
                reranking
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Context assembly suppresses stale and conflicting document versions``() =
        let source = KnowledgeSourceId.parse "source"
        let documentId = KnowledgeDocumentId.parse "versioned-document"

        let stale = documentVersionFrom source documentId 1 "version-one" 0 "stale evidence"

        let current =
            documentVersionFrom source documentId 2 "version-two" 1 "current evidence"

        let conflictingDocumentId = KnowledgeDocumentId.parse "conflicting-source-document"

        let conflictingSourceFirst =
            documentVersionFrom
                (KnowledgeSourceId.parse "source-a")
                conflictingDocumentId
                1
                "shared-version"
                2
                "first source evidence"

        let conflictingSourceSecond =
            documentVersionFrom
                (KnowledgeSourceId.parse "source-b")
                conflictingDocumentId
                1
                "shared-version"
                3
                "second source evidence"

        let forkedDocumentId = KnowledgeDocumentId.parse "forked-document"

        let forkFirst =
            documentVersionFrom source forkedDocumentId 1 "fork-a" 4 "first fork evidence"

        let forkSecond =
            documentVersionFrom source forkedDocumentId 1 "fork-b" 5 "second fork evidence"

        let reranking =
            [ stale
              current
              conflictingSourceFirst
              conflictingSourceSecond
              forkFirst
              forkSecond ]
            |> candidates
            |> fun retrievalCandidates ->
                KnowledgeRerankingExecutor.executeAsync
                    (KnowledgeScoreBasedReranker.create KnowledgeScoreBasedRerankerConfig.defaultConfig)
                    (KnowledgeRerankRequest.create
                        retrievalCandidates
                        6
                        (KnowledgeRerankBudget.create (TimeSpan.FromSeconds 1.0) 0 0 0m))
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()

        let result =
            KnowledgeContextAssembler.assemble
                authorization
                (KnowledgeContextAssemblyConfig.create (UnicodeWordTokenizer.create ()) authorityThenCurrent 100 6 6 1)
                reranking

        match result with
        | KnowledgeContextAssemblyResult.Insufficient(reason, _) ->
            Assert.Fail $"Context assembly was unexpectedly insufficient: {reason}"
        | KnowledgeContextAssemblyResult.Assembled context ->
            CollectionAssert.AreEqual(
                [| current |],
                context
                |> KnowledgeAssembledContext.items
                |> List.map (KnowledgeContextItem.candidate >> KnowledgeRerankedCandidate.document)
                |> List.toArray
            )

            match
                context
                |> KnowledgeAssembledContext.decisions
                |> List.map KnowledgeContextCandidateDecision.outcome
            with
            | [ KnowledgeContextCandidateOutcome.Suppressed(KnowledgeContextSuppressionReason.StaleDocumentVersion(staleDocumentId,
                                                                                                                   1,
                                                                                                                   2))
                KnowledgeContextCandidateOutcome.Included 1
                KnowledgeContextCandidateOutcome.Suppressed(KnowledgeContextSuppressionReason.ConflictingDocumentSources(firstConflictId,
                                                                                                                         firstSources))
                KnowledgeContextCandidateOutcome.Suppressed(KnowledgeContextSuppressionReason.ConflictingDocumentSources(secondConflictId,
                                                                                                                         secondSources))
                KnowledgeContextCandidateOutcome.Suppressed(KnowledgeContextSuppressionReason.ConflictingDocumentVersion(firstForkId,
                                                                                                                         1,
                                                                                                                         firstVersionIds))
                KnowledgeContextCandidateOutcome.Suppressed(KnowledgeContextSuppressionReason.ConflictingDocumentVersion(secondForkId,
                                                                                                                         1,
                                                                                                                         secondVersionIds)) ] ->
                Assert.AreEqual(documentId, staleDocumentId)
                Assert.AreEqual(conflictingDocumentId, firstConflictId)
                Assert.AreEqual(firstConflictId, secondConflictId)
                CollectionAssert.AreEqual(firstSources |> List.toArray, secondSources |> List.toArray)
                Assert.AreEqual(2, firstSources.Length)
                Assert.AreEqual(forkedDocumentId, firstForkId)
                Assert.AreEqual(firstForkId, secondForkId)
                CollectionAssert.AreEqual(firstVersionIds |> List.toArray, secondVersionIds |> List.toArray)
                Assert.AreEqual(2, firstVersionIds.Length)
            | outcomes -> Assert.Fail $"Unexpected freshness decisions: {outcomes}"

    [<TestMethod>]
    member _.``Context assembly applies explicit authority and currentness priority``() =
        let assessedAt = DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)

        let createDocument sourceName documentName trust modifiedAt sequence text =
            let assessment = TrustAssessment.create trust "preference test" assessedAt

            documentVersionWithTrustFrom
                (KnowledgeSourceId.parse sourceName)
                (KnowledgeDocumentId.parse documentName)
                1
                "version"
                assessment
                assessment
                (Some modifiedAt)
                modifiedAt
                sequence
                text

        let authoritative =
            createDocument
                "authoritative-source"
                "authoritative-document"
                KnowledgeTrust.Authoritative
                (assessedAt.AddDays 1.0)
                0
                "authoritative evidence"

        let unverified =
            createDocument
                "unverified-source"
                "unverified-document"
                KnowledgeTrust.Unverified
                (assessedAt.AddDays 2.0)
                1
                "unverified evidence"

        let organizationVerified =
            createDocument
                "organization-source"
                "organization-document"
                KnowledgeTrust.OrganizationVerified
                (assessedAt.AddDays 3.0)
                2
                "organization evidence"

        let reranking =
            [ unverified; authoritative; organizationVerified ]
            |> candidates
            |> fun retrievalCandidates ->
                KnowledgeRerankingExecutor.executeAsync
                    (KnowledgeScoreBasedReranker.create KnowledgeScoreBasedRerankerConfig.defaultConfig)
                    (KnowledgeRerankRequest.create
                        retrievalCandidates
                        3
                        (KnowledgeRerankBudget.create (TimeSpan.FromSeconds 1.0) 0 0 0m))
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()

        let assemble priority =
            let policy = KnowledgeContextSourcePreferencePolicy.create priority

            match
                KnowledgeContextAssembler.assemble
                    authorization
                    (KnowledgeContextAssemblyConfig.create (UnicodeWordTokenizer.create ()) policy 100 3 3 1)
                    reranking
            with
            | KnowledgeContextAssemblyResult.Insufficient(reason, _) ->
                Assert.Fail $"Context assembly was unexpectedly insufficient: {reason}"
                []
            | KnowledgeContextAssemblyResult.Assembled context ->
                context
                |> KnowledgeAssembledContext.decisions
                |> List.map (fun decision ->
                    let evidence = KnowledgeContextCandidateDecision.preference decision

                    decision
                    |> KnowledgeContextCandidateDecision.candidate
                    |> KnowledgeRerankedCandidate.document,
                    KnowledgeContextPreferenceEvidence.effectiveTrust evidence,
                    KnowledgeContextPreferenceEvidence.preferenceRank evidence)

        CollectionAssert.AreEqual(
            [| authoritative, KnowledgeTrust.Authoritative, 1
               organizationVerified, KnowledgeTrust.OrganizationVerified, 2
               unverified, KnowledgeTrust.Unverified, 3 |],
            assemble KnowledgeContextSourcePreferencePriority.AuthorityThenCurrent
            |> List.toArray
        )

        CollectionAssert.AreEqual(
            [| organizationVerified, KnowledgeTrust.OrganizationVerified, 1
               unverified, KnowledgeTrust.Unverified, 2
               authoritative, KnowledgeTrust.Authoritative, 3 |],
            assemble KnowledgeContextSourcePreferencePriority.CurrentThenAuthority
            |> List.toArray
        )

    [<TestMethod>]
    member _.``Context assembly represents token and diversity insufficiency``() =
        let first = document 0 "first evidence"
        let second = document 1 "second evidence"
        let retrievalCandidates = candidates [ first; second ]

        let reranking =
            KnowledgeRerankingExecutor.executeAsync
                (KnowledgeScoreBasedReranker.create KnowledgeScoreBasedRerankerConfig.defaultConfig)
                (KnowledgeRerankRequest.create
                    retrievalCandidates
                    2
                    (KnowledgeRerankBudget.create (TimeSpan.FromSeconds 1.0) 0 0 0m))
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let tokenizer = UnicodeWordTokenizer.create ()

        match
            KnowledgeContextAssembler.assemble
                authorization
                (KnowledgeContextAssemblyConfig.create tokenizer authorityThenCurrent 1 2 2 1)
                reranking
        with
        | KnowledgeContextAssemblyResult.Insufficient(KnowledgeContextInsufficiency.NoCandidates, decisions) ->
            Assert.AreEqual(2, decisions.Length)
        | result -> Assert.Fail $"Expected token insufficiency, received: {result}"

        match
            KnowledgeContextAssembler.assemble
                authorization
                (KnowledgeContextAssemblyConfig.create tokenizer authorityThenCurrent 20 2 1 2)
                reranking
        with
        | KnowledgeContextAssemblyResult.Insufficient(KnowledgeContextInsufficiency.BelowMinimumCandidates(2, 1), _) ->
            ()
        | result -> Assert.Fail $"Expected diversity insufficiency, received: {result}"
