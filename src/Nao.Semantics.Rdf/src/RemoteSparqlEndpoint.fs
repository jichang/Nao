namespace Nao.Semantics.Rdf

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open Nao.Agents
open Nao.Semantics
open VDS.RDF
open VDS.RDF.Parsing
open VDS.RDF.Query
open VDS.RDF.Query.Patterns

[<RequireQualifiedAccess>]
module RemoteSparqlEndpoint =
    let private canonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"{name} must be canonical and non-blank."

    let private validateEndpoint (endpoint: Uri) =
        if
            not endpoint.IsAbsoluteUri
            || (endpoint.Scheme <> Uri.UriSchemeHttp && endpoint.Scheme <> Uri.UriSchemeHttps)
            || String.IsNullOrEmpty endpoint.Host
            || not (String.IsNullOrEmpty endpoint.UserInfo)
            || not (String.IsNullOrEmpty endpoint.Query)
            || not (String.IsNullOrEmpty endpoint.Fragment)
        then
            invalidArg
                (nameof endpoint)
                "Remote SPARQL endpoint must be an absolute HTTP(S) URI without credentials, query, or fragment."

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

    let private remoteUri (endpoint: Uri) (dataset: SparqlRemoteDataset) =
        let parameters =
            [ yield!
                  dataset
                  |> SparqlRemoteDataset.defaultGraphs
                  |> Set.toList
                  |> List.sort
                  |> List.map (fun graph -> "default-graph-uri", RdfIri.value graph)
              yield!
                  dataset
                  |> SparqlRemoteDataset.namedGraphs
                  |> Set.toList
                  |> List.sort
                  |> List.map (fun graph -> "named-graph-uri", RdfIri.value graph) ]

        let query =
            parameters
            |> List.map (fun (name, value) -> $"{name}={Uri.EscapeDataString value}")
            |> String.concat "&"

        let builder = UriBuilder(endpoint)
        builder.Query <- query
        builder.Uri

    let private fromResource (node: INode) =
        match node with
        | :? IUriNode as iri -> RdfResource.Iri(RdfIri.parse iri.Uri.AbsoluteUri)
        | :? IBlankNode as blank -> RdfResource.BlankNode(RdfBlankNodeId.parse blank.InternalID)
        | _ -> invalidOp $"Remote SPARQL result node type '{node.NodeType}' is not an RDF resource."

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
        | _ -> invalidOp $"Remote SPARQL result node type '{node.NodeType}' is unsupported."

    let private parseResult kind body =
        match kind with
        | SparqlReadQueryKind.Ask
        | SparqlReadQueryKind.Select ->
            let resultSet = new SparqlResultSet()
            SparqlJsonParser().Load(resultSet, new StringReader(body))

            match kind with
            | SparqlReadQueryKind.Ask -> SparqlReadResult.Ask resultSet.Result, 1
            | SparqlReadQueryKind.Select ->
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
            | _ -> invalidOp "Unexpected remote SPARQL result kind."
        | SparqlReadQueryKind.Construct ->
            let graph = new Graph()
            NTriplesParser().Load(graph, new StringReader(body))

            let statements =
                graph.Triples
                |> Seq.map (fun triple ->
                    let predicate =
                        match triple.Predicate with
                        | :? IUriNode as iri -> RdfIri.parse iri.Uri.AbsoluteUri
                        | node -> invalidOp $"Remote SPARQL predicate node type '{node.NodeType}' is invalid."

                    { Subject = fromResource triple.Subject
                      Predicate = predicate
                      Object = fromTerm triple.Object })
                |> Seq.toList

            SparqlReadResult.Construct statements, statements.Length

    let create
        endpointId
        endpointVersion
        (endpoint: Uri)
        maxResponseBytes
        (timeProvider: TimeProvider)
        (authorizer: SparqlRemoteAuthorizer)
        (transport: SparqlRemoteTransport)
        : SparqlRemoteReadEndpoint =
        canonical (nameof endpointId) endpointId
        canonical (nameof endpointVersion) endpointVersion
        validateEndpoint endpoint

        if maxResponseBytes <= 0 then
            invalidArg (nameof maxResponseBytes) "Remote SPARQL response limit must be positive."

        { ExecuteAsync =
            fun
                (authorization: AuthorizationScope)
                (dataset: SparqlRemoteDataset)
                (request: SparqlReadRequest)
                (cancellationToken: CancellationToken) ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    let dataset = SparqlRemoteDataset.requireOwner authorization dataset
                    let startedAt = timeProvider.GetUtcNow()
                    let stopwatch = Stopwatch.StartNew()
                    let template = SparqlReadRequest.template request
                    let budget = SparqlReadRequest.budget request
                    let requestEndpoint = remoteUri endpoint dataset
                    let access = ResourceAccess.web WebAction.Post requestEndpoint.AbsoluteUri

                    let! permission =
                        SparqlRemoteAuthorizer.authorizeAsync authorization access cancellationToken authorizer

                    if permission.Access <> access then
                        invalidOp "Remote SPARQL authorizer returned evidence for a different resource access."

                    let complete outcome =
                        stopwatch.Stop()

                        { Provenance =
                            { EndpointId = endpointId
                              EndpointVersion = endpointVersion
                              DatasetId = SparqlRemoteDataset.id dataset
                              DatasetRevision = SparqlRemoteDataset.revision dataset
                              QueryId = SparqlQueryTemplate.id template
                              StartedAt = startedAt
                              Duration = stopwatch.Elapsed }
                          Permission = Some permission
                          Outcome = outcome }

                    if permission.Decision <> PermissionDecision.Allow then
                        return complete SparqlExecutionOutcome.Denied
                    else
                        let queryText = SparqlQueryTemplate.text template
                        let textLimit = SparqlQueryBudget.maxQueryTextLength budget

                        let statementCount = SparqlRemoteDataset.statementCount dataset

                        let statementLimit = SparqlQueryBudget.maxDatasetStatements budget

                        let graphCount =
                            Set.union
                                (SparqlRemoteDataset.defaultGraphs dataset)
                                (SparqlRemoteDataset.namedGraphs dataset)
                            |> Set.count

                        let graphLimit = SparqlQueryBudget.maxNamedGraphs budget

                        if queryText.Length > textLimit then
                            return complete (SparqlExecutionOutcome.QueryTextLimitExceeded(textLimit, queryText.Length))
                        elif statementCount > statementLimit then
                            return
                                complete (SparqlExecutionOutcome.DatasetLimitExceeded(statementLimit, statementCount))
                        elif graphCount > graphLimit then
                            return complete (SparqlExecutionOutcome.NamedGraphLimitExceeded(graphLimit, graphCount))
                        else
                            let query = SparqlQueryParser().ParseFromString(bind request)

                            if not (queryKindMatches (SparqlQueryTemplate.kind template) query.QueryType) then
                                invalidArg
                                    (nameof request)
                                    "Parsed remote SPARQL query kind does not match the declared request kind."

                            if not query.UsesDefaultDataset then
                                invalidArg
                                    (nameof request)
                                    "Remote SPARQL queries cannot override the authorized protocol dataset with FROM clauses."

                            let triplePatternCount, hasService = inspectPattern query.RootGraphPattern

                            if hasService then
                                invalidArg (nameof request) "Remote SPARQL queries cannot contain SERVICE clauses."

                            let patternLimit = SparqlQueryBudget.maxTriplePatterns budget

                            if triplePatternCount > patternLimit then
                                return
                                    complete (
                                        SparqlExecutionOutcome.QueryComplexityLimitExceeded(
                                            patternLimit,
                                            triplePatternCount
                                        )
                                    )
                            else
                                let kind = SparqlQueryTemplate.kind template

                                let accept =
                                    match kind with
                                    | SparqlReadQueryKind.Select
                                    | SparqlReadQueryKind.Ask -> "application/sparql-results+json"
                                    | SparqlReadQueryKind.Construct -> "application/n-triples"

                                let timeout = SparqlQueryBudget.timeout budget

                                let transportRequest =
                                    { Endpoint = requestEndpoint
                                      ContentType = "application/sparql-query; charset=utf-8"
                                      Accept = accept
                                      Body = query.ToString()
                                      Timeout = timeout }

                                use timeoutSource =
                                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                                timeoutSource.CancelAfter timeout

                                try
                                    let! response =
                                        SparqlRemoteTransport.sendAsync transportRequest timeoutSource.Token transport

                                    if response.FinalEndpoint <> requestEndpoint then
                                        invalidOp
                                            "Remote SPARQL transport followed or reported an unauthorized endpoint."

                                    if response.StatusCode < 200 || response.StatusCode >= 300 then
                                        return complete (SparqlExecutionOutcome.RemoteFailure response.StatusCode)
                                    else
                                        if isNull response.Body then
                                            invalidOp "Remote SPARQL transport returned a null response body."

                                        let mediaType = (response.ContentType.Split(';')[0]).Trim()

                                        if not (mediaType.Equals(accept, StringComparison.OrdinalIgnoreCase)) then
                                            invalidOp
                                                $"Remote SPARQL endpoint returned unsupported content type '{response.ContentType}'."

                                        let responseBytes = Encoding.UTF8.GetByteCount response.Body

                                        if responseBytes > maxResponseBytes then
                                            return
                                                complete (
                                                    SparqlExecutionOutcome.ResponseSizeLimitExceeded(
                                                        maxResponseBytes,
                                                        responseBytes
                                                    )
                                                )
                                        else
                                            let result, count = parseResult kind response.Body

                                            let resultLimit = SparqlQueryBudget.maxResultItems budget

                                            if count > resultLimit then
                                                return
                                                    complete (
                                                        SparqlExecutionOutcome.ResultLimitExceeded(resultLimit, count)
                                                    )
                                            else
                                                return complete (SparqlExecutionOutcome.Completed result)
                                with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                                    return complete SparqlExecutionOutcome.TimedOut
                } }
