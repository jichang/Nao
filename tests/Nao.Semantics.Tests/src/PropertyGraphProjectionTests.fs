namespace Nao.Semantics.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Semantics

[<TestClass>]
type PropertyGraphProjectionTests() =
    let authorization tenant =
        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let scope = authorization "tenant"
    let timestamp = DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)

    let node id =
        { Id = id
          EntityType = "person"
          Aliases = Set.empty
          Properties = Map.empty
          Provenance = []
          CreatedAt = timestamp
          UpdatedAt = timestamp }

    let relation id direction =
        { Id = id
          Subject = "alice smith"
          Predicate = "knows/personally"
          Object = "bob"
          Direction = direction
          Properties = Map.empty
          Validity = { ValidFrom = None; ValidTo = None }
          Confidence = 1.0
          Provenance =
            { SourceVersionId = "source-v1"
              ExtractionOperationId = "operation-v1"
              EvidenceAddresses = [ "document:1#10-20" ] }
          AssertedAt = timestamp }

    let config =
        PropertyGraphProjectionConfig.create
            (RdfIri.parse "https://example.test/entities")
            (RdfIri.parse "https://example.test/vocabulary/")
            (Some(RdfGraphName.Iri(RdfIri.parse "https://example.test/graphs/projected")))

    [<TestMethod>]
    member _.DirectedRelationsProjectWithStableIdentityAndSourceEvidence() =
        let snapshot =
            { Owner = AuthorizationScope.identity scope
              CreatedAt = timestamp
              Nodes = [ node "alice smith"; node "bob" ]
              Relations = [ relation "relation-1" GraphRelationDirection.Directed ] }

        let projection =
            PropertyGraphProjection.project scope "projection" 1L [] config snapshot

        let statement = RdfDataset.statements projection.Dataset |> List.exactlyOne

        Assert.AreEqual("relation-1", statement.Id)
        Assert.AreEqual(RdfResource.Iri(RdfIri.parse "https://example.test/entities/alice%20smith"), statement.Subject)

        Assert.AreEqual(RdfIri.parse "https://example.test/vocabulary/knows%2Fpersonally", statement.Predicate)

        match statement.Origin with
        | RdfStatementOrigin.Asserted provenance ->
            Assert.AreEqual("source-v1", provenance.SourceVersionId)
            CollectionAssert.AreEqual([| "document:1#10-20" |], provenance.EvidenceAddresses |> List.toArray)
        | RdfStatementOrigin.AuthorizedUpdate _ ->
            Assert.Fail "Projected source assertions must not become authorized updates."
        | RdfStatementOrigin.Inferred _ ->
            Assert.Fail "Projected source assertions must not become inferred statements."

        Assert.AreEqual(2, projection.Losses.Length)

    [<TestMethod>]
    member _.UndirectedRelationsAreReportedRatherThanGivenFalseDirectionality() =
        let snapshot =
            { Owner = AuthorizationScope.identity scope
              CreatedAt = timestamp
              Nodes = [ node "alice smith"; node "bob" ]
              Relations = [ relation "relation-1" GraphRelationDirection.Undirected ] }

        let projection =
            PropertyGraphProjection.project scope "projection" 1L [] config snapshot

        Assert.AreEqual([], RdfDataset.statements projection.Dataset)

        Assert.IsTrue(
            projection.Losses
            |> List.exists (fun loss ->
                loss.ElementId = "relation-1"
                && loss.Aspect = PropertyGraphProjectionAspect.UndirectedRelation)
        )

    [<TestMethod>]
    member _.ProjectionRejectsForeignSnapshots() =
        let snapshot =
            { Owner = AuthorizationScope.identity (authorization "other")
              CreatedAt = timestamp
              Nodes = []
              Relations = [] }

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            PropertyGraphProjection.project scope "projection" 1L [] config snapshot
            |> ignore)
        |> ignore
