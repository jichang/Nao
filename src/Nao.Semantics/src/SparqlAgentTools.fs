namespace Nao.Semantics

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type SparqlToolParameterKind =
    | Iri
    | Literal

type ApprovedSparqlQuery =
    private
        { Id: string
          Description: string
          Template: SparqlQueryTemplate
          Parameters: Map<string, SparqlToolParameterKind>
          Budget: SparqlQueryBudget }

[<RequireQualifiedAccess>]
module ApprovedSparqlQuery =
    let create id description template parameters budget =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "Approved SPARQL query identity must be canonical and non-blank."

        if String.IsNullOrWhiteSpace description || description <> description.Trim() then
            invalidArg (nameof description) "Approved SPARQL query description must be canonical and non-blank."

        if
            parameters |> Map.keys |> Set.ofSeq
            <> SparqlQueryTemplate.parameterNames template
        then
            invalidArg (nameof parameters) "Approved SPARQL parameter schema must exactly match the query template."

        { Id = id
          Description = description
          Template = template
          Parameters = parameters
          Budget = budget }

    let id value = value.Id
    let description value = value.Description
    let template value = value.Template
    let parameters value = value.Parameters
    let budget value = value.Budget

type SparqlToolExecution =
    private
        { Permission: ResourceAccess
          ExecuteAsync: AuthorizationScope -> SparqlReadRequest -> CancellationToken -> Task<SparqlExecution> }

[<RequireQualifiedAccess>]
module SparqlToolExecution =
    let create permission executeAsync =
        match permission with
        | ResourceAccess.Knowledge(KnowledgeAction.Retrieve, _) -> ()
        | _ -> invalidArg (nameof permission) "SPARQL agent tools require a concrete knowledge-retrieve permission."

        { Permission = permission
          ExecuteAsync = executeAsync }

    let permission value = value.Permission

type SparqlAgentToolConfig =
    { Enabled: bool
      MaxOutputChars: int }

    static member Default =
        { Enabled = true
          MaxOutputChars = 32_000 }

type private SparqlToolInput =
    { QueryId: string
      Parameters: Map<string, SparqlParameterValue> }

