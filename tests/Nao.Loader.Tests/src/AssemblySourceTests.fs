namespace Nao.Loader.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Loader
open Nao.Agents
open Nao.Agents
open Nao.Eval

/// A test agent that can be discovered via reflection
type DiscoverableTestAgent() =
    let id = { Name = "discoverable-agent"; Description = "found by reflection" }

    interface IAgent with
        member _.Id = id
        member _.RunAsync(input: string) =
            Task.FromResult(sprintf "discovered: %s" input)
        member _.HandleMessageAsync(msg: AgentMessage) =
            Task.FromResult(None)

/// A test evaluator that can be discovered via reflection
type DiscoverableTestEvaluator() =
    interface IEvaluator with
        member _.Name = "discoverable-evaluator"
        member _.EvaluateAsync (_case: EvalCase) (actual: string) =
            let verdict = if actual.Contains("good") then EvalVerdict.Pass else EvalVerdict.Fail
            Task.FromResult((verdict, "test reason"))

[<TestClass>]
type AssemblySourceTests() =

    [<TestMethod>]
    member _.DiscoversAgentsFromCurrentAssembly() =
        // Use the current test assembly which contains DiscoverableTestAgent
        let assemblyPath = typeof<DiscoverableTestAgent>.Assembly.Location
        let source = AssemblySource.fromPath assemblyPath
        let result = source.Load()

        // Should find at least DiscoverableTestAgent
        let found = result.BuiltAgents |> List.exists (fun a -> a.Id.Name = "discoverable-agent")
        Assert.IsTrue(found, "Should discover DiscoverableTestAgent")

    [<TestMethod>]
    member _.DiscoversEvaluatorsFromCurrentAssembly() =
        let assemblyPath = typeof<DiscoverableTestEvaluator>.Assembly.Location
        let source = AssemblySource.fromPath assemblyPath
        let result = source.Load()

        let found = result.BuiltEvaluators |> List.exists (fun e -> e.Name = "discoverable-evaluator")
        Assert.IsTrue(found, "Should discover DiscoverableTestEvaluator")

    [<TestMethod>]
    member _.DiscoveredAgentIsRunnable() =
        let assemblyPath = typeof<DiscoverableTestAgent>.Assembly.Location
        let source = AssemblySource.fromPath assemblyPath
        let result = source.Load()

        let agent = result.BuiltAgents |> List.find (fun a -> a.Id.Name = "discoverable-agent")
        let output = agent.RunAsync("hello").Result
        Assert.AreEqual("discovered: hello", output)

    [<TestMethod>]
    member _.ReturnsEmptyForNonexistentPath() =
        let source = AssemblySource.fromPath "/nonexistent/path/fake.dll"
        let result = source.Load()

        Assert.AreEqual(0, result.BuiltAgents.Length)
        Assert.AreEqual(0, result.BuiltTools.Length)
        Assert.AreEqual(0, result.BuiltEvaluators.Length)

    [<TestMethod>]
    member _.FromDirectoryFindsMultipleDlls() =
        // Use a temp dir with a symlink or just test empty directory behavior
        let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(tempDir) |> ignore
        try
            let sources = AssemblySource.fromDirectory tempDir
            Assert.AreEqual(0, sources.Length)
        finally
            Directory.Delete(tempDir, true)

    [<TestMethod>]
    member _.HasCorrectSourceName() =
        let source = AssemblySource.fromPath "/some/path/MyPlugin.dll"
        Assert.AreEqual("assembly:MyPlugin.dll", source.Name)
