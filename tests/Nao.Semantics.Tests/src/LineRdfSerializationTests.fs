namespace Nao.Semantics.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Semantics
open Nao.Semantics.Rdf
open VDS.RDF.Parsing

[<TestClass>]
type LineRdfSerializationTests() =
    let authorization tenant =
        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let scope = authorization "tenant"
    let source = RdfImportSource.create "fixture-v1" "fixture:line-rdf"

    let semanticKey statement =
        statement.Subject, statement.Predicate, statement.Object, statement.Graph

    let asserted id graph =
        { Id = id
          Subject = RdfResource.Iri(RdfIri.parse "https://example.test/subject")
          Predicate = RdfIri.parse "https://example.test/predicate"
          Object = RdfTerm.Literal(RdfLiteral.language "line\n\"quoted\"" "en-GB")
          Graph = graph
          Origin =
            RdfStatementOrigin.Asserted
                { SourceVersionId = "source-v1"
                  EvidenceAddresses = [ "document:1#0-10" ] } }

    [<TestMethod>]
    member _.NTriplesFixtureRoundTripsRdfTermsWithoutSemanticLoss() =
        let fixture =
            """
# RDF 1.1 N-Triples fixture
_:123.node <https://example.test/name> "Nao\n\"runtime\""@en-GB .
<https://example.test/item> <https://example.test/count> "001"^^<http://www.w3.org/2001/XMLSchema#integer> .
<https://example.test/item> <https://example.test/link> <https://example.test/%7Etarget> .
"""

        let imported = NTriples.importAsserted scope "fixture" 1L source fixture
        let statements = RdfDataset.statements imported

        Assert.AreEqual(3, statements.Length)
        Assert.AreEqual(3, RdfDataset.asserted imported |> List.length)
        Assert.AreEqual(0, RdfDataset.inferred imported |> List.length)
        Assert.AreEqual(AuthorizationScope.identity scope, RdfDataset.owner imported)

        let exported = NTriples.exportAsserted scope imported

        let reimported =
            NTriples.importAsserted scope "fixture-roundtrip" 1L source exported.Content

        CollectionAssert.AreEqual(
            statements |> List.map semanticKey |> List.sort |> List.toArray,
            RdfDataset.statements reimported
            |> List.map semanticKey
            |> List.sort
            |> List.toArray
        )

        Assert.IsTrue(
            exported.Losses
            |> List.exists (fun loss -> loss.Aspect = RdfSerializationLossAspect.StatementMetadata)
        )

    [<TestMethod>]
    member _.NQuadsFixturePreservesDefaultNamedAndBlankNodeGraphs() =
        let fixture =
            """
<https://example.test/s> <https://example.test/p> "default" .
<https://example.test/s> <https://example.test/p> "named"@x-nao-private <https://example.test/graph> .
_:subject <https://example.test/p> <https://example.test/o> _:123.graph .
"""

        let imported = NQuads.importAsserted scope "fixture" 1L source fixture
        let graphs = RdfDataset.statements imported |> List.map _.Graph |> Set.ofList

        Assert.IsTrue(graphs.Contains None)
        Assert.IsTrue(graphs.Contains(Some(RdfGraphName.Iri(RdfIri.parse "https://example.test/graph"))))
        Assert.IsTrue(graphs.Contains(Some(RdfGraphName.BlankNode(RdfBlankNodeId.parse "123.graph"))))

        let exported = NQuads.exportAsserted scope imported

        let reimported =
            NQuads.importAsserted scope "fixture-roundtrip" 1L source exported.Content

        CollectionAssert.AreEqual(
            RdfDataset.statements imported
            |> List.map semanticKey
            |> List.sort
            |> List.toArray,
            RdfDataset.statements reimported
            |> List.map semanticKey
            |> List.sort
            |> List.toArray
        )

    [<TestMethod>]
    member _.ExportOmitsInferenceAndReportsEveryNonRepresentableBoundary() =
        let sourceStatement = asserted "asserted" None

        let inferred =
            { sourceStatement with
                Id = "inferred"
                Origin =
                    RdfStatementOrigin.Inferred
                        { EngineId = "reasoner"
                          EngineVersion = "1"
                          RuleOrAxiomId = "rule"
                          PremiseStatementIds = [ sourceStatement.Id ] } }

        let dataset =
            RdfDataset.create
                (AuthorizationScope.identity scope)
                "dataset"
                1L
                [ RdfPrefix.create "ex" (RdfIri.parse "https://example.test/") ]
                [ sourceStatement; inferred ]

        let exported = NQuads.exportAsserted scope dataset

        Assert.AreEqual(1, exported.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length)

        CollectionAssert.AreEquivalent(
            [| RdfSerializationLossAspect.DatasetPrefixes
               RdfSerializationLossAspect.StatementMetadata
               RdfSerializationLossAspect.InferredStatementsOmitted |],
            exported.Losses |> List.map _.Aspect |> List.toArray
        )

        Assert.IsFalse(exported.Content.Contains("inferred", StringComparison.Ordinal))

    [<TestMethod>]
    member _.NTriplesRejectsNamedGraphsAndForeignOwners() =
        let named = Some(RdfGraphName.Iri(RdfIri.parse "https://example.test/graph"))

        let dataset =
            RdfDataset.create (AuthorizationScope.identity scope) "dataset" 1L [] [ asserted "asserted" named ]

        Assert.ThrowsExactly<ArgumentException>(fun () -> NTriples.exportAsserted scope dataset |> ignore)
        |> ignore

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            NQuads.exportAsserted (authorization "other") dataset |> ignore)
        |> ignore

    [<TestMethod>]
    member _.MalformedInputFailsExplicitly() =
        Assert.ThrowsExactly<RdfParseException>(fun () ->
            NTriples.importAsserted
                scope
                "invalid"
                1L
                source
                "<https://example.test/s> <https://example.test/p> \"unterminated ."
            |> ignore)
        |> ignore