module private SparqlAgentToolJson =
    let private propertyNames (value: JsonElement) =
        value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

    let private parseObject (value: string) (read: JsonElement -> Result<'Value, string>) =
        try
            use document = JsonDocument.Parse value

            if document.RootElement.ValueKind <> JsonValueKind.Object then
                Error "$input must be an object."
            else
                read document.RootElement
        with :? JsonException as ex ->
            Error $"$input must be valid JSON: {ex.Message}"

    let private requiredString (name: string) (value: JsonElement) =
        match value.TryGetProperty name with
        | true, property when
            property.ValueKind = JsonValueKind.String
            && not (String.IsNullOrWhiteSpace(property.GetString()))
            ->
            Ok(property.GetString().Trim())
        | _ -> Error $"$input.{name} must be a non-empty string."

    let private optionalString (path: string) (name: string) (value: JsonElement) =
        match value.TryGetProperty name with
        | false, _ -> Ok None
        | true, property when
            property.ValueKind = JsonValueKind.String
            && not (String.IsNullOrWhiteSpace(property.GetString()))
            ->
            Ok(Some(property.GetString().Trim()))
        | true, _ -> Error $"{path}.{name} must be a non-empty string."

    let private parameterValue (name: string) (expected: SparqlToolParameterKind) (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then
            Error $"$input.parameters.{name} must be an object."
        else
            let path = $"$input.parameters.{name}"

            match requiredString "kind" value, requiredString "value" value with
            | Ok "iri", Ok lexical when expected = SparqlToolParameterKind.Iri ->
                if propertyNames value <> Set.ofList [ "kind"; "value" ] then
                    Error $"{path} contains fields outside the approved IRI schema."
                else
                    try
                        RdfIri.parse lexical |> SparqlParameterValue.Iri |> Ok
                    with :? ArgumentException as ex ->
                        Error $"{path}.value is invalid: {ex.Message}"
            | Ok "literal", Ok lexical when expected = SparqlToolParameterKind.Literal ->
                let allowed = Set.ofList [ "kind"; "value"; "language"; "datatype" ]

                if not (Set.isSubset (propertyNames value) allowed) then
                    Error $"{path} contains fields outside the approved literal schema."
                else
                    match optionalString path "language" value, optionalString path "datatype" value with
                    | Ok language, Ok datatype when language.IsSome && datatype.IsSome ->
                        Error $"{path} cannot specify both language and datatype."
                    | Ok language, Ok datatype ->
                        try
                            match language, datatype with
                            | Some tag, None -> RdfLiteral.language lexical tag
                            | None, Some iri -> RdfLiteral.typed lexical (RdfIri.parse iri)
                            | None, None -> RdfLiteral.plain lexical
                            | Some _, Some _ -> invalidOp "Unreachable literal shape."
                            |> SparqlParameterValue.Literal
                            |> Ok
                        with :? ArgumentException as ex ->
                            Error $"{path} is invalid: {ex.Message}"
                    | Error error, _
                    | _, Error error -> Error error
            | Ok kind, Ok _ -> Error $"$input.parameters.{name}.kind '{kind}' does not match the approved schema."
            | Error error, _
            | _, Error error -> Error error

    let decodeSchema (value: string) =
        parseObject value (fun root ->
            if root.EnumerateObject() |> Seq.isEmpty then
                Ok()
            else
                Error "Schema discovery takes an empty object.")

    let decodeQuery (queries: Map<string, ApprovedSparqlQuery>) (value: string) =
        parseObject value (fun root ->
            match requiredString "queryId" root with
            | Error error -> Error error
            | Ok queryId ->
                let allowedRoot = Set.ofList [ "queryId"; "parameters" ]

                if not (Set.isSubset (propertyNames root) allowedRoot) then
                    Error "$input contains fields outside the approved query contract."
                else
                    match Map.tryFind queryId queries with
                    | None -> Error $"$input.queryId '{queryId}' is not approved."
                    | Some query ->
                        match root.TryGetProperty "parameters" with
                        | false, _ when Map.isEmpty (ApprovedSparqlQuery.parameters query) ->
                            Ok
                                { QueryId = queryId
                                  Parameters = Map.empty }
                        | false, _ -> Error "$input.parameters is required for this approved query."
                        | true, parameters when parameters.ValueKind = JsonValueKind.Object ->
                            let supplied =
                                parameters.EnumerateObject()
                                |> Seq.map (fun property -> property.Name)
                                |> Set.ofSeq

                            let expected = query |> ApprovedSparqlQuery.parameters |> Map.keys |> Set.ofSeq

                            if supplied <> expected then
                                Error "$input.parameters must exactly match the approved query schema."
                            else
                                query
                                |> ApprovedSparqlQuery.parameters
                                |> Map.toList
                                |> List.fold
                                    (fun state (name, kind) ->
                                        state
                                        |> Result.bind (fun values ->
                                            parameterValue name kind (parameters.GetProperty name)
                                            |> Result.map (fun value -> Map.add name value values)))
                                    (Ok Map.empty)
                                |> Result.map (fun values ->
                                    { QueryId = queryId
                                      Parameters = values })
                        | true, _ -> Error "$input.parameters must be an object.")

    let private jsonString (value: string) = JsonValue.Create(value) :> JsonNode

    let private jsonObject fields =
        fields
        |> Seq.map (fun (name, value) -> KeyValuePair<string, JsonNode>(name, value))
        |> JsonObject

    let private jsonArray values = values |> Seq.toArray |> JsonArray

    let private term =
        function
        | RdfTerm.Resource(RdfResource.Iri value) ->
            jsonObject [ "kind", jsonString "iri"; "value", jsonString (RdfIri.value value) ] :> JsonNode
        | RdfTerm.Resource(RdfResource.BlankNode value) ->
            jsonObject
                [ "kind", jsonString "blankNode"
                  "value", jsonString (RdfBlankNodeId.value value) ]
            :> JsonNode
        | RdfTerm.Literal value ->
            let result =
                jsonObject
                    [ "kind", jsonString "literal"
                      "value", jsonString (RdfLiteral.lexicalForm value) ]

            RdfLiteral.languageTag value
            |> Option.iter (fun language -> result["language"] <- jsonString language)

            RdfLiteral.datatype value
            |> Option.iter (fun datatype -> result["datatype"] <- jsonString (RdfIri.value datatype))

            result :> JsonNode

    let encodeExecution (execution: SparqlExecution) =
        let root =
            jsonObject
                [ "endpointId", jsonString execution.Provenance.EndpointId
                  "endpointVersion", jsonString execution.Provenance.EndpointVersion
                  "datasetId", jsonString execution.Provenance.DatasetId
                  "datasetRevision", JsonValue.Create(execution.Provenance.DatasetRevision) :> JsonNode
                  "queryId", jsonString execution.Provenance.QueryId
                  "durationMilliseconds", JsonValue.Create(execution.Provenance.Duration.TotalMilliseconds) :> JsonNode ]

        let status value = root["status"] <- jsonString value

        match execution.Outcome with
        | SparqlExecutionOutcome.Completed(SparqlReadResult.Ask value) ->
            status "completed"
            root["kind"] <- jsonString "ask"
            root["value"] <- JsonValue.Create(value)
        | SparqlExecutionOutcome.Completed(SparqlReadResult.Select(variables, rows)) ->
            status "completed"
            root["kind"] <- jsonString "select"
            root["variables"] <- jsonArray (variables |> List.map jsonString)

            root["rows"] <-
                jsonArray (
                    rows
                    |> List.map (fun row ->
                        let result = JsonObject()

                        row
                        |> Map.iter (fun name value ->
                            result[name] <- value |> Option.map term |> Option.defaultValue null)

                        result :> JsonNode)
                )
        | SparqlExecutionOutcome.Completed(SparqlReadResult.Construct statements) ->
            status "completed"
            root["kind"] <- jsonString "construct"

            root["statements"] <-
                jsonArray (
                    statements
                    |> List.map (fun statement ->
                        jsonObject
                            [ "subject", RdfTerm.Resource statement.Subject |> term
                              "predicate", jsonString (RdfIri.value statement.Predicate)
                              "object", term statement.Object ]
                        :> JsonNode)
                )
        | outcome ->
            status (
                match outcome with
                | SparqlExecutionOutcome.Denied -> "denied"
                | SparqlExecutionOutcome.TimedOut -> "timedOut"
                | SparqlExecutionOutcome.QueryTextLimitExceeded _ -> "queryTextLimitExceeded"
                | SparqlExecutionOutcome.QueryComplexityLimitExceeded _ -> "queryComplexityLimitExceeded"
                | SparqlExecutionOutcome.ResultLimitExceeded _ -> "resultLimitExceeded"
                | SparqlExecutionOutcome.DatasetLimitExceeded _ -> "datasetLimitExceeded"
                | SparqlExecutionOutcome.NamedGraphLimitExceeded _ -> "namedGraphLimitExceeded"
                | SparqlExecutionOutcome.ResponseSizeLimitExceeded _ -> "responseSizeLimitExceeded"
                | SparqlExecutionOutcome.RemoteFailure _ -> "remoteFailure"
                | SparqlExecutionOutcome.Completed _ -> invalidOp "Handled completed outcome."
            )

        Ok(root.ToJsonString())

[<RequireQualifiedAccess>]
module SparqlAgentTools =
    let names = Set.ofList [ "semantic_schema"; "semantic_query" ]

    let create
        (config: SparqlAgentToolConfig)
        (execution: SparqlToolExecution)
        (schemaQuery: ApprovedSparqlQuery)
        (approvedQueries: ApprovedSparqlQuery list)
        =
        if config.MaxOutputChars <= 0 then
            invalidArg (nameof config.MaxOutputChars) "Semantic tool output limit must be positive."

        let queries =
            approvedQueries
            |> List.map (fun query -> ApprovedSparqlQuery.id query, query)
            |> Map.ofList

        if queries.Count <> approvedQueries.Length then
            invalidArg (nameof approvedQueries) "Approved SPARQL query identities must be unique."

        if not (Map.isEmpty (ApprovedSparqlQuery.parameters schemaQuery)) then
            invalidArg (nameof schemaQuery) "Schema discovery query cannot require parameters."

        let output =
            ToolCodec.create
                "object - Structured bounded SPARQL execution result and provenance."
                (fun value ->
                    SparqlAgentToolJson.encodeExecution value
                    |> Result.bind (fun encoded ->
                        if encoded.Length <= config.MaxOutputChars then
                            Ok encoded
                        else
                            Error "Semantic tool output exceeded the configured character limit."))
                (fun _ -> Error "Tool outputs are not decoded by the runtime.")

        let operation query parameters =
            ToolOperation.create ToolReplaySafety.RetrySafe (fun context () ->
                task {
                    let authorization = AgentContext.requireAuthorization context

                    let request =
                        SparqlReadRequest.create
                            (ApprovedSparqlQuery.template query)
                            parameters
                            (ApprovedSparqlQuery.budget query)

                    let! result = execution.ExecuteAsync authorization request context.CancellationToken

                    match result.Outcome with
                    | SparqlExecutionOutcome.Denied ->
                        return Error(ToolExecError.PermissionDenied "SPARQL execution was denied.")
                    | _ -> return Ok result
                })

        if not config.Enabled then
            []
        else
            let schemaInput =
                ToolCodec.create
                    "object - Empty object; schema discovery is a fixed approved query."
                    (fun _ -> Error "Tool inputs are not encoded by the runtime.")
                    SparqlAgentToolJson.decodeSchema

            let schema =
                Tool.create
                    "semantic_schema"
                    "Discover the approved RDF schema surface for the current authorized dataset."
                    1000
                    [ SparqlToolExecution.permission execution ]
                    schemaInput
                    output
                    (operation schemaQuery Map.empty)

            let queryInput =
                ToolCodec.create
                    "object\n  - queryId (required): string - Registered approved query identity.\n  - parameters (required when declared): object - Exact typed IRI/literal bindings. Arbitrary SPARQL text is never accepted."
                    (fun _ -> Error "Tool inputs are not encoded by the runtime.")
                    (SparqlAgentToolJson.decodeQuery queries)

            let queryOperation =
                ToolOperation.create ToolReplaySafety.RetrySafe (fun context input ->
                    task {
                        let authorization = AgentContext.requireAuthorization context
                        let query = queries[input.QueryId]

                        let request =
                            SparqlReadRequest.create
                                (ApprovedSparqlQuery.template query)
                                input.Parameters
                                (ApprovedSparqlQuery.budget query)

                        let! result = execution.ExecuteAsync authorization request context.CancellationToken

                        match result.Outcome with
                        | SparqlExecutionOutcome.Denied ->
                            return Error(ToolExecError.PermissionDenied "SPARQL execution was denied.")
                        | _ -> return Ok result
                    })

            let query =
                Tool.create
                    "semantic_query"
                    "Execute one host-approved parameterized SPARQL query against the current authorized dataset. Never accepts raw query text."
                    1000
                    [ SparqlToolExecution.permission execution ]
                    queryInput
                    output
                    queryOperation

            [ schema; query ]
