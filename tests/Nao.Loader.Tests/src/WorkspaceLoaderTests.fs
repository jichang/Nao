namespace Nao.Loader.Tests

open System
open System.IO
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Loader

[<TestClass>]
type WorkspaceLoaderTests() =

    let mutable tempDir = ""

    [<TestInitialize>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(tempDir) |> ignore

    [<TestCleanup>]
    member _.Cleanup() =
        if Directory.Exists tempDir then
            Directory.Delete(tempDir, true)

    [<TestMethod>]
    member _.LoadWorkspace_LoadsFromNaoDirectory() =
        let naoDir = Path.Combine(tempDir, ".nao")
        Directory.CreateDirectory(Path.Combine(naoDir, "agents")) |> ignore
        Directory.CreateDirectory(Path.Combine(naoDir, "tools")) |> ignore

        let agentJson = """{"name": "ws-agent", "description": "workspace agent"}"""
        File.WriteAllText(Path.Combine(naoDir, "agents", "ws-agent.json"), agentJson)

        let toolJson = """{"name": "ws-tool", "description": "workspace tool", "execution": {"type": "process", "command": "echo", "args": []}}"""
        File.WriteAllText(Path.Combine(naoDir, "tools", "ws-tool.json"), toolJson)

        let result = WorkspaceLoader.loadWorkspace tempDir

        Assert.AreEqual(1, result.AgentDefs.Length)
        Assert.AreEqual("ws-agent", result.AgentDefs.[0].Name)
        Assert.AreEqual(1, result.ToolDefs.Length)
        Assert.AreEqual("ws-tool", result.ToolDefs.[0].Name)

    [<TestMethod>]
    member _.LoadWorkspace_ReturnsEmptyWhenNoNaoDir() =
        let result = WorkspaceLoader.loadWorkspace tempDir

        Assert.AreEqual(0, result.AgentDefs.Length)
        Assert.AreEqual(0, result.ToolDefs.Length)
        Assert.AreEqual(0, result.EvalSuiteDefs.Length)
        Assert.AreEqual(0, result.Agents.Length)
        Assert.AreEqual(0, result.Tools.Length)

    [<TestMethod>]
    member _.LoadWorkspace_HandlesPluginsDirectory() =
        // Create .nao with an agent and empty plugins dir
        let naoDir = Path.Combine(tempDir, ".nao")
        Directory.CreateDirectory(Path.Combine(naoDir, "agents")) |> ignore
        Directory.CreateDirectory(Path.Combine(tempDir, "plugins")) |> ignore

        let agentJson = """{"name": "defined-agent"}"""
        File.WriteAllText(Path.Combine(naoDir, "agents", "defined-agent.json"), agentJson)

        let result = WorkspaceLoader.loadWorkspace tempDir

        Assert.AreEqual(1, result.AgentDefs.Length)
        Assert.AreEqual("defined-agent", result.AgentDefs.[0].Name)

    [<TestMethod>]
    member _.Load_CombinesMultipleSources() =
        // Create two JSON source directories
        let dir1 = Path.Combine(tempDir, "source1", "agents")
        let dir2 = Path.Combine(tempDir, "source2", "agents")
        Directory.CreateDirectory(dir1) |> ignore
        Directory.CreateDirectory(dir2) |> ignore

        File.WriteAllText(Path.Combine(dir1, "a1.json"), """{"name": "agent-1"}""")
        File.WriteAllText(Path.Combine(dir2, "a2.json"), """{"name": "agent-2"}""")

        let sources: IDefinitionSource list = [
            JsonSource.fromDirectory (Path.Combine(tempDir, "source1"))
            JsonSource.fromDirectory (Path.Combine(tempDir, "source2"))
        ]

        let result = WorkspaceLoader.load sources

        Assert.AreEqual(2, result.AgentDefs.Length)
        let names = result.AgentDefs |> List.map (fun d -> d.Name) |> List.sort
        Assert.AreEqual(["agent-1"; "agent-2"], names)

    [<TestMethod>]
    member _.Load_CollectsErrorsAcrossSources() =
        let dir1 = Path.Combine(tempDir, "good", "agents")
        let dir2 = Path.Combine(tempDir, "bad", "agents")
        Directory.CreateDirectory(dir1) |> ignore
        Directory.CreateDirectory(dir2) |> ignore

        File.WriteAllText(Path.Combine(dir1, "ok.json"), """{"name": "ok-agent"}""")
        File.WriteAllText(Path.Combine(dir2, "broken.json"), "not json!")

        let sources: IDefinitionSource list = [
            JsonSource.fromDirectory (Path.Combine(tempDir, "good"))
            JsonSource.fromDirectory (Path.Combine(tempDir, "bad"))
        ]

        let result = WorkspaceLoader.load sources

        Assert.AreEqual(1, result.AgentDefs.Length)
        Assert.AreEqual("ok-agent", result.AgentDefs.[0].Name)
        Assert.AreEqual(1, result.Errors.Length)
