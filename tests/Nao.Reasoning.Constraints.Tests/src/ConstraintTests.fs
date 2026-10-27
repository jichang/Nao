namespace Nao.Reasoning.Constraints.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Reasoning
open Nao.Reasoning.Constraints

[<TestClass>]
type ConstraintTests() =
    let authorization =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let citation id =
        ReasoningCitation.create $"fixture:{id}" "v1" None

    let identity =
        { Id = "allocation"
          Version = "1"
          Digest = String('a', 64) }

    let variable =
        { Name = "units"
          Domain = ConstraintDomain.IntegerRange(0I, 10I) }

    let assertion =
        { Id = "minimum"
          Expression =
            ConstraintExpression.GreaterThanOrEqual(
                ConstraintExpression.Variable "units",
                ConstraintExpression.Value(ConstraintValue.Integer 2I)
            )
          TrackForUnsatCore = true
          Citations = [ citation "minimum" ] }

    let objective =
        { Id = "capacity"
          Kind = ConstraintObjectiveKind.Maximize
          Expression = ConstraintExpression.Variable "units" }

    let problem () =
        ConstraintValidation.create authorization identity [ variable ] [ assertion ] [ objective ]

    let limits =
        ReasoningLimits.create (TimeSpan.FromSeconds 2.0) 100_000_000L 10_000L 100 100 100 100_000 0

    let statistics =
        { Values = Map.empty
          PeakMemoryBytes = Some 1L
          Steps = Some 10L
          ResponseBytes = Some 100 }

    let execute outcome =
        let runner =
            Z3Runner.create (fun _ _ ->
                Task.FromResult
                    { EngineVersion = "4.12.2"
                      Outcome = outcome
                      Statistics = statistics })

        Z3Endpoint.create "4.12.2" TimeProvider.System runner
        <| authorization
        <| problem ()
        <| limits
        <| CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.InvalidTypesDomainsAndIdentifiersAreRejected() =
        let invalidVariable = { variable with Name = "units);(exit" }

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            ConstraintValidation.create authorization identity [ invalidVariable ] [ assertion ] []
            |> ignore)
        |> ignore

        Assert.AreEqual("1/3", ConstraintRational.create 2I 6I |> ConstraintRational.toString)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            ConstraintValidation.create
                authorization
                identity
                [ { variable with
                      Domain = ConstraintDomain.IntegerRange(10I, 0I) } ]
                [ assertion ]
                []
            |> ignore)
        |> ignore

        let mismatched =
            { assertion with
                Expression =
                    ConstraintExpression.Equal(
                        ConstraintExpression.Variable "units",
                        ConstraintExpression.Value(ConstraintValue.Boolean true)
                    ) }

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            ConstraintValidation.create authorization identity [ variable ] [ mismatched ] []
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.SmtLibIsDeterministicTypedAndEffectFree() =
        let mutable requests = []

        let runner =
            Z3Runner.create (fun request _ ->
                requests <- request :: requests

                Task.FromResult
                    { EngineVersion = "4.12.2"
                      Outcome = ConstraintSolverOutcome.Satisfiable(Map.ofList [ "units", ConstraintValue.Integer 2I ])
                      Statistics = statistics })

        let endpoint = Z3Endpoint.create "4.12.2" TimeProvider.System runner

        for _ in 1..2 do
            endpoint authorization (problem ()) limits CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore

        Assert.AreEqual(requests[0].Script, requests[1].Script)
        StringAssert.Contains(requests[0].Script, "(declare-const units Int)")
        StringAssert.Contains(requests[0].Script, ":named minimum")
        StringAssert.Contains(requests[0].Script, "(maximize units)")
        Assert.IsTrue(requests[0].ReturnModel)
        Assert.IsTrue(requests[0].ReturnUnsatCore)
        Assert.AreEqual<string list>([ "capacity" ], requests[0].ObjectiveIds)
        Assert.AreEqual("4.12.2", requests[0].ExpectedEngineVersion)
        Assert.IsFalse(requests[0].AllowFileSystem)
        Assert.IsFalse(requests[0].AllowNetwork)
        Assert.IsFalse(requests[0].AllowProcess)

    [<TestMethod>]
    member _.RunnerModelsCoresAndIdentitiesAreTreatedAsUntrusted() =
        let invalidModel =
            execute (ConstraintSolverOutcome.Satisfiable(Map.ofList [ "units", ConstraintValue.Integer 99I ]))

        match invalidModel.Outcome with
        | ConstraintSolverOutcome.Failed("invalid_response", _) -> ()
        | outcome -> Assert.Fail $"Expected invalid response, got {outcome}."

        let invalidCore = execute (ConstraintSolverOutcome.Unsatisfiable [ "untracked" ])

        match invalidCore.Outcome with
        | ConstraintSolverOutcome.Failed("invalid_response", _) -> ()
        | outcome -> Assert.Fail $"Expected invalid response, got {outcome}."

        let runner =
            Z3Runner.create (fun _ _ ->
                Task.FromResult
                    { EngineVersion = "different"
                      Outcome = ConstraintSolverOutcome.Unknown "incomplete"
                      Statistics = statistics })

        let mismatch =
            Z3Endpoint.create
                "4.12.2"
                TimeProvider.System
                runner
                authorization
                (problem ())
                limits
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match mismatch.Outcome with
        | ConstraintSolverOutcome.Failed("invalid_response", _) -> ()
        | outcome -> Assert.Fail $"Expected version rejection, got {outcome}."

    [<TestMethod>]
    member _.TimeoutAndResourceLimitsRemainDistinct() =
        let timeoutRunner =
            Z3Runner.create (fun _ cancellationToken ->
                task {
                    do! Task.Delay(TimeSpan.FromSeconds 30.0, cancellationToken)
                    return Unchecked.defaultof<Z3RunnerResponse>
                })

        let shortLimits =
            ReasoningLimits.create (TimeSpan.FromMilliseconds 10.0) 100L 10L 10 10 10 100 0

        let timedOut =
            Z3Endpoint.create
                "4.12.2"
                TimeProvider.System
                timeoutRunner
                authorization
                (problem ())
                shortLimits
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(ConstraintSolverOutcome.TimedOut, timedOut.Outcome)

        let limited =
            execute (
                ConstraintSolverOutcome.Optimal(
                    Map.ofList [ "units", ConstraintValue.Integer 10I ],
                    [ { ObjectiveId = "capacity"
                        Value = ConstraintValue.Integer 10I } ]
                )
            )

        match limited.Outcome with
        | ConstraintSolverOutcome.Optimal _ -> ()
        | outcome -> Assert.Fail $"Expected a valid optimum, got {outcome}."

        let resourceRunner =
            Z3Runner.create (fun _ _ ->
                Task.FromResult
                    { EngineVersion = "4.12.2"
                      Outcome = ConstraintSolverOutcome.Unknown "budget"
                      Statistics =
                        { statistics with
                            PeakMemoryBytes = Some 101L } })

        let resourceLimited =
            Z3Endpoint.create
                "4.12.2"
                TimeProvider.System
                resourceRunner
                authorization
                (problem ())
                shortLimits
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match resourceLimited.Outcome with
        | ConstraintSolverOutcome.ResourceLimitExceeded(ReasoningResourceLimit.Memory, 100L, 101L) -> ()
        | outcome -> Assert.Fail $"Expected memory limit, got {outcome}."

    [<TestMethod>]
    member _.CommonReasoningPreservesModelsUnsatCoresAndUnknown() =
        let requirements =
            ReasoningEngineRequirements.create
                (Some "z3")
                Set.empty
                (Set.singleton ReasoningExecutionMode.IsolatedProcess)
                false
                true

        let commonAssertion =
            ReasoningAssertion.create
                "minimum"
                (ReasoningArtifact.create "application/vnd.nao.constraint-expression+text" "units >= 2")
                [ citation "minimum" ]

        let request =
            ReasoningRequest.create
                authorization
                (Guid.Parse "44444444-4444-4444-4444-444444444444")
                [ commonAssertion ]
                ConstraintReasoningBridge.semanticMode
                (ReasoningArtifact.create "application/vnd.nao.constraint-problem+text" "allocation")
                requirements
                limits
                (ConstraintReasoningBridge.pinnedInputs (problem ()))

        let satisfiable =
            execute (ConstraintSolverOutcome.Satisfiable(Map.ofList [ "units", ConstraintValue.Integer 2I ]))

        match
            ConstraintReasoningBridge.adapt authorization request (problem ()) satisfiable
            |> ReasoningResult.outcome
        with
        | ReasoningOutcome.Proven _ -> ()
        | outcome -> Assert.Fail $"Expected proven model, got {outcome}."

        match
            execute (ConstraintSolverOutcome.Unsatisfiable [ "minimum" ])
            |> ConstraintReasoningBridge.adapt authorization request (problem ())
            |> ReasoningResult.outcome
        with
        | ReasoningOutcome.Disproven evidence -> Assert.AreEqual(1, evidence.UnsatCore.Length)
        | outcome -> Assert.Fail $"Expected disproven core, got {outcome}."

        match
            execute (ConstraintSolverOutcome.Unknown "incomplete")
            |> ConstraintReasoningBridge.adapt authorization request (problem ())
            |> ReasoningResult.outcome
        with
        | ReasoningOutcome.Unknown(ReasoningUnknownReason.EngineReturnedUnknown "incomplete", _) -> ()
        | outcome -> Assert.Fail $"Expected distinct unknown, got {outcome}."
