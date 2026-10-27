namespace Nao.Semantics.Tests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Semantics

[<TestClass>]
type SparqlAgentToolsTests() =
    let authorization tenant =
        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let permission =
        ResourceAccess.knowledge "rdf-dataset/current" KnowledgeAction.Retrieve

    let budget =
        SparqlQueryBudget.create (TimeSpan.FromSeconds 2.0) 2_000 1_000 20 20 100

    let template id kind text parameterNames =
        SparqlQueryTemplate.create id kind text parameterNames

    let approved id kind text parameters =
        ApprovedSparqlQuery.create
            id
            $"Approved query {id}"
            (template id kind text (parameters |> Map.keys |> Set.ofSeq))
            parameters
            budget

    let execution
        (outcome: SparqlExecutionOutcome)
        (execute: AuthorizationScope -> SparqlReadRequest -> CancellationToken -> unit)
        =
        SparqlToolExecution.create permission (fun scope request cancellationToken ->
            execute scope request cancellationToken

            Task.FromResult
                { Provenance =
                    { EndpointId = "semantic-endpoint"
                      EndpointVersion = "1"
                      DatasetId = "dataset"
                      DatasetRevision = 7L
                      QueryId = request |> SparqlReadRequest.template |> SparqlQueryTemplate.id
                      StartedAt = DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero)
                      Duration = TimeSpan.FromMilliseconds 12.0 }
                  Permission = None
                  Outcome = outcome })

    let schemaQuery =
        approved
            "schema"
            SparqlReadQueryKind.Select
            "SELECT ?class WHERE { ?class a <http://www.w3.org/2002/07/owl#Class> }"
            Map.empty

    let context scope allowed =
        { (AgentContext.unrestrictedForTests ()) with
            Authorization = scope
            RequestPermission = fun _ _ _ -> Task.FromResult allowed }

    let tool (name: string) (tools: Tool list) : Tool =
        tools |> List.find (fun candidate -> candidate.Name = name)

    let run (candidate: Tool) context input =
        candidate.RunAsync context input |> _.GetAwaiter().GetResult()

    let assertInputFailure (result: ToolRunResult) =
        match result with
        | Error failure -> Assert.AreEqual(ToolFailureKind.InputContract, failure.Kind)
        | Ok output -> Assert.Fail $"Expected an input-contract failure, got {output}."

    [<TestMethod>]
    member _.CreationHonorsEnablementAndDeclaresExactPermissionSurface() =
        let execute =
            execution (SparqlExecutionOutcome.Completed(SparqlReadResult.Ask true)) (fun _ _ _ -> ())

        let disabled =
            SparqlAgentTools.create
                { Enabled = false
                  MaxOutputChars = 1_000 }
                execute
                schemaQuery
                []

        Assert.AreEqual(0, disabled.Length)

        let tools =
            SparqlAgentTools.create SparqlAgentToolConfig.Default execute schemaQuery []

        Assert.AreEqual(SparqlAgentTools.names, tools |> List.map _.Name |> Set.ofList)

        for candidate in tools do
            Assert.AreEqual(ToolReplaySafety.RetrySafe, candidate.ReplaySafety)
            Assert.IsTrue(List.contains permission candidate.Permissions)
            Assert.IsTrue(List.contains (ResourceAccess.tool candidate.Name) candidate.Permissions)

    [<TestMethod>]
    member _.MissingInvocationAuthorizationFailsBeforeEndpointExecution() =
        let mutable calls = 0

        let execute =
            execution (SparqlExecutionOutcome.Completed(SparqlReadResult.Ask true)) (fun _ _ _ -> calls <- calls + 1)

        let schema =
            SparqlAgentTools.create SparqlAgentToolConfig.Default execute schemaQuery []
            |> tool "semantic_schema"

        match run schema (context None true) "{}" with
        | Error failure -> Assert.AreEqual(ToolFailureKind.PermissionDenied, failure.Kind)
        | Ok output -> Assert.Fail $"Expected authorization failure, got {output}."

        Assert.AreEqual(0, calls)

    [<TestMethod>]
    member _.SchemaToolAcceptsOnlyEmptyInputAndUsesFixedApprovedQuery() =
        let scope = authorization "tenant-schema"
        let mutable requests = []

        let execute =
            execution (SparqlExecutionOutcome.Completed(SparqlReadResult.Select([], []))) (fun received request _ ->
                Assert.AreEqual(AuthorizationScope.identity scope, AuthorizationScope.identity received)
                requests <- request :: requests)

        let schema =
            SparqlAgentTools.create SparqlAgentToolConfig.Default execute schemaQuery []
            |> tool "semantic_schema"

        run schema (context (Some scope) true) """{"queryId":"other"}"""
        |> assertInputFailure

        Assert.AreEqual(0, requests.Length)

        match run schema (context (Some scope) true) "{}" with
        | Error failure -> Assert.Fail failure.Message
        | Ok _ -> ()

        Assert.AreEqual(1, requests.Length)
        Assert.AreEqual("schema", requests.Head |> SparqlReadRequest.template |> SparqlQueryTemplate.id)
        Assert.IsTrue(Object.ReferenceEquals(budget, requests.Head |> SparqlReadRequest.budget))

    [<TestMethod>]
    member _.QueryToolExecutesOnlyRegisteredQueriesWithExactTypedBindings() =
        let scope = authorization "tenant-query"
        let mutable captured = None

        let query =
            approved
                "find-by-values"
                SparqlReadQueryKind.Select
                "SELECT ?subject WHERE { ?subject <https://example.test/resource> @resource; <https://example.test/label> @label }"
                (Map.ofList
                    [ "resource", SparqlToolParameterKind.Iri
                      "label", SparqlToolParameterKind.Literal ])

        let execute =
            execution (SparqlExecutionOutcome.Completed(SparqlReadResult.Select([], []))) (fun received request _ ->
                captured <- Some(received, request))

        let queryTool =
            SparqlAgentTools.create SparqlAgentToolConfig.Default execute schemaQuery [ query ]
            |> tool "semantic_query"

        [ """{"queryId":"unknown"}"""
          """{"queryId":"find-by-values","sparql":"ASK {}","parameters":{"resource":{"kind":"iri","value":"https://example.test/r"},"label":{"kind":"literal","value":"label"}}}"""
          """{"queryId":"find-by-values","parameters":{"resource":{"kind":"literal","value":"wrong"},"label":{"kind":"literal","value":"label"}}}"""
          """{"queryId":"find-by-values","parameters":{"resource":{"kind":"iri","value":"https://example.test/r"},"label":{"kind":"literal","value":"label","language":"en","datatype":"https://example.test/type"}}}""" ]
        |> List.iter (fun input -> run queryTool (context (Some scope) true) input |> assertInputFailure)

        Assert.IsTrue(captured.IsNone)

        let input =
            """{"queryId":"find-by-values","parameters":{"resource":{"kind":"iri","value":"https://example.test/r"},"label":{"kind":"literal","value":"bonjour","language":"fr"}}}"""

        match run queryTool (context (Some scope) true) input with
        | Error failure -> Assert.Fail failure.Message
        | Ok _ -> ()

        let received, request = captured.Value
        Assert.AreEqual(AuthorizationScope.identity scope, AuthorizationScope.identity received)
        Assert.IsTrue(Object.ReferenceEquals(budget, SparqlReadRequest.budget request))

        match SparqlReadRequest.parameters request |> Map.find "resource" with
        | SparqlParameterValue.Iri iri -> Assert.AreEqual("https://example.test/r", RdfIri.value iri)
        | value -> Assert.Fail $"Expected IRI binding, got {value}."

        match SparqlReadRequest.parameters request |> Map.find "label" with
        | SparqlParameterValue.Literal literal ->
            Assert.AreEqual("bonjour", RdfLiteral.lexicalForm literal)
            Assert.AreEqual(Some "fr", RdfLiteral.languageTag literal)
        | value -> Assert.Fail $"Expected literal binding, got {value}."

    [<TestMethod>]
    member _.DeniedExecutionAndOversizedOutputFailExplicitly() =
        let scope = authorization "tenant-outcome"

        let denied =
            SparqlAgentTools.create
                SparqlAgentToolConfig.Default
                (execution SparqlExecutionOutcome.Denied (fun _ _ _ -> ()))
                schemaQuery
                []
            |> tool "semantic_schema"

        match run denied (context (Some scope) true) "{}" with
        | Error failure -> Assert.AreEqual(ToolFailureKind.PermissionDenied, failure.Kind)
        | Ok output -> Assert.Fail $"Expected permission denial, got {output}."

        let bounded =
            SparqlAgentTools.create
                { Enabled = true; MaxOutputChars = 10 }
                (execution (SparqlExecutionOutcome.Completed(SparqlReadResult.Ask true)) (fun _ _ _ -> ()))
                schemaQuery
                []
            |> tool "semantic_schema"

        match run bounded (context (Some scope) true) "{}" with
        | Error failure ->
            Assert.AreEqual(ToolFailureKind.OutputContract, failure.Kind)
            StringAssert.Contains(failure.Message, "character limit")
        | Ok output -> Assert.Fail $"Expected output limit failure, got {output}."

    [<TestMethod>]
    member _.SuccessfulOutputIncludesStructuredExecutionProvenance() =
        let scope = authorization "tenant-output"

        let schema =
            SparqlAgentTools.create
                SparqlAgentToolConfig.Default
                (execution (SparqlExecutionOutcome.Completed(SparqlReadResult.Ask true)) (fun _ _ _ -> ()))
                schemaQuery
                []
            |> tool "semantic_schema"

        let output =
            match run schema (context (Some scope) true) "{}" with
            | Ok value -> value
            | Error failure ->
                Assert.Fail failure.Message
                ""

        use document = JsonDocument.Parse output
        let root = document.RootElement
        Assert.AreEqual("completed", root.GetProperty("status").GetString())
        Assert.AreEqual("ask", root.GetProperty("kind").GetString())
        Assert.IsTrue(root.GetProperty("value").GetBoolean())
        Assert.AreEqual("semantic-endpoint", root.GetProperty("endpointId").GetString())
        Assert.AreEqual("dataset", root.GetProperty("datasetId").GetString())
        Assert.AreEqual(7L, root.GetProperty("datasetRevision").GetInt64())
        Assert.AreEqual("schema", root.GetProperty("queryId").GetString())

    [<TestMethod>]
    member _.ExplicitParameterDeclarationsIgnoreAtSignsInsideSparqlSyntax() =
        let query =
            SparqlQueryTemplate.create
                "language-literal"
                SparqlReadQueryKind.Ask
                """ASK { ?subject <https://example.test/label> "mail@example.test"@en }"""
                Set.empty

        SparqlReadRequest.create query Map.empty budget |> ignore

        let update =
            SparqlUpdateTemplate.create
                "language-update"
                """INSERT DATA { <https://example.test/subject> <https://example.test/label> "mail@example.test"@en }"""
                Set.empty

        SparqlUpdateRequest.create
            update
            Map.empty
            1L
            (Set.singleton None)
            (SparqlUpdateBudget.create (TimeSpan.FromSeconds 1.0) 1_000 100 10 2 10)
        |> ignore
