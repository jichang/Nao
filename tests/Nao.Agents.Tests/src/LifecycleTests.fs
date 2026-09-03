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
            { LifecycleHook.passthrough with
                OnBeforeInit = fun _ -> Task.FromResult(Error "blocked by policy") }
        let lc = AgentLifecycle.create () |> AgentLifecycle.withHooks [ blockHook ]
        let result = (AgentLifecycle.initializeAsync agentId lc).Result
        match result with
        | Error msg -> Assert.AreEqual("blocked by policy", msg)
        | Ok _ -> Assert.Fail("Expected Error")

    [<TestMethod>]
    member _.PassthroughLifecycleHookAllowsAll() =
        let lc = AgentLifecycle.create () |> AgentLifecycle.withHooks [ LifecycleHook.passthrough ]
        let result = (AgentLifecycle.initializeAsync agentId lc).Result
        match result with
        | Ok initialized -> Assert.AreEqual(LifecycleState.Ready, initialized.State)
        | Error msg -> Assert.Fail(msg)
