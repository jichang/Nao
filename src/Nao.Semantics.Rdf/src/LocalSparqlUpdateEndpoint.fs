namespace Nao.Semantics.Rdf

#nowarn "3511"

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open Nao.Agents
open Nao.Semantics
open VDS.RDF
open VDS.RDF.Parsing
open VDS.RDF.Query
open VDS.RDF.Query.Datasets
open VDS.RDF.Query.Patterns
open VDS.RDF.Update
open VDS.RDF.Update.Commands

[<RequireQualifiedAccess>]
module LocalSparqlUpdateEndpoint =
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
        parameterized.CommandText <- request |> SparqlUpdateRequest.template |> SparqlUpdateTemplate.text
        let factory = NodeFactory() :> INodeFactory

        request
        |> SparqlUpdateRequest.parameters
        |> Map.iter (fun name value -> parameterized.SetParameter(name, parameterNode factory value))

        parameterized.ToString()

    let rec private containsService (pattern: GraphPattern) =
        pattern.IsService
        || (pattern.ChildGraphPatterns |> Seq.exists containsService)
        || (pattern.TriplePatterns
            |> Seq.exists (fun triplePattern ->
                match triplePattern with
                | :? SubQueryPattern as subquery -> containsService subquery.SubQuery.RootGraphPattern
                | _ -> false))

    let private validateCommand (command: SparqlUpdateCommand) =
        match command with
        | :? LoadCommand -> invalidArg (nameof command) "Local SPARQL updates cannot load remote RDF."
        | :? CreateCommand
        | :? DropCommand ->
            invalidArg
                (nameof command)
                "RDF dataset contracts do not represent empty graph lifecycle; CREATE and DROP are unsupported."
        | :? ModifyCommand as modify when containsService modify.WherePattern ->
            invalidArg (nameof command) "Local SPARQL updates cannot contain SERVICE clauses."
        | :? InsertCommand as insert when containsService insert.WherePattern ->
            invalidArg (nameof command) "Local SPARQL updates cannot contain SERVICE clauses."
        | :? DeleteCommand as delete when containsService delete.WherePattern ->
            invalidArg (nameof command) "Local SPARQL updates cannot contain SERVICE clauses."
        | _ -> ()

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
        | _ -> invalidOp $"SPARQL update node type '{node.NodeType}' is not an RDF resource."

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
        | _ -> invalidOp $"SPARQL update node type '{node.NodeType}' is not supported."

    let private fromGraphName (name: IRefNode) =
        match name with
        | :? IUriNode as iri -> RdfGraphName.Iri(RdfIri.parse iri.Uri.AbsoluteUri)
        | :? IBlankNode as blank -> RdfGraphName.BlankNode(RdfBlankNodeId.parse blank.InternalID)
        | _ -> invalidOp $"SPARQL update graph-name type '{name.NodeType}' is invalid."

    let private semanticTermKey =
        function
        | RdfTerm.Literal literal when
            RdfLiteral.languageTag literal |> Option.isNone
            && RdfLiteral.datatype literal |> Option.isNone
            ->
            RdfLiteral.typed (RdfLiteral.lexicalForm literal) (RdfIri.parse "http://www.w3.org/2001/XMLSchema#string")
            |> RdfTerm.Literal
            |> LineRdf.term
        | value -> LineRdf.term value

    let private statementKey subject predicate object' graph =
        let graphValue =
            graph
            |> Option.map (fun value -> $" {LineRdf.graphName value}")
            |> Option.defaultValue ""

        $"{LineRdf.resource subject} {LineRdf.iri predicate} {semanticTermKey object'}{graphValue} ."

    let private existingStatementKey (statement: RdfStatement) =
        statementKey statement.Subject statement.Predicate statement.Object statement.Graph

    let private storeStatements (store: ITripleStore) =
        store.Graphs
        |> Seq.collect (fun graph ->
            let graphName =
                if isNull graph.Name then
                    None
                else
                    Some(fromGraphName graph.Name)

            graph.Triples
            |> Seq.map (fun triple ->
                let subject = fromResource triple.Subject

                let predicate =
                    match triple.Predicate with
                    | :? IUriNode as iri -> RdfIri.parse iri.Uri.AbsoluteUri
                    | node -> invalidOp $"SPARQL update predicate node type '{node.NodeType}' is invalid."

                let object' = fromTerm triple.Object
                let key = statementKey subject predicate object' graphName
                key, (subject, predicate, object', graphName)))
        |> Map.ofSeq

    let private newStatementId operationId key =
        let bytes = Encoding.UTF8.GetBytes $"{operationId}\n{key}"
        let digest: byte array = SHA256.HashData bytes
        $"rdf-update-{Convert.ToHexStringLower digest}"

    let private replacementDataset endpointId permissionDecisionId request dataset resultStatements =
        let existing =
            dataset
            |> RdfDataset.statements
            |> List.groupBy existingStatementKey
            |> Map.ofList

        let operationId = request |> SparqlUpdateRequest.template |> SparqlUpdateTemplate.id

        let statements =
            resultStatements
            |> Map.toList
            |> List.collect (fun (key, (subject, predicate, object', graph)) ->
                match Map.tryFind key existing with
                | Some statements -> statements
                | None ->
                    [ { Id = newStatementId operationId key
                        Subject = subject
                        Predicate = predicate
                        Object = object'
                        Graph = graph
                        Origin =
                          RdfStatementOrigin.AuthorizedUpdate
                              { OperationId = operationId
                                EndpointId = endpointId
                                PermissionDecisionId = permissionDecisionId
                                PriorDatasetRevision = RdfDataset.revision dataset } } ])

        let prefixes =
            dataset
            |> RdfDataset.prefixes
            |> Map.toList
            |> List.map (fun (name, namespaceIri) -> RdfPrefix.create name namespaceIri)

        RdfDataset.create
            (RdfDataset.owner dataset)
            (RdfDataset.id dataset)
            (RdfDataset.revision dataset + 1L)
            prefixes
            statements

    let create
        endpointId
        endpointVersion
        (timeProvider: TimeProvider)
        (authorizer: SparqlUpdateAuthorizer)
        : SparqlUpdateEndpoint =
        canonical (nameof endpointId) endpointId
        canonical (nameof endpointVersion) endpointVersion

        { ExecuteAsync =
            fun authorization dataset request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    let dataset = RdfDataset.requireOwner authorization dataset
                    let startedAt = timeProvider.GetUtcNow()
                    let stopwatch = Stopwatch.StartNew()
                    let template = SparqlUpdateRequest.template request
                    let operationId = SparqlUpdateTemplate.id template
                    let priorRevision = RdfDataset.revision dataset

                    let access =
                        ResourceAccess.knowledge $"rdf-dataset/{RdfDataset.id dataset}" KnowledgeAction.Index

                    let! permission =
                        SparqlUpdateAuthorizer.authorizeAsync authorization access cancellationToken authorizer

                    if permission.Access <> access then
                        invalidOp "SPARQL update authorizer returned evidence for a different resource access."

                    let complete resultRevision outcome : SparqlUpdateExecution =
                        stopwatch.Stop()

                        { Provenance =
                            { EndpointId = endpointId
                              EndpointVersion = endpointVersion
                              DatasetId = RdfDataset.id dataset
                              PriorDatasetRevision = priorRevision
                              ResultDatasetRevision = resultRevision
                              OperationId = operationId
                              StartedAt = startedAt
                              Duration = stopwatch.Elapsed }
                          Permission = permission
                          Outcome = outcome }

                    if permission.Decision <> PermissionDecision.Allow then
                        return complete None SparqlUpdateOutcome.Denied
                    elif SparqlUpdateRequest.expectedDatasetRevision request <> priorRevision then
                        return
                            complete
                                None
                                (SparqlUpdateOutcome.RevisionConflict(
                                    SparqlUpdateRequest.expectedDatasetRevision request,
                                    priorRevision
                                ))
                    else
                        let budget = SparqlUpdateRequest.budget request
                        let updateText = SparqlUpdateTemplate.text template
                        let textLimit = SparqlUpdateBudget.maxUpdateTextLength budget
                        let statements = RdfDataset.statements dataset
                        let datasetLimit = SparqlUpdateBudget.maxDatasetStatements budget

                        if updateText.Length > textLimit then
                            return
                                complete
                                    None
                                    (SparqlUpdateOutcome.UpdateTextLimitExceeded(textLimit, updateText.Length))
                        elif statements.Length > datasetLimit then
                            return
                                complete
                                    None
                                    (SparqlUpdateOutcome.DatasetLimitExceeded(datasetLimit, statements.Length))
                        else
                            let initialGraphCount = statements |> List.choose _.Graph |> Set.ofList |> Set.count

                            let graphLimit = SparqlUpdateBudget.maxNamedGraphs budget

                            if initialGraphCount > graphLimit then
                                return
                                    complete
                                        None
                                        (SparqlUpdateOutcome.NamedGraphLimitExceeded(graphLimit, initialGraphCount))
                            else
                                let commands = SparqlUpdateParser().ParseFromString(bind request)

                                commands.Commands |> Seq.iter validateCommand
                                let commandLimit = SparqlUpdateBudget.maxCommands budget

                                if commands.CommandCount > commandLimit then
                                    return
                                        complete
                                            None
                                            (SparqlUpdateOutcome.CommandLimitExceeded(
                                                commandLimit,
                                                commands.CommandCount
                                            ))
                                else
                                    commands.Timeout <-
                                        SparqlUpdateBudget.timeout budget |> _.TotalMilliseconds |> int64

                                    use store = datasetStore dataset
                                    let processor = new LeviathanUpdateProcessor(new InMemoryDataset(store))

                                    try
                                        processor.ProcessCommandSet commands
                                        cancellationToken.ThrowIfCancellationRequested()
                                        let resultStatements = storeStatements store
                                        let resultKeys = resultStatements |> Map.keys |> Set.ofSeq

                                        let existingByKey =
                                            statements |> List.groupBy existingStatementKey |> Map.ofList

                                        let existingKeys = existingByKey |> Map.keys |> Set.ofSeq

                                        let inserted = Set.difference resultKeys existingKeys

                                        let deleted = Set.difference existingKeys resultKeys

                                        let changedCount = inserted.Count + deleted.Count

                                        let mutationLimit = SparqlUpdateBudget.maxChangedStatements budget

                                        if changedCount > mutationLimit then
                                            return
                                                complete
                                                    None
                                                    (SparqlUpdateOutcome.MutationLimitExceeded(
                                                        mutationLimit,
                                                        changedCount
                                                    ))
                                        elif resultKeys.Count > datasetLimit then
                                            return
                                                complete
                                                    None
                                                    (SparqlUpdateOutcome.DatasetLimitExceeded(
                                                        datasetLimit,
                                                        resultKeys.Count
                                                    ))
                                        else
                                            let resultGraphCount =
                                                resultStatements
                                                |> Map.values
                                                |> Seq.choose (fun (_, _, _, graph) -> graph)
                                                |> Set.ofSeq
                                                |> Set.count

                                            if resultGraphCount > graphLimit then
                                                return
                                                    complete
                                                        None
                                                        (SparqlUpdateOutcome.NamedGraphLimitExceeded(
                                                            graphLimit,
                                                            resultGraphCount
                                                        ))
                                            else
                                                let changedGraphs =
                                                    Set.union inserted deleted
                                                    |> Seq.map (fun key ->
                                                        match Map.tryFind key resultStatements with
                                                        | Some(_, _, _, graph) -> graph
                                                        | None -> existingByKey[key] |> List.head |> _.Graph)
                                                    |> Set.ofSeq

                                                let violations =
                                                    Set.difference
                                                        changedGraphs
                                                        (SparqlUpdateRequest.writableGraphs request)

                                                if not (Set.isEmpty violations) then
                                                    return
                                                        complete
                                                            None
                                                            (SparqlUpdateOutcome.GraphScopeViolation violations)
                                                else
                                                    let replacement =
                                                        replacementDataset
                                                            endpointId
                                                            permission.Id
                                                            request
                                                            dataset
                                                            resultStatements

                                                    return
                                                        complete
                                                            (Some(RdfDataset.revision replacement))
                                                            (SparqlUpdateOutcome.Applied(
                                                                replacement,
                                                                inserted.Count,
                                                                deleted.Count
                                                            ))
                                    with
                                    | :? SparqlUpdateTimeoutException
                                    | :? RdfQueryTimeoutException -> return complete None SparqlUpdateOutcome.TimedOut
                } }
