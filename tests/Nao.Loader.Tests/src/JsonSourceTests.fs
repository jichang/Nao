namespace Nao.Loader.Tests

open System
open System.IO
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Loader
open Nao.Agents
open Nao.Agents

[<TestClass>]
type JsonSourceTests() =

    let mutable tempDir = ""

    [<TestInitialize>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(tempDir) |> ignore
        Directory.CreateDirectory(Path.Combine(tempDir, "agents")) |> ignore
        Directory.CreateDirectory(Path.Combine(tempDir, "tools")) |> ignore
        Directory.CreateDirectory(Path.Combine(tempDir, "evals")) |> ignore

    [<TestCleanup>]
    member _.Cleanup() =
        if Directory.Exists tempDir then
            Directory.Delete(tempDir, true)

    [<TestMethod>]
    member _.LoadsAgentDefFromJson() =
        let json = """{
            "name": "test-agent",
            "description": "A test agent",
            "provider": "ollama",
            "model": "llama3",
            "prompt": {
                "role": "You are a helpful assistant",
                "objective": "Answer questions",
                "constraints": ["Be concise"]
            },
            "tools": ["calculator"],
            "sub_agents": [],
            "options": {
                "temperature": 0.5,
                "max_tokens": 100
            },
            "max_rounds": 3
        }"""
        File.WriteAllText(Path.Combine(tempDir, "agents", "test-agent.json"), json)

        let source = JsonSource.fromDirectory tempDir
        let result = source.Load()

        Assert.AreEqual(1, result.Agents.Length)
        match result.Agents.[0] with
        | Result.Ok def ->
            Assert.AreEqual("test-agent", def.Name)
            Assert.AreEqual("A test agent", def.Description)
            Assert.AreEqual("ollama", def.Provider)
            Assert.AreEqual("llama3", def.Model)
            Assert.AreEqual("You are a helpful assistant", def.Prompt.Role)
            Assert.AreEqual("Answer questions", def.Prompt.Objective)
            Assert.AreEqual(["Be concise"], def.Prompt.Constraints)
            Assert.AreEqual(["calculator"], def.Tools)
            Assert.AreEqual(0.5, def.Options.Temperature)
            Assert.AreEqual(Some 100, def.Options.MaxTokens)
            Assert.AreEqual(3, def.MaxRounds)
        | Result.Error e -> Assert.Fail(LoadError.format e)

    [<TestMethod>]
    member _.LoadsToolDefFromJson() =
        let json = """{
            "name": "web-search",
            "description": "Search the web",
            "execution": {
                "type": "process",
                "command": "curl",
                "args": ["-s", "https://api.example.com/search?q="]
            }
        }"""
        File.WriteAllText(Path.Combine(tempDir, "tools", "web-search.json"), json)

        let source = JsonSource.fromDirectory tempDir
        let result = source.Load()

        Assert.AreEqual(1, result.Tools.Length)
        match result.Tools.[0] with
        | Result.Ok def ->
            Assert.AreEqual("web-search", def.Name)
            Assert.AreEqual("Search the web", def.Description)
            match def.Kind with
            | ExecutableTool (ToolExecutionDef.Process (cmd, args), _, _) ->
                Assert.AreEqual("curl", cmd)
                Assert.AreEqual(["-s"; "https://api.example.com/search?q="], args)
            | _ -> Assert.Fail("Expected Process execution")
        | Result.Error e -> Assert.Fail(LoadError.format e)

    [<TestMethod>]
    member _.LoadsToolRuntimeFromJson() =
        let json = """{
            "name": "ts-tool",
            "description": "A TypeScript tool",
            "execution": {
                "type": "process",
                "command": "tool.ts",
                "args": []
            },
            "runtime": "deno"
        }"""
        File.WriteAllText(Path.Combine(tempDir, "tools", "ts-tool.json"), json)

        let source = JsonSource.fromDirectory tempDir
        let result = source.Load()

        Assert.AreEqual(1, result.Tools.Length)
        match result.Tools.[0] with
        | Result.Ok def -> Assert.AreEqual("deno", ToolDef.runtime def)
        | Result.Error e -> Assert.Fail(LoadError.format e)

    [<TestMethod>]
    member _.ToolRuntimeDefaultsToEmpty() =
        let json = """{
            "name": "plain-tool",
            "description": "No runtime declared",
            "execution": {
                "type": "process",
                "command": "echo",
                "args": []
            }
        }"""
        File.WriteAllText(Path.Combine(tempDir, "tools", "plain-tool.json"), json)

        let source = JsonSource.fromDirectory tempDir
        let result = source.Load()

        match result.Tools.[0] with
        | Result.Ok def -> Assert.AreEqual("", ToolDef.runtime def)
        | Result.Error e -> Assert.Fail(LoadError.format e)

    [<TestMethod>]
    member _.LoadsPromptToolFromJson() =
        let json = """{
            "name": "summarize",
            "description": "Summarize the input text",
            "kind": "prompt",
            "prompt": {
                "role": "You are a concise summarizer",
                "objective": "Produce a one-sentence summary"
            },
            "output_content_type": "text"
        }"""
        File.WriteAllText(Path.Combine(tempDir, "tools", "summarize.json"), json)

        let result = (JsonSource.fromDirectory tempDir).Load()
        Assert.AreEqual(1, result.Tools.Length)
        match result.Tools.[0] with
        | Result.Ok def ->
            Assert.AreEqual("summarize", def.Name)
            match def.Kind with
            | PromptTool prompt -> Assert.AreEqual("You are a concise summarizer", prompt.Role)
            | _ -> Assert.Fail("Expected a prompt tool")
            Assert.IsFalse(ToolDef.isAsync def, "Prompt tools are synchronous")
        | Result.Error e -> Assert.Fail(LoadError.format e)

    [<TestMethod>]
    member _.LoadsAsyncExecutableToolFromJson() =
        let json = """{
            "name": "long-job",
            "description": "A long running job",
            "kind": "executable",
            "execution": { "type": "process", "command": "sleep", "args": ["5"] },
            "is_async": true
        }"""
        File.WriteAllText(Path.Combine(tempDir, "tools", "long-job.json"), json)

        let result = (JsonSource.fromDirectory tempDir).Load()
        match result.Tools.[0] with
        | Result.Ok def ->
            Assert.IsTrue(ToolDef.isAsync def, "Tool should be async")
            match def.Kind with
            | ExecutableTool (ToolExecutionDef.Process (cmd, _), _, true) -> Assert.AreEqual("sleep", cmd)
            | _ -> Assert.Fail("Expected an async process executable tool")
        | Result.Error e -> Assert.Fail(LoadError.format e)

    [<TestMethod>]
    member _.PromptToolWithExecutionIsRejected() =
        let json = """{
            "name": "bad-tool",
            "description": "Invalid: prompt with execution",
            "kind": "prompt",
            "prompt": { "role": "x", "objective": "y" },
            "execution": { "type": "process", "command": "echo", "args": [] }
        }"""
        File.WriteAllText(Path.Combine(tempDir, "tools", "bad-tool.json"), json)

        let result = (JsonSource.fromDirectory tempDir).Load()
        match result.Tools.[0] with
        | Result.Ok _ -> Assert.Fail("Expected a prompt tool with execution to be rejected")
        | Result.Error _ -> ()

    [<TestMethod>]
    member _.PromptToolMarkedAsyncIsRejected() =
        let json = """{
            "name": "bad-async-prompt",
            "description": "Invalid: prompt cannot be async",
            "kind": "prompt",
            "prompt": { "role": "x", "objective": "y" },
            "is_async": true
        }"""
        File.WriteAllText(Path.Combine(tempDir, "tools", "bad-async-prompt.json"), json)

        let result = (JsonSource.fromDirectory tempDir).Load()
        match result.Tools.[0] with
        | Result.Ok _ -> Assert.Fail("Expected an async prompt tool to be rejected")
        | Result.Error _ -> ()

    [<TestMethod>]
    member _.ExecutableToolWithoutExecutionIsRejected() =
        let json = """{
            "name": "bad-exec",
            "description": "Invalid: executable without execution",
            "kind": "executable"
        }"""
        File.WriteAllText(Path.Combine(tempDir, "tools", "bad-exec.json"), json)

        let result = (JsonSource.fromDirectory tempDir).Load()
        match result.Tools.[0] with
        | Result.Ok _ -> Assert.Fail("Expected an executable tool without execution to be rejected")
        | Result.Error _ -> ()

    [<TestMethod>]
    member _.LoadsEvalSuiteFromJson() =
        let json = """{
            "name": "qa-suite",
            "description": "Question answering tests",
            "agent": "test-agent",
            "evaluator": {
                "type": "contains",
                "keywords": ["hello", "world"]
            },
            "cases": [
                {
                    "id": "case-1",
                    "description": "Simple greeting",
                    "input": "Say hello",
                    "expected": "hello world",
                    "tags": ["basic"]
                }
            ]
        }"""
        File.WriteAllText(Path.Combine(tempDir, "evals", "qa-suite.json"), json)

        let source = JsonSource.fromDirectory tempDir
        let result = source.Load()

        Assert.AreEqual(1, result.EvalSuites.Length)
        match result.EvalSuites.[0] with
        | Result.Ok def ->
            Assert.AreEqual("qa-suite", def.Name)
            Assert.AreEqual("test-agent", def.Agent)
            Assert.AreEqual("contains", def.Evaluator.Type)
            Assert.AreEqual(["hello"; "world"], def.Evaluator.Keywords)
            Assert.AreEqual(1, def.Cases.Length)
            Assert.AreEqual("case-1", def.Cases.[0].Id)
            Assert.AreEqual("Say hello", def.Cases.[0].Input)
            Assert.AreEqual(Some "hello world", def.Cases.[0].Expected)
            Assert.AreEqual(["basic"], def.Cases.[0].Tags)
        | Result.Error e -> Assert.Fail(LoadError.format e)

    [<TestMethod>]
    member _.ReturnsParseErrorForInvalidJson() =
        File.WriteAllText(Path.Combine(tempDir, "agents", "bad.json"), "not valid json {{{")

        let source = JsonSource.fromDirectory tempDir
        let result = source.Load()

        Assert.AreEqual(1, result.Agents.Length)
        match result.Agents.[0] with
        | Result.Error (ParseError _) -> () // expected
        | _ -> Assert.Fail("Expected ParseError")

    [<TestMethod>]
    member _.ReturnsEmptyWhenNoFiles() =
        let source = JsonSource.fromDirectory tempDir
        let result = source.Load()

        Assert.AreEqual(0, result.Agents.Length)
        Assert.AreEqual(0, result.Tools.Length)
        Assert.AreEqual(0, result.EvalSuites.Length)

    [<TestMethod>]
    member _.LoadsMultipleAgents() =
        for i in 1..3 do
            let json = sprintf """{"name": "agent-%d", "description": "Agent %d"}""" i i
            File.WriteAllText(Path.Combine(tempDir, "agents", sprintf "agent-%d.json" i), json)

        let source = JsonSource.fromDirectory tempDir
        let result = source.Load()

        Assert.AreEqual(3, result.Agents.Length)
        let names =
            result.Agents
            |> List.choose (fun r -> match r with Result.Ok d -> Some d.Name | _ -> None)
            |> List.sort
        Assert.AreEqual(["agent-1"; "agent-2"; "agent-3"], names)

    [<TestMethod>]
    member _.ParsesPromptExamplesAndOutputFormat() =
        let json = """{
            "name": "formatted-agent",
            "prompt": {
                "role": "formatter",
                "output_format": {
                    "type": "json",
                    "schema": "{\"type\":\"object\"}"
                },
                "examples": [
                    {"input": "hello", "output": "hi", "explanation": "greeting"}
                ]
            }
        }"""
        File.WriteAllText(Path.Combine(tempDir, "agents", "formatted.json"), json)

        let source = JsonSource.fromDirectory tempDir
        let result = source.Load()

        match result.Agents.[0] with
        | Result.Ok def ->
            Assert.AreEqual(Json (Some "{\"type\":\"object\"}"), def.Prompt.OutputFormat)
            Assert.AreEqual(1, def.Prompt.Examples.Length)
            Assert.AreEqual("hello", def.Prompt.Examples.[0].Input)
            Assert.AreEqual("hi", def.Prompt.Examples.[0].Output)
            Assert.AreEqual(Some "greeting", def.Prompt.Examples.[0].Explanation)
        | Result.Error e -> Assert.Fail(LoadError.format e)

    [<TestMethod>]
    member _.HandlesDefaultOptionsGracefully() =
        let json = """{"name": "minimal"}"""
        File.WriteAllText(Path.Combine(tempDir, "agents", "minimal.json"), json)

        let source = JsonSource.fromDirectory tempDir
        let result = source.Load()

        match result.Agents.[0] with
        | Result.Ok def ->
            Assert.AreEqual("minimal", def.Name)
            Assert.AreEqual(CompletionOptions.Default.Temperature, def.Options.Temperature)
            Assert.AreEqual(None, def.Options.MaxTokens)
            Assert.AreEqual(5, def.MaxRounds)
        | Result.Error e -> Assert.Fail(LoadError.format e)
