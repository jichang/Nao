namespace Nao.Agents.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type LifecycleTests() =

    let agentId = { Name = "test-agent"; Description = "test" }

    [<TestMethod>]
    member _.CreateStartsInCreatedState() =
        let lc = AgentLifecycle.create ()
        Assert.AreEqual(LifecycleState.Created, lc.State)
        Assert.AreEqual(0, lc.Events.Length)

    [<TestMethod>]
    member _.InitializeTransitionsToReady() =
        let lc = AgentLifecycle.create ()
        let result = (AgentLifecycle.initializeAsync agentId lc).Result
        match result with
        | Ok initialized ->
            Assert.AreEqual(LifecycleState.Ready, initialized.State)
            Assert.AreEqual(1, initialized.Events.Length)
        | Error msg -> Assert.Fail(msg)

    [<TestMethod>]
    member _.StartTransitionsToRunning() =
        let lc = AgentLifecycle.create ()
        let initialized = (AgentLifecycle.initializeAsync agentId lc).Result |> Result.defaultWith (fun _ -> failwith "init failed")
        let started = (AgentLifecycle.startAsync agentId "test input" initialized).Result
        Assert.AreEqual(LifecycleState.Running, started.State)
        Assert.AreEqual(2, started.Events.Length)

    [<TestMethod>]
    member _.SuspendTransitionsToSuspended() =
        let lc = AgentLifecycle.create ()
        let initialized = (AgentLifecycle.initializeAsync agentId lc).Result |> Result.defaultWith (fun _ -> failwith "init failed")
        let started = (AgentLifecycle.startAsync agentId "input" initialized).Result
        let suspended = AgentLifecycle.suspend agentId "pausing" started
        Assert.AreEqual(LifecycleState.Suspended, suspended.State)

    [<TestMethod>]
    member _.ResumeTransitionsToRunning() =
        let lc = AgentLifecycle.create ()
        let initialized = (AgentLifecycle.initializeAsync agentId lc).Result |> Result.defaultWith (fun _ -> failwith "init failed")
        let started = (AgentLifecycle.startAsync agentId "input" initialized).Result
        let suspended = AgentLifecycle.suspend agentId "pause" started
        let resumed = AgentLifecycle.resume agentId suspended
        Assert.AreEqual(LifecycleState.Running, resumed.State)

    [<TestMethod>]
    member _.CompleteTransitionsToCompleted() =
        let lc = AgentLifecycle.create ()
        let initialized = (AgentLifecycle.initializeAsync agentId lc).Result |> Result.defaultWith (fun _ -> failwith "init failed")
        let started = (AgentLifecycle.startAsync agentId "input" initialized).Result
        let completed = (AgentLifecycle.completeAsync agentId "done" started).Result
        Assert.AreEqual(LifecycleState.Completed, completed.State)

    [<TestMethod>]
    member _.FailTransitionsToFailed() =
        let lc = AgentLifecycle.create ()
        let initialized = (AgentLifecycle.initializeAsync agentId lc).Result |> Result.defaultWith (fun _ -> failwith "init failed")
        let started = (AgentLifecycle.startAsync agentId "input" initialized).Result
        let failed = (AgentLifecycle.failAsync agentId (exn "boom") started).Result
        match failed.State with
        | LifecycleState.Failed msg -> Assert.AreEqual("boom", msg)
        | _ -> Assert.Fail("Expected Failed state")

    [<TestMethod>]
    member _.TerminateTransitionsToTerminated() =
        let lc = AgentLifecycle.create ()
        let terminated = AgentLifecycle.terminate agentId "shutdown" lc
        Assert.AreEqual(LifecycleState.Terminated, terminated.State)

    [<TestMethod>]
    member _.HookCanBlockInitialization() =
        let blockHook =
            { new ILifecycleHook with
                member _.OnBeforeInit _ = Task.FromResult(Error "blocked by policy")
                member _.OnAfterInit _ = Task.FromResult(())
                member _.OnBeforeStep _ input = Task.FromResult(Ok input)
                member _.OnAfterStep _ _ = Task.FromResult(())
                member _.OnCompleted _ _ = Task.FromResult(())
                member _.OnFailed _ _ = Task.FromResult(()) }
        let lc = AgentLifecycle.create () |> AgentLifecycle.withHooks [ blockHook ]
        let result = (AgentLifecycle.initializeAsync agentId lc).Result
        match result with
        | Error msg -> Assert.AreEqual("blocked by policy", msg)
        | Ok _ -> Assert.Fail("Expected Error")

    [<TestMethod>]
    member _.PassthroughHookAllowsAll() =
        let lc = AgentLifecycle.create () |> AgentLifecycle.withHooks [ PassthroughHook() :> ILifecycleHook ]
        let result = (AgentLifecycle.initializeAsync agentId lc).Result
        match result with
        | Ok initialized -> Assert.AreEqual(LifecycleState.Ready, initialized.State)
        | Error msg -> Assert.Fail(msg)

