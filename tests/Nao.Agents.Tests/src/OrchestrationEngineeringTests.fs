namespace Nao.Agents.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

module private TestAgent =
    let transform (id: string) (transform: string -> string) : Agent =
        Agent.createContextual
            id
            id
            id
            0
            []
            AgentContract.Text
            (fun _ input -> Task.FromResult(transform input))

[<TestClass>]
type LoopEngineeringTests() =

    [<TestMethod>]
    member _.LoopCompletesWithExplicitStateTransitions() =
        let definition =
            { MaxIterations = 5
              StepAsync =
                fun _ state ->
                    Task.FromResult(
                        if state = 2 then Complete(string state)
                        else Continue(state + 1)) }

        match (Loop.runAsync definition 0).Result with
        | Completed(output, iterations) ->
            Assert.AreEqual("2", output)
            Assert.AreEqual(3, iterations)
        | IterationLimitReached _ -> Assert.Fail("Expected the loop to complete.")

    [<TestMethod>]
    member _.LoopReturnsLastStateAtItsSafetyLimit() =
        let definition =
            { MaxIterations = 3
              StepAsync = fun _ state -> Task.FromResult(Continue(state + 1)) }

        match (Loop.runAsync definition 0).Result with
        | Completed _ -> Assert.Fail("Expected the loop to reach its limit.")
        | IterationLimitReached(state, iterations) ->
            Assert.AreEqual(3, state)
            Assert.AreEqual(3, iterations)

    [<TestMethod>]
    member _.CustomLoopCanBeUsedAsAnAgent() =
        let definition =
            { MaxIterations = 4
              Initialize = fun _ input -> input, 0
              StepAsync =
                fun _ _ (input, attempts) ->
                    if attempts = 1 then Task.FromResult(Complete(input.ToUpperInvariant()))
                    else Task.FromResult(Continue(input, attempts + 1))
              OnLimitReached = fun (input, _) -> input }

        let agent =
            LoopAgent.create
                "looping-agent"
                "Looping agent"
                "Exercises a custom loop"
                0
                []
                AgentContract.Text
                definition

        Assert.AreEqual("READY", (Agent.runAsync AgentContext.allowAll "ready" agent).Result)

[<TestClass>]
type GraphEngineeringTests() =

    let node (id: string) (transform: string -> string) : AgentGraphNode =
        { Id = GraphNodeId.create id; Agent = TestAgent.transform id transform }

    [<TestMethod>]
    member _.GraphSelectsTheFirstMatchingBranch() =
        let classify = node "classify" id
        let positive = node "positive" (fun value -> "accepted:" + value)
        let negative = node "negative" (fun value -> "rejected:" + value)

        let graph =
            { Entry = classify.Id
              Nodes = [ classify; positive; negative ]
              Edges =
                [ { From = classify.Id
                    Target = positive.Id
                    Condition = fun step -> step.Output.StartsWith("yes", StringComparison.OrdinalIgnoreCase)
                    Transform = fun step -> step.Output }
                  ExecutionGraph.edge classify.Id negative.Id ]
              MaxSteps = 2 }

        let accepted = ExecutionGraph.runAsync AgentContext.allowAll "yes please" graph |> fun task -> task.Result
        let rejected = ExecutionGraph.runAsync AgentContext.allowAll "no thanks" graph |> fun task -> task.Result

        match accepted, rejected with
        | Ok acceptedResult, Ok rejectedResult ->
            Assert.AreEqual("accepted:yes please", acceptedResult.Output)
            Assert.AreEqual("rejected:no thanks", rejectedResult.Output)
            CollectionAssert.AreEqual([| "classify"; "positive" |], acceptedResult.Steps |> List.map (fun step -> GraphNodeId.value step.NodeId) |> List.toArray)
        | _ -> Assert.Fail("Expected both graph branches to complete.")

    [<TestMethod>]
    member _.GraphBoundsCyclesWithAnExplicitStepLimit() =
        let first = node "first" id
        let second = node "second" id

        let graph =
            { Entry = first.Id
              Nodes = [ first; second ]
              Edges =
                [ ExecutionGraph.edge first.Id second.Id
                  ExecutionGraph.edge second.Id first.Id ]
              MaxSteps = 3 }

        match (ExecutionGraph.runAsync AgentContext.allowAll "input" graph).Result with
        | Error(StepLimitReached(maxSteps, steps)) ->
            Assert.AreEqual(3, maxSteps)
            Assert.AreEqual(3, steps.Length)
        | _ -> Assert.Fail("Expected the cyclic graph to reach its step limit.")

    [<TestMethod>]
    member _.LinearGraphPreservesPipelineBehavior() =
        let stages = [ TestAgent.transform "trim" (fun value -> value.Trim()); TestAgent.transform "decorate" (fun value -> "[" + value + "]") ]
        let graph = ExecutionGraph.linear stages |> Option.get
        let output =
            match (ExecutionGraph.runAsync AgentContext.allowAll " value " graph).Result with
            | Ok result -> result.Output
            | Error error -> failwithf "Unexpected graph error: %A" error

        Assert.AreEqual("[value]", output)
