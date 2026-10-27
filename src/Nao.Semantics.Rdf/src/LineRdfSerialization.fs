namespace Nao.Semantics.Rdf

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Nao.Agents
open Nao.Semantics
open VDS.RDF
open VDS.RDF.Parsing

[<RequireQualifiedAccess>]
type RdfSerializationLossAspect =
    | DatasetPrefixes
    | StatementMetadata
    | InferredStatementsOmitted

type RdfSerializationLoss =
    { Aspect: RdfSerializationLossAspect
      StatementIds: string list
      Detail: string }

type RdfExportResult =
    { Content: string
      Losses: RdfSerializationLoss list }

type RdfImportSource =
    private
        { SourceVersionId: string
          EvidenceAddress: string }

[<RequireQualifiedAccess>]
module RdfImportSource =
    let private canonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"{name} must be canonical and non-blank."

    let create sourceVersionId evidenceAddress =
        canonical (nameof sourceVersionId) sourceVersionId
        canonical (nameof evidenceAddress) evidenceAddress

        { SourceVersionId = sourceVersionId
          EvidenceAddress = evidenceAddress }

[<RequireQualifiedAccess>]
module internal LineRdf =
    let private escapeCodeUnit (character: char) = $"\\u{int character:X4}"

    let private escapeIri (value: string) =
        let builder = StringBuilder(value.Length)

        value
        |> Seq.iter (fun character ->
            if
                int character <= 0x20
                || character = '<'
                || character = '>'
                || character = '"'
                || character = '{'
                || character = '}'
                || character = '|'
                || character = '^'
                || character = '`'
                || character = '\\'
            then
                builder.Append(escapeCodeUnit character) |> ignore
            else
                builder.Append(character) |> ignore)

        builder.ToString()

    let private escapeLiteral (value: string) =
        let builder = StringBuilder(value.Length)

        value
        |> Seq.iter (fun character ->
            match character with
            | '\\' -> builder.Append("\\\\") |> ignore
            | '"' -> builder.Append("\\\"") |> ignore
            | '\n' -> builder.Append("\\n") |> ignore
            | '\r' -> builder.Append("\\r") |> ignore
            | '\t' -> builder.Append("\\t") |> ignore
            | character when Char.IsControl character -> builder.Append(escapeCodeUnit character) |> ignore
            | character -> builder.Append(character) |> ignore)

        builder.ToString()

    let iri (value: RdfIri) = $"<{RdfIri.value value |> escapeIri}>"

    let resource (value: RdfResource) =
        match value with
        | RdfResource.Iri value -> iri value
        | RdfResource.BlankNode value -> $"_:{RdfBlankNodeId.value value}"

    let term (value: RdfTerm) =
        match value with
        | RdfTerm.Resource value -> resource value
        | RdfTerm.Literal value ->
            let lexicalForm = RdfLiteral.lexicalForm value |> escapeLiteral

            match RdfLiteral.languageTag value, RdfLiteral.datatype value with
            | Some language, None -> $"\"{lexicalForm}\"@{language}"
            | None, Some datatype -> $"\"{lexicalForm}\"^^{iri datatype}"
            | None, None -> $"\"{lexicalForm}\""
            | Some _, Some _ -> invalidOp "RDF literals cannot have both a language and datatype."

    let graphName (value: RdfGraphName) =
        match value with
        | RdfGraphName.Iri value -> iri value
        | RdfGraphName.BlankNode value -> $"_:{RdfBlankNodeId.value value}"

    let renderStatement includeGraph (statement: RdfStatement) =
        let graph =
            if includeGraph then
                statement.Graph
                |> Option.map (fun value -> $" {graphName value}")
                |> Option.defaultValue ""
            else
                ""

        $"{resource statement.Subject} {iri statement.Predicate} {term statement.Object}{graph} ."

    let losses (dataset: RdfDataset) (inferred: RdfStatement list) (exported: RdfStatement list) =
        [ if not (Map.isEmpty (RdfDataset.prefixes dataset)) then
              { Aspect = RdfSerializationLossAspect.DatasetPrefixes
                StatementIds = []
                Detail = "N-Triples and N-Quads do not serialize dataset prefix declarations." }

          if not (List.isEmpty exported) then
              { Aspect = RdfSerializationLossAspect.StatementMetadata
                StatementIds = exported |> List.map _.Id
                Detail = "Line RDF serializations do not encode Nao statement identities or source provenance." }

          if not (List.isEmpty inferred) then
              { Aspect = RdfSerializationLossAspect.InferredStatementsOmitted
                StatementIds = inferred |> List.map _.Id
                Detail =
                  "Inferred statements were omitted because line RDF serializations cannot preserve their asserted/inferred boundary." } ]

    let export authorization includeGraph (dataset: RdfDataset) =
        let dataset = RdfDataset.requireOwner authorization dataset
        let inferred = RdfDataset.inferred dataset
        let asserted = RdfDataset.asserted dataset

        let content =
            asserted
            |> List.map (renderStatement includeGraph)
            |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
            |> String.concat "\n"
            |> fun value -> if String.IsNullOrEmpty value then value else value + "\n"

        { Content = content
          Losses = losses dataset inferred asserted }

    let private fromNode (node: INode) =
        match node with
        | :? IUriNode as iri -> RdfResource.Iri(RdfIri.parse iri.Uri.AbsoluteUri)
        | :? IBlankNode as blank -> RdfResource.BlankNode(RdfBlankNodeId.parse blank.InternalID)
        | _ -> invalidOp $"RDF node type '{node.NodeType}' cannot be used as a resource."

    let private fromTerm (node: INode) =
        match node with
        | :? IUriNode
        | :? IBlankNode -> fromNode node |> RdfTerm.Resource
        | :? ILiteralNode as literal ->
            let value =
                if not (String.IsNullOrEmpty literal.Language) then
                    RdfLiteral.language literal.Value literal.Language
                elif not (isNull literal.DataType) then
                    RdfLiteral.typed literal.Value (RdfIri.parse literal.DataType.AbsoluteUri)
                else
                    RdfLiteral.plain literal.Value

            RdfTerm.Literal value
        | _ -> invalidOp $"RDF node type '{node.NodeType}' cannot be used as an RDF object."

    let private fromGraphName (name: IRefNode) =
        match name with
        | :? IUriNode as iri -> RdfGraphName.Iri(RdfIri.parse iri.Uri.AbsoluteUri)
        | :? IBlankNode as blank -> RdfGraphName.BlankNode(RdfBlankNodeId.parse blank.InternalID)
        | _ -> invalidOp $"RDF node type '{name.NodeType}' cannot identify a graph."

    let private statementKey
        (subject: RdfResource)
        (predicate: RdfIri)
        (object': RdfTerm)
        (graph: RdfGraphName option)
        =
        let graphValue =
            graph
            |> Option.map (fun value -> $" {graphName value}")
            |> Option.defaultValue ""

        $"{resource subject} {iri predicate} {term object'}{graphValue} ."

    let private statementId (key: string) =
        let digest: byte array = SHA256.HashData(Encoding.UTF8.GetBytes key)
        $"rdf-{Convert.ToHexStringLower digest}"

    let private importedStatement (source: RdfImportSource) (graph: RdfGraphName option) (triple: Triple) =
        let subject = fromNode triple.Subject

        let predicate =
            match triple.Predicate with
            | :? IUriNode as value -> RdfIri.parse value.Uri.AbsoluteUri
            | value -> invalidOp $"RDF predicate node type '{value.NodeType}' is invalid."

        let object' = fromTerm triple.Object
        let key = statementKey subject predicate object' graph
        let id = statementId key

        { Id = id
          Subject = subject
          Predicate = predicate
          Object = object'
          Graph = graph
          Origin =
            RdfStatementOrigin.Asserted
                { SourceVersionId = source.SourceVersionId
                  EvidenceAddresses = [ $"{source.EvidenceAddress}#rdf={id}" ] } }

    let importGraph authorization datasetId revision source (graph: IGraph) =
        let statements =
            graph.Triples
            |> Seq.map (importedStatement source None)
            |> Seq.sortBy _.Id
            |> Seq.toList

        RdfDataset.create (AuthorizationScope.identity authorization) datasetId revision [] statements

    let importStore authorization datasetId revision source (store: ITripleStore) =
        let statements =
            store.Graphs
            |> Seq.collect (fun graph ->
                let name =
                    if isNull graph.Name then
                        None
                    else
                        Some(fromGraphName graph.Name)

                graph.Triples |> Seq.map (importedStatement source name))
            |> Seq.sortBy _.Id
            |> Seq.toList

        RdfDataset.create (AuthorizationScope.identity authorization) datasetId revision [] statements

[<RequireQualifiedAccess>]
module NTriples =
    let exportAsserted authorization dataset =
        let namedStatements =
            RdfDataset.requireOwner authorization dataset
            |> RdfDataset.asserted
            |> List.filter (fun statement -> statement.Graph.IsSome)

        if not (List.isEmpty namedStatements) then
            invalidArg (nameof dataset) "N-Triples cannot represent named graphs; use N-Quads for this dataset."

        LineRdf.export authorization false dataset

    let importAsserted authorization datasetId revision source content =
        if isNull content then
            nullArg (nameof content)

        let graph = new Graph()
        NTriplesParser().Load(graph, new StringReader(content))
        LineRdf.importGraph authorization datasetId revision source graph

[<RequireQualifiedAccess>]
module NQuads =
    let exportAsserted authorization dataset =
        LineRdf.export authorization true dataset

    let importAsserted authorization datasetId revision source content =
        if isNull content then
            nullArg (nameof content)

        let store = new TripleStore()
        NQuadsParser().Load(store, new StringReader(content))
        LineRdf.importStore authorization datasetId revision source store