[<TestClass>]
type LifecyclePipelineTests() =

    [<TestMethod>]
    member _.ExecutesAllStagesSequentially() =
        let stage1 : PipelineStage =
            { Name = "upper"
              Description = "Convert to uppercase"
              Execute = fun input -> Task.FromResult(input.ToUpperInvariant())
              Validate = fun _ -> Task.FromResult(Ok ())
              Retry = RetryPolicy.None }
        let stage2 : PipelineStage =
            { Name = "prefix"
              Description = "Add prefix"
              Execute = fun input -> Task.FromResult(sprintf "PREFIX_%s" input)
              Validate = fun _ -> Task.FromResult(Ok ())
              Retry = RetryPolicy.None }
        let result = (LifecyclePipeline.executeAsync [ stage1; stage2 ] "hello").Result
        Assert.IsTrue(result.Success)
        Assert.AreEqual(Some "PREFIX_HELLO", result.FinalOutput)
        Assert.AreEqual(2, result.Stages.Length)
        Assert.IsTrue(result.Stages |> List.forall (fun s -> s.Success))

    [<TestMethod>]
    member _.HaltsOnStageFailure() =
        let failStage : PipelineStage =
            { Name = "fail"
              Description = "Always fails"
              Execute = fun _ -> failwith "stage error"
              Validate = fun _ -> Task.FromResult(Ok ())
              Retry = RetryPolicy.None }
        let neverReached : PipelineStage =
            { Name = "never"
              Description = "Should not run"
              Execute = fun _ -> Task.FromResult "nope"
              Validate = fun _ -> Task.FromResult(Ok ())
              Retry = RetryPolicy.None }
        let result = (LifecyclePipeline.executeAsync [ failStage; neverReached ] "input").Result
        Assert.IsFalse(result.Success)
        Assert.AreEqual(Some "fail", result.FailedStage)
        Assert.AreEqual(None, result.FinalOutput)
        Assert.AreEqual(1, result.Stages.Length)

    [<TestMethod>]
    member _.RetriesOnFailureWhenRetryable() =
        let mutable attempts = 0
        let retryStage : PipelineStage =
            { Name = "flaky"
              Description = "Fails first then succeeds"
              Execute = fun input ->
                  task {
                      attempts <- attempts + 1
                      if attempts < 3 then
                          return failwith "transient"
                      else
                          return input
                  }
              Validate = fun _ -> Task.FromResult(Ok ())
              Retry = RetryPolicy.Fixed (3, 0) }
        let result = (LifecyclePipeline.executeAsync [ retryStage ] "data").Result
        Assert.IsTrue(result.Success)
        Assert.AreEqual(Some "data", result.FinalOutput)

    [<TestMethod>]
    member _.ValidationFailureTreatedAsFailure() =
        let validatedStage : PipelineStage =
            { Name = "validated"
              Description = "Produces invalid output"
              Execute = fun _ -> Task.FromResult "bad output"
              Validate = fun output ->
                  if output.Contains("bad") then Task.FromResult(Error "invalid output")
                  else Task.FromResult(Ok ())
              Retry = RetryPolicy.None }
        let result = (LifecyclePipeline.executeAsync [ validatedStage ] "input").Result
        Assert.IsFalse(result.Success)
        Assert.AreEqual(Some "validated", result.FailedStage)
