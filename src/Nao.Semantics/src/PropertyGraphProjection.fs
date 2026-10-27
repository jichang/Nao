namespace Nao.Semantics

open System
open Nao.Agents

type PropertyGraphProjectionConfig =
    private
        { EntityNamespace: RdfIri
          PredicateNamespace: RdfIri
          Graph: RdfGraphName option }

[<RequireQualifiedAccess>]
module PropertyGraphProjectionConfig =
    let create entityNamespace predicateNamespace graph =
        { EntityNamespace = entityNamespace
          PredicateNamespace = predicateNamespace
          Graph = graph }

[<RequireQualifiedAccess>]
type PropertyGraphProjectionAspect =
    | UndirectedRelation
    | NodeMetadata
    | RelationMetadata

type PropertyGraphProjectionLoss =
    { ElementId: string
      Aspect: PropertyGraphProjectionAspect
      Detail: string }

type PropertyGraphProjection =
    { Dataset: RdfDataset
      Losses: PropertyGraphProjectionLoss list }

[<RequireQualifiedAccess>]
module PropertyGraphProjection =
    let private appendPathSegment (namespaceIri: RdfIri) (value: string) =
        let namespaceValue = RdfIri.value namespaceIri

        let separator =
            if namespaceValue.EndsWith("/", StringComparison.Ordinal) then
                ""
            else
                "/"

        RdfIri.parse $"{namespaceValue}{separator}{Uri.EscapeDataString value}"

    let private relationMetadataLoss (relation: GraphRelation) =
        let hasValidity =
            relation.Validity.ValidFrom.IsSome || relation.Validity.ValidTo.IsSome

        if Map.isEmpty relation.Properties && not hasValidity && relation.Confidence = 1.0 then
            []
        else
            [ { ElementId = relation.Id
                Aspect = PropertyGraphProjectionAspect.RelationMetadata
                Detail =
                  "RDF edge projection does not encode property-graph relation properties, temporal validity, or confidence." } ]

    let private nodeMetadataLoss (node: GraphNode) =
        [ { ElementId = node.Id
            Aspect = PropertyGraphProjectionAspect.NodeMetadata
            Detail =
              "RDF edge projection preserves node identity only; entity type, aliases, properties, provenance, and timestamps are not encoded." } ]

    let project
        authorization
        datasetId
        revision
        prefixes
        (config: PropertyGraphProjectionConfig)
        (snapshot: GraphSnapshot)
        =
        GraphContracts.validateSnapshot authorization snapshot

        let projectRelation (relation: GraphRelation) =
            match relation.Direction with
            | GraphRelationDirection.Directed ->
                let statement =
                    { Id = relation.Id
                      Subject = appendPathSegment config.EntityNamespace relation.Subject |> RdfResource.Iri
                      Predicate = appendPathSegment config.PredicateNamespace relation.Predicate
                      Object =
                        appendPathSegment config.EntityNamespace relation.Object
                        |> RdfResource.Iri
                        |> RdfTerm.Resource
                      Graph = config.Graph
                      Origin =
                        RdfStatementOrigin.Asserted
                            { SourceVersionId = relation.Provenance.SourceVersionId
                              EvidenceAddresses = relation.Provenance.EvidenceAddresses } }

                Some statement, relationMetadataLoss relation
            | GraphRelationDirection.Undirected ->
                None,
                [ { ElementId = relation.Id
                    Aspect = PropertyGraphProjectionAspect.UndirectedRelation
                    Detail = "An undirected property-graph relation has no lossless RDF triple representation." } ]

        let projected = snapshot.Relations |> List.map projectRelation
        let statements = projected |> List.choose fst

        let losses =
            [ yield! snapshot.Nodes |> List.collect nodeMetadataLoss
              yield! projected |> List.collect snd ]

        { Dataset = RdfDataset.create snapshot.Owner datasetId revision prefixes statements
          Losses = losses }
