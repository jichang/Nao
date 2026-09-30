namespace Nao.Knowledge.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass>]
type IndexContractTests() =
    let now = DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)
    let tenant = TenantId.parse "tenant"
    let workspace = WorkspaceId.parse "workspace"
    let ownerUser = UserId.parse "owner"
    let readerUser = UserId.parse "reader"
    let readerGroup = GroupId.parse "readers"
    let namespaceName = KnowledgeIndexNamespace.parse "primary"
    let lifecycle = KnowledgeLifecycle.active None false
    let testComponent = ComponentVersion.create "test" "1"

    let principal user groups roles =
        SecurityPrincipal.createAuthenticated tenant user None groups roles Map.empty AuthenticationStrength.MultiFactor

    let authorization user groups roles =
        AuthorizationScope.tryCreate (principal user groups roles) None workspace None
        |> Option.get

    let createDocument access =
        let owner = authorization ownerUser [] Set.empty
        let ownerIdentity = AuthorizationScope.identity owner

        let source =
            KnowledgeSource.create
                (KnowledgeSourceId.parse "source")
                (Uri "https://example.test/source")
                ownerIdentity
                DataClassification.Internal
                (TrustAssessment.create KnowledgeTrust.OrganizationVerified "test" now)
                (Map.ofList [ "system", "docs" ])
                now
                lifecycle

        let documentId = KnowledgeDocumentId.parse "document"
        let contentHash = ContentHash.computeText "content"

        let version =
            KnowledgeDocumentVersion.create
                documentId
                source.Id
                1
                contentHash
                (SourceAcquisition.create testComponent contentHash now Map.empty)
                "text/plain"
                (Some "en")
                7L
                (Some now)
                now
                DataClassification.Internal
                source.Trust
                (Map.ofList [ "release", "current" ])
                lifecycle

        let location = SourceLocation.create 0L 7L 0 7 None (Some 1) (Some 1) None

        let unit =
            ContentUnit.create
                version.Id
                0
                ContentUnitKind.Paragraph
                "content"
                location
                testComponent
                []
                Map.empty
                lifecycle

        let chunk =
            KnowledgeChunk.create
                version.Id
                0
                KnowledgeChunkRole.Leaf
                None
                [ unit.Id ]
                unit.Text
                [ location ]
                testComponent
                (ContentHash.computeText "chunker")
                []
                (Map.ofList [ "section", "intro" ])
                lifecycle

        KnowledgeIndexDocument.create namespaceName access source version chunk now, owner

    [<TestMethod>]
    member _.``Index document pins searchable identity and prefixed metadata``() =
        let document, _ = createDocument KnowledgeIndexAccess.ownerOnly

        Assert.AreEqual(namespaceName, KnowledgeIndexDocument.namespaceName document)
        Assert.AreEqual("content", KnowledgeIndexDocument.text document)
        Assert.AreEqual("docs", KnowledgeIndexDocument.metadata document |> Map.find "source.system")
        Assert.AreEqual("current", KnowledgeIndexDocument.metadata document |> Map.find "version.release")
        Assert.AreEqual("intro", KnowledgeIndexDocument.metadata document |> Map.find "chunk.section")

    [<TestMethod>]
    member _.``ACL requires tenant workspace session and an explicit grant``() =
        let access =
            KnowledgeIndexAccess.create [ readerUser ] [ readerGroup ] [ "knowledge-reader" ]

        let document, owner = createDocument access
        let ownerIdentity = KnowledgeIndexDocument.owner document

        Assert.IsTrue(KnowledgeIndexAccess.allows owner ownerIdentity access)
        Assert.IsTrue(KnowledgeIndexAccess.allows (authorization readerUser [] Set.empty) ownerIdentity access)

        let grouped = authorization (UserId.parse "group-member") [ readerGroup ] Set.empty
        Assert.IsTrue(KnowledgeIndexAccess.allows grouped ownerIdentity access)

        let roleReader =
            authorization (UserId.parse "role-member") [] (Set.singleton "knowledge-reader")

        Assert.IsTrue(KnowledgeIndexAccess.allows roleReader ownerIdentity access)

        let ungranted = authorization (UserId.parse "other") [] Set.empty
        Assert.IsFalse(KnowledgeIndexAccess.allows ungranted ownerIdentity access)

        let otherWorkspace =
            AuthorizationScope.tryCreate (principal readerUser [] Set.empty) None (WorkspaceId.parse "other") None
            |> Option.get

        Assert.IsFalse(KnowledgeIndexAccess.allows otherWorkspace ownerIdentity access)

    [<TestMethod>]
    member _.``Index filters reject inverted time ranges``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeIndexFilter.create [] [] Map.empty (Some now) (Some(now.AddMinutes -1.0)) None None
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Vector queries enforce the complete embedding profile dimensions``() =
        let profile =
            EmbeddingCapabilityDescriptor.create
                (ComponentVersion.create "embedding-model" "1")
                3
                EmbeddingNormalization.UnitLength
                VectorDistanceMetric.Cosine
                (ComponentVersion.create "preprocessing" "1")
                (EmbeddingCapabilityLimits.create 8 1000 100 800)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            VectorIndexQuery.create namespaceName profile [| 1.0; 2.0 |] KnowledgeIndexFilter.none 5
            |> ignore)
        |> ignore

        let query =
            VectorIndexQuery.create namespaceName profile [| 1.0; 2.0; 3.0 |] KnowledgeIndexFilter.none 5

        let returned = VectorIndexQuery.values query
        returned[0] <- 99.0
        Assert.AreEqual(1.0, VectorIndexQuery.values query |> Array.head)

    [<TestMethod>]
    member _.``Lexical and graph queries require bounded non-empty input``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            LexicalIndexQuery.create namespaceName " " KnowledgeIndexFilter.none 5 |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> GraphIndexQuery.create namespaceName [] [] 1 5 |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Index mutations require work and report non-negative evidence``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeIndexMutation.create namespaceName [] [] None [] |> ignore)
        |> ignore

        let retiredSourceId = KnowledgeSourceId.parse "retired-source"

        let mutation =
            KnowledgeIndexMutation.create
                namespaceName
                [ "record" ]
                [ KnowledgeChunkId.parse "chunk_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" ]
                None
                [ retiredSourceId ]

        Assert.AreEqual([ "record" ], KnowledgeIndexMutation.upserts mutation)
        Assert.AreEqual(1, KnowledgeIndexMutation.deleteChunkIds mutation |> Set.count)
        Assert.AreEqual(Set.singleton retiredSourceId, KnowledgeIndexMutation.retireSourceIds mutation)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeIndexMutationResult.create namespaceName 1 -1 0 now |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeIndexMutationResult.create namespaceName 1 0 -1 now |> ignore)
        |> ignore
