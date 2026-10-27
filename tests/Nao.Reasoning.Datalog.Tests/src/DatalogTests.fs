namespace Nao.Reasoning.Datalog.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Reasoning
open Nao.Reasoning.Datalog

[<TestClass>]
type DatalogTests() =
    let digest character = String(character, 64)

    let authorization tenant =
        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let scope = authorization "tenant"

    let symbol name =
        { Name = name
          Type = DatalogType.Symbol }

    let variable name = DatalogTerm.Variable name

    let atom relation variables =
        { Relation = relation
          Terms = variables |> List.map variable }

    let citation id =
        ReasoningCitation.create $"fixture:{id}" "rules-v1" None

    let relations =
        [ { Name = "edge"
            Columns = [ symbol "source"; symbol "target" ]
            Kind = DatalogRelationKind.Extensional }
          { Name = "blocked"
            Columns = [ symbol "node" ]
            Kind = DatalogRelationKind.Extensional }
          { Name = "path"
            Columns = [ symbol "source"; symbol "target" ]
            Kind = DatalogRelationKind.Derived }
          { Name = "allowed_path"
            Columns = [ symbol "source"; symbol "target" ]
            Kind = DatalogRelationKind.Derived } ]

    let facts =
        [ { Id = "edge-a-b"
            Relation = "edge"
            Values = [ DatalogValue.Symbol "a"; DatalogValue.Symbol "b" ]
            Citations = [ citation "edge-a-b" ] }
          { Id = "edge-b-c"
            Relation = "edge"
            Values = [ DatalogValue.Symbol "b"; DatalogValue.Symbol "c" ]
            Citations = [ citation "edge-b-c" ] }
          { Id = "blocked-c"
            Relation = "blocked"
            Values = [ DatalogValue.Symbol "c" ]
            Citations = [ citation "blocked-c" ] } ]

    let rules =
        [ { Id = "path-base"
            Head = atom "path" [ "x"; "y" ]
            Body = [ DatalogLiteral.Positive(atom "edge" [ "x"; "y" ]) ]
            Citations = [ citation "path-base" ] }
          { Id = "path-recursive"
            Head = atom "path" [ "x"; "z" ]
            Body =
              [ DatalogLiteral.Positive(atom "path" [ "x"; "y" ])
                DatalogLiteral.Positive(atom "edge" [ "y"; "z" ]) ]
            Citations = [ citation "path-recursive" ] }
          { Id = "allowed"
            Head = atom "allowed_path" [ "x"; "y" ]
            Body =
              [ DatalogLiteral.Positive(atom "path" [ "x"; "y" ])
                DatalogLiteral.Negative(atom "blocked" [ "y" ]) ]
            Citations = [ citation "allowed" ] } ]

    let identity =
        { Id = "reachability"
          Version = "1"
          Digest = digest 'a' }

    let query =
        { Atom = atom "allowed_path" [ "source"; "target" ]
          OutputVariables = [ "source"; "target" ] }

    let program () =
        DatalogValidation.create scope identity relations facts rules query

    let limits conclusions =
        ReasoningLimits.create (TimeSpan.FromSeconds 2.0) 100_000_000L 10_000L 100 conclusions 100 100_000 0

    [<TestMethod>]
    member _.PositiveRecursionAndStratifiedNegationValidate() =
        let validated = program ()
        Assert.AreEqual(3, DatalogProgram.rules validated |> List.length)

        Assert.AreEqual(
            ReasoningMonotonicity.NonMonotonic,
            validated
            |> DatalogReasoningBridge.semanticMode
            |> ReasoningSemanticMode.monotonicity
        )

        Assert.AreEqual(
            ReasoningNegationSemantics.NegationAsFailure,
            validated
            |> DatalogReasoningBridge.semanticMode
            |> ReasoningSemanticMode.negation
        )

    [<TestMethod>]
    member _.UnsafeVariablesAndNegativeRecursionAreRejected() =
        let unsafeRule =
            { Id = "unsafe"
              Head = atom "path" [ "x"; "unbound" ]
              Body = [ DatalogLiteral.Positive(atom "edge" [ "x"; "y" ]) ]
              Citations = [ citation "unsafe" ] }

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            DatalogValidation.create scope identity relations facts [ unsafeRule ] query
            |> ignore)
        |> ignore

        let negativeRelations =
            [ { Name = "base"
                Columns = [ symbol "value" ]
                Kind = DatalogRelationKind.Extensional }
              { Name = "p"
                Columns = [ symbol "value" ]
                Kind = DatalogRelationKind.Derived }
              { Name = "q"
                Columns = [ symbol "value" ]
                Kind = DatalogRelationKind.Derived } ]

        let negativeRules =
            [ { Id = "p-rule"
                Head = atom "p" [ "x" ]
                Body =
                  [ DatalogLiteral.Positive(atom "base" [ "x" ])
                    DatalogLiteral.Negative(atom "q" [ "x" ]) ]
                Citations = [ citation "p-rule" ] }
              { Id = "q-rule"
                Head = atom "q" [ "x" ]
                Body =
                  [ DatalogLiteral.Positive(atom "base" [ "x" ])
                    DatalogLiteral.Positive(atom "p" [ "x" ]) ]
                Citations = [ citation "q-rule" ] } ]

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            DatalogValidation.create
                scope
                identity
                negativeRelations
                [ { Id = "base"
                    Relation = "base"
                    Values = [ DatalogValue.Symbol "a" ]
                    Citations = [ citation "base" ] } ]
                negativeRules
                { Atom = atom "p" [ "x" ]
                  OutputVariables = [ "x" ] }
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.SouffleCompilationDisablesEffectsAndPreservesRecursionAndNegation() =
        let mutable captured = None

        let runner =
            SouffleRunner.create (fun request _ ->
                captured <- Some request

                Task.FromResult
                    { EngineVersion = "2.5.0"
                      Rows = []
                      ResponseBytes = 0
                      PeakMemoryBytes = 1L
                      Steps = 10L
                      TimedOut = false
                      Failure = None })

        let execute = SouffleEndpoint.create "2.5.0" 100 TimeProvider.System [] runner

        execute scope (program ()) (limits 10) CancellationToken.None
        |> _.GetAwaiter().GetResult()
        |> ignore

        let request = captured.Value
        Assert.IsFalse(request.AllowFileSystem)
        Assert.IsFalse(request.AllowNetwork)
        Assert.IsFalse(request.AllowProcess)
        Assert.AreEqual(Set.empty, request.ExternalFunctors)
        StringAssert.Contains(request.Program, "path(X, Z) :- path(X, Y), edge(Y, Z).")
        StringAssert.Contains(request.Program, "!blocked(Y)")
        StringAssert.Contains(request.Program, ".output __nao_query")

    [<TestMethod>]
    member _.HostRelationsAreMaterializedBeforeIsolationAndCannotBeUndeclared() =
        let hostRelation =
            { Name = "approved"
              Columns = [ symbol "node" ]
              Kind = DatalogRelationKind.HostMaterialized }

        let hostProgram =
            DatalogValidation.create
                scope
                identity
                [ hostRelation
                  { Name = "visible"
                    Columns = [ symbol "node" ]
                    Kind = DatalogRelationKind.Derived } ]
                []
                [ { Id = "visible-rule"
                    Head = atom "visible" [ "x" ]
                    Body = [ DatalogLiteral.Positive(atom "approved" [ "x" ]) ]
                    Citations = [ citation "visible-rule" ] } ]
                { Atom = atom "visible" [ "x" ]
                  OutputVariables = [ "x" ] }

        let provider =
            { RelationName = "approved"
              ResolveAsync =
                fun _ _ _ ->
                    Task.FromResult
                        [ { Id = "approved-a"
                            Relation = "approved"
                            Values = [ DatalogValue.Symbol "a" ]
                            Citations = [ citation "approved-a" ] } ] }

        let mutable source = ""

        let runner =
            SouffleRunner.create (fun request _ ->
                source <- request.Program

                Task.FromResult
                    { EngineVersion = "2.5.0"
                      Rows = []
                      ResponseBytes = 0
                      PeakMemoryBytes = 1L
                      Steps = 1L
                      TimedOut = false
                      Failure = None })

        let execute =
            SouffleEndpoint.create "2.5.0" 10 TimeProvider.System [ provider ] runner

        execute scope hostProgram (limits 10) CancellationToken.None
        |> _.GetAwaiter().GetResult()
        |> ignore

        StringAssert.Contains(source, "approved(\"a\").")

        let missingProvider =
            SouffleEndpoint.create "2.5.0" 10 TimeProvider.System [] runner

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            missingProvider scope hostProgram (limits 10) CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.LimitsAndCommonBindingResultsAreStructuredAndCited() =
        let row =
            { Binding = Map.ofList [ "source", DatalogValue.Symbol "a"; "target", DatalogValue.Symbol "b" ]
              Derivations =
                [ { RuleId = "allowed"
                    PremiseFactIds = [ "edge-a-b" ] } ] }

        let runner =
            SouffleRunner.create (fun _ _ ->
                Task.FromResult
                    { EngineVersion = "2.5.0"
                      Rows = [ row ]
                      ResponseBytes = 100
                      PeakMemoryBytes = 1L
                      Steps = 10L
                      TimedOut = false
                      Failure = None })

        let execute = SouffleEndpoint.create "2.5.0" 100 TimeProvider.System [] runner

        let datalogExecution =
            execute scope (program ()) (limits 10) CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let requirements =
            ReasoningEngineRequirements.create
                (Some "souffle")
                Set.empty
                (Set.singleton ReasoningExecutionMode.IsolatedProcess)
                false
                false

        let reasoningRequest =
            ReasoningRequest.create
                scope
                (Guid.Parse "33333333-3333-3333-3333-333333333333")
                []
                (DatalogReasoningBridge.semanticMode (program ()))
                (ReasoningArtifact.create "application/vnd.nao.datalog-query+text" "allowed_path(source,target)")
                requirements
                (limits 10)
                (DatalogReasoningBridge.pinnedInputs (program ()))

        let result =
            DatalogReasoningBridge.adapt scope reasoningRequest (program ()) datalogExecution

        match ReasoningResult.outcome result with
        | ReasoningOutcome.Proven([ conclusion ], _) ->
            Assert.AreEqual(2, conclusion.Citations.Length)
            StringAssert.Contains(ReasoningArtifact.content conclusion.Artifact, "\"source\":\"a\"")
        | outcome -> Assert.Fail $"Expected one proven binding, got {outcome}."

        let limitedRunner =
            SouffleRunner.create (fun _ _ ->
                Task.FromResult
                    { EngineVersion = "2.5.0"
                      Rows = [ row; row ]
                      ResponseBytes = 100
                      PeakMemoryBytes = 1L
                      Steps = 10L
                      TimedOut = false
                      Failure = None })

        let limited =
            SouffleEndpoint.create "2.5.0" 100 TimeProvider.System [] limitedRunner

        match
            limited scope (program ()) (limits 1) CancellationToken.None
            |> _.GetAwaiter().GetResult()
        with
        | { Outcome = DatalogExecutionOutcome.ResourceLimitExceeded(ReasoningResourceLimit.Conclusions, 1L, 2L) } -> ()
        | execution -> Assert.Fail $"Expected conclusion limit, got {execution.Outcome}."
