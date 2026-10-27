namespace Nao.Semantics.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Semantics

[<TestClass>]
type RdfContractTests() =
    let authorization tenant =
        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let owner = authorization "tenant"
    let subject = RdfResource.Iri(RdfIri.parse "https://example.test/entities/subject")
    let predicate = RdfIri.parse "https://example.test/vocabulary/name"

    let asserted id =
        { Id = id
          Subject = subject
          Predicate = predicate
          Object = RdfTerm.Literal(RdfLiteral.language "Nao" "en-US")
          Graph = Some(RdfGraphName.Iri(RdfIri.parse "https://example.test/graphs/source"))
          Origin =
            RdfStatementOrigin.Asserted
                { SourceVersionId = "source-v1"
                  EvidenceAddresses = [ "document:1#0-3" ] } }

    [<TestMethod>]
    member _.TermsPreserveIriBlankNodeLiteralLanguageAndDatatypeLexicalForms() =
        let iri = RdfIri.parse "https://example.test/%7Eexact?q=a%2Fb"
        Assert.AreEqual("https://example.test/%7Eexact?q=a%2Fb", RdfIri.value iri)
        Assert.AreEqual("file:///tmp/ontology.ttl", RdfIri.parse "file:///tmp/ontology.ttl" |> RdfIri.value)
        Assert.AreEqual("node_1", RdfBlankNodeId.parse "node_1" |> RdfBlankNodeId.value)
        Assert.AreEqual("123.node", RdfBlankNodeId.parse "123.node" |> RdfBlankNodeId.value)

        let language = RdfLiteral.language "colour" "en-GB"
        Assert.AreEqual("colour", RdfLiteral.lexicalForm language)
        Assert.AreEqual(Some "en-gb", RdfLiteral.languageTag language)
        Assert.IsTrue(RdfLiteral.datatype language |> Option.isNone)
        Assert.AreEqual(Some "x-nao-private", RdfLiteral.language "private" "x-nao-private" |> RdfLiteral.languageTag)

        let datatype = RdfIri.parse "http://www.w3.org/2001/XMLSchema#integer"
        let typed = RdfLiteral.typed "001" datatype
        Assert.AreEqual("001", RdfLiteral.lexicalForm typed)
        Assert.AreEqual(Some datatype, RdfLiteral.datatype typed)

    [<TestMethod>]
    member _.InvalidTermsAndPrefixesFailClosed() =
        Assert.ThrowsExactly<ArgumentException>(fun () -> RdfIri.parse "relative/path" |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> RdfBlankNodeId.parse "bad label" |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> RdfLiteral.language "text" "not_a_language" |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            RdfPrefix.create "1invalid" (RdfIri.parse "https://example.test/ns#") |> ignore)
        |> ignore

    [<TestMethod>]
    member _.AssertedAndInferredStatementsRemainDistinguishable() =
        let source = asserted "asserted"

        let inferred =
            { source with
                Id = "inferred"
                Origin =
                    RdfStatementOrigin.Inferred
                        { EngineId = "reasoner"
                          EngineVersion = "1"
                          RuleOrAxiomId = "rule-1"
                          PremiseStatementIds = [ source.Id ] } }

        let dataset =
            RdfDataset.create
                (AuthorizationScope.identity owner)
                "dataset"
                1L
                [ RdfPrefix.create "ex" (RdfIri.parse "https://example.test/") ]
                [ source; inferred ]

        Assert.AreEqual([ source ], RdfDataset.asserted dataset)
        Assert.AreEqual([ inferred ], RdfDataset.inferred dataset)

    [<TestMethod>]
    member _.DatasetsAreAuthorizationPartitioned() =
        let dataset =
            RdfDataset.create (AuthorizationScope.identity owner) "dataset" 1L [] [ asserted "statement" ]

        Assert.AreSame(dataset, RdfDataset.requireOwner owner dataset)

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            RdfDataset.requireOwner (authorization "other") dataset |> ignore)
        |> ignore

    [<TestMethod>]
    member _.OntologyIdentityVersionImportsAndCompatibilityAreExplicit() =
        let ontology = RdfIri.parse "https://example.test/ontology"
        let version = RdfIri.parse "https://example.test/ontology/2026-10"
        let imported = RdfIri.parse "https://example.test/base"

        let descriptor =
            OntologyDescriptor.create ontology version (Set.singleton imported) (Map.ofList [ "runtime", "1" ])

        Assert.AreEqual(ontology, OntologyDescriptor.ontologyIri descriptor)
        Assert.AreEqual(version, OntologyDescriptor.versionIri descriptor)
        Assert.AreEqual(Set.singleton imported, OntologyDescriptor.imports descriptor)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            OntologyDescriptor.create ontology version (Set.singleton ontology) Map.empty
            |> ignore)
        |> ignore
