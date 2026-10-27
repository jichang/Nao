namespace Nao.Semantics.Rdf

open System
open System.Diagnostics
open System.IO
open System.Threading
open Nao.Semantics
open VDS.RDF
open VDS.RDF.Parsing
open VDS.RDF.Query
open VDS.RDF.Query.Datasets
open VDS.RDF.Query.Patterns

[<RequireQualifiedAccess>]
module LocalSparqlEndpoint =
    let private canonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"{name} must be canonical and non-blank."

    let private parameterNode (factory: INodeFactory) =
        function
        | SparqlParameterValue.Iri value -> factory.CreateUriNode(Uri(RdfIri.value value)) :> INode
        | SparqlParameterValue.Literal value ->
            match RdfLiteral.languageTag value, RdfLiteral.datatype value with
            | Some language, None -> factory.CreateLiteralNode(RdfLiteral.lexicalForm value, language) :> INode
            | None, Some datatype ->
                factory.CreateLiteralNode(RdfLiteral.lexicalForm value, Uri(RdfIri.value datatype)) :> INode
            | None, None -> factory.CreateLiteralNode(RdfLiteral.lexicalForm value) :> INode
            | Some _, Some _ -> invalidOp "RDF literals cannot have both a language and datatype."

    let private bind request =
        let parameterized = SparqlParameterizedString()
        parameterized.CommandText <- request |> SparqlReadRequest.template |> SparqlQueryTemplate.text
        let factory = NodeFactory() :> INodeFactory

        request
        |> SparqlReadRequest.parameters
        |> Map.iter (fun name value -> parameterized.SetParameter(name, parameterNode factory value))

        parameterized.ToString()

    let private queryKindMatches expected actual =
        match expected, actual with
        | SparqlReadQueryKind.Ask, SparqlQueryType.Ask
        | SparqlReadQueryKind.Construct, SparqlQueryType.Construct -> true
        | SparqlReadQueryKind.Select,
          (SparqlQueryType.Select | SparqlQueryType.SelectAll | SparqlQueryType.SelectDistinct | SparqlQueryType.SelectReduced | SparqlQueryType.SelectAllDistinct | SparqlQueryType.SelectAllReduced) ->
            true
        | _ -> false

    let rec private inspectPattern (pattern: GraphPattern) =
        let localCount, localHasService =
            pattern.TriplePatterns
            |> Seq.fold
                (fun (count, service) triplePattern ->
                    match triplePattern with
                    | :? SubQueryPattern as subquery ->
                        let nestedCount, nestedService = inspectPattern subquery.SubQuery.RootGraphPattern

                        count + nestedCount, service || nestedService
                    | _ -> count + 1, service)
                (0, false)

        let childCount, childHasService =
            pattern.ChildGraphPatterns
            |> Seq.map inspectPattern
            |> Seq.fold
                (fun (count, service) (nextCount, nextService) -> count + nextCount, service || nextService)
                (0, false)

        localCount + childCount, pattern.IsService || localHasService || childHasService

    let private datasetStore dataset =
        let content =
            dataset
            |> RdfDataset.statements
            |> List.map (LineRdf.renderStatement true)
            |> String.concat "\n"

        let store = new TripleStore()
        NQuadsParser().Load(store, new StringReader(content))
        store

    let private fromResource (node: INode) =
        match node with
        | :? IUriNode as iri -> RdfResource.Iri(RdfIri.parse iri.Uri.AbsoluteUri)
        | :? IBlankNode as blank -> RdfResource.BlankNode(RdfBlankNodeId.parse blank.InternalID)
        | _ -> invalidOp $"SPARQL result node type '{node.NodeType}' is not an RDF resource."

    let private fromTerm (node: INode) =
        match node with
        | :? IUriNode
        | :? IBlankNode -> fromResource node |> RdfTerm.Resource
        | :? ILiteralNode as literal ->
            if not (String.IsNullOrEmpty literal.Language) then
                RdfLiteral.language literal.Value literal.Language
            elif not (isNull literal.DataType) then
                RdfLiteral.typed literal.Value (RdfIri.parse literal.DataType.AbsoluteUri)
            else
                RdfLiteral.plain literal.Value
            |> RdfTerm.Literal
        | _ -> invalidOp $"SPARQL result node type '{node.NodeType}' is not supported."

    let private selectResult (resultSet: SparqlResultSet) =
        let variables = resultSet.Variables |> Seq.toList

        let rows =
            resultSet.Results
            |> Seq.map (fun row ->
                variables
                |> List.map (fun variable ->
                    variable,
                    if row.HasBoundValue variable then
                        Some(fromTerm row[variable])
                    else
                        None)
                |> Map.ofList)
            |> Seq.toList

        SparqlReadResult.Select(variables, rows), rows.Length

    let private constructResult (graph: IGraph) =
        let statements =
            graph.Triples
            |> Seq.map (fun triple ->
                let predicate =
                    match triple.Predicate with
                    | :? IUriNode as iri -> RdfIri.parse iri.Uri.AbsoluteUri
                    | node -> invalidOp $"SPARQL constructed predicate type '{node.NodeType}' is invalid."

                { Subject = fromResource triple.Subject
                  Predicate = predicate
                  Object = fromTerm triple.Object })
            |> Seq.toList

        SparqlReadResult.Construct statements, statements.Length

    let private result queryKind (value: obj) =
        match queryKind, value with
        | SparqlReadQueryKind.Ask, (:? SparqlResultSet as resultSet) -> SparqlReadResult.Ask resultSet.Result, 1
        | SparqlReadQueryKind.Select, (:? SparqlResultSet as resultSet) -> selectResult resultSet
        | SparqlReadQueryKind.Construct, (:? IGraph as graph) -> constructResult graph
        | _ -> invalidOp "The SPARQL engine returned a result shape that does not match the validated query kind."

    let create endpointId endpointVersion (timeProvider: TimeProvider) : SparqlReadEndpoint =
        canonical (nameof endpointId) endpointId
        canonical (nameof endpointVersion) endpointVersion

        { ExecuteAsync =
            fun authorization dataset request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    let dataset = RdfDataset.requireOwner authorization dataset
                    let startedAt = timeProvider.GetUtcNow()
                    let stopwatch = Stopwatch.StartNew()
                    let budget = SparqlReadRequest.budget request
                    let template = SparqlReadRequest.template request

                    let complete outcome =
                        stopwatch.Stop()

                        { Provenance =
                            { EndpointId = endpointId
                              EndpointVersion = endpointVersion
                              DatasetId = RdfDataset.id dataset
                              DatasetRevision = RdfDataset.revision dataset
                              QueryId = SparqlQueryTemplate.id template
                              StartedAt = startedAt
                              Duration = stopwatch.Elapsed }
                          Permission = None
                          Outcome = outcome }

                    let queryText = SparqlQueryTemplate.text template
                    let queryTextLimit = SparqlQueryBudget.maxQueryTextLength budget
                    let statementLimit = SparqlQueryBudget.maxDatasetStatements budget
                    let statements = RdfDataset.statements dataset

                    if queryText.Length > queryTextLimit then
                        return
                            complete (SparqlExecutionOutcome.QueryTextLimitExceeded(queryTextLimit, queryText.Length))
                    elif statements.Length > statementLimit then
                        return complete (SparqlExecutionOutcome.DatasetLimitExceeded(statementLimit, statements.Length))
                    else
                        let namedGraphCount = statements |> List.choose _.Graph |> Set.ofList |> Set.count

                        let namedGraphLimit = SparqlQueryBudget.maxNamedGraphs budget

                        if namedGraphCount > namedGraphLimit then
                            return
                                complete (
                                    SparqlExecutionOutcome.NamedGraphLimitExceeded(namedGraphLimit, namedGraphCount)
                                )
                        else
                            let query = SparqlQueryParser().ParseFromString(bind request)

                            if not (queryKindMatches (SparqlQueryTemplate.kind template) query.QueryType) then
                                invalidArg
                                    (nameof request)
                                    "Parsed SPARQL query kind does not match the declared request kind."

                            let triplePatternCount, hasService = inspectPattern query.RootGraphPattern

                            if hasService then
                                invalidArg (nameof request) "Local SPARQL queries cannot contain SERVICE clauses."

                            let triplePatternLimit = SparqlQueryBudget.maxTriplePatterns budget

                            if triplePatternCount > triplePatternLimit then
                                return
                                    complete (
                                        SparqlExecutionOutcome.QueryComplexityLimitExceeded(
                                            triplePatternLimit,
                                            triplePatternCount
                                        )
                                    )
                            else
                                let resultLimit = SparqlQueryBudget.maxResultItems budget

                                if
                                    SparqlQueryTemplate.kind template = SparqlReadQueryKind.Select
                                    && (query.Limit < 0 || query.Limit > resultLimit + 1)
                                then
                                    query.Limit <- resultLimit + 1

                                query.Timeout <- SparqlQueryBudget.timeout budget |> _.TotalMilliseconds |> int64

                                query.PartialResultsOnTimeout <- false
                                use store = datasetStore dataset
                                let processor = new LeviathanQueryProcessor(new InMemoryDataset(store))

                                try
                                    let! rawResult = processor.ProcessQueryAsync query
                                    cancellationToken.ThrowIfCancellationRequested()
                                    let result, count = result (SparqlQueryTemplate.kind template) rawResult

                                    if count > resultLimit then
                                        return complete (SparqlExecutionOutcome.ResultLimitExceeded(resultLimit, count))
                                    else
                                        return complete (SparqlExecutionOutcome.Completed result)
                                with :? RdfQueryTimeoutException ->
                                    return complete SparqlExecutionOutcome.TimedOut
                } }
