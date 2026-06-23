namespace Nao.E2E.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Core
open Nao.Agents
open Nao.Loader

// =============================================================================
// Comprehensive E2E Workspace Tests
//
// Exercises the full workspace loading pipeline with tools defined in
// multiple formats: JSON definitions, code-defined (in this project), and
// mixed together. Includes real file-system operations with verify/revert.
// =============================================================================

/// Real file-system tools that create/verify/remove directories.
/// These do actual I/O so we can test verify and revert end-to-end.
module FileSystemTools =

    /// Mutable base directory — set per test to ensure isolation
    let mutable baseTempDir =
        Path.Combine(Path.GetTempPath(), "nao-e2e-workspace")

    /// Resolves the target path from tool input (relative to temp dir)
    let private resolvePath (input: string) =
        let name = input.Trim().Replace("\\", "/").TrimStart('/')
        Path.Combine(baseTempDir, name)

    /// Tool that creates a directory. Output is the full path created.
    let createFolder: Tool =
        { Tool.Create("create_folder", "Create a new folder. Input: folder name.",
            fun input -> task {
                let path = resolvePath input
                Directory.CreateDirectory(path) |> ignore
                return sprintf """{"created":"%s","exists":true}""" (path.Replace("\\", "/"))
            }) with
            OutputContentType = ContentMeta.Json
            Verify = Some (fun input _output -> task {
                let path = resolvePath input
                if Directory.Exists(path) then return Ok ()
                else return Error (sprintf "Directory does not exist: %s" path)
            })
            Revert = Some (fun ctx -> task {
                let path = resolvePath ctx.Input
                if Directory.Exists(path) then
                    Directory.Delete(path, true)
                    return Ok ()
                else
                    return Ok () // already gone
            }) }

    /// Tool that writes a file inside a folder. Input: "folder/filename|content"
    let writeFile: Tool =
        { Tool.Create("write_file", "Write content to a file. Input: 'path|content'",
            fun input -> task {
                let parts = input.Split('|', 2)
                if parts.Length < 2 then return "Error: expected 'path|content'"
                else
                    let path = resolvePath parts.[0]
                    let dir = Path.GetDirectoryName(path)
                    if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore
                    do! File.WriteAllTextAsync(path, parts.[1])
                    return sprintf """{"written":"%s","bytes":%d}""" (path.Replace("\\", "/")) parts.[1].Length
            }) with
            OutputContentType = ContentMeta.Json
            Verify = Some (fun input _output -> task {
                let parts = input.Split('|', 2)
                if parts.Length < 2 then return Error "Invalid input"
                else
                    let path = resolvePath parts.[0]
                    if File.Exists(path) then
                        let! content = File.ReadAllTextAsync(path)
                        if content = parts.[1] then return Ok ()
                        else return Error "File content mismatch"
                    else return Error (sprintf "File does not exist: %s" path)
            })
            Revert = Some (fun ctx -> task {
                let parts = ctx.Input.Split('|', 2)
                if parts.Length >= 1 then
                    let path = resolvePath parts.[0]
                    if File.Exists(path) then File.Delete(path)
                return Ok ()
            }) }

    /// Tool that lists directory contents. Read-only, no revert needed.
    let listFolder: Tool =
        { Tool.Create("list_folder", "List contents of a folder. Input: folder name.",
            fun input -> task {
                let path = resolvePath input
                if not (Directory.Exists(path)) then
                    return sprintf """{"error":"not found","path":"%s"}""" (path.Replace("\\", "/"))
                else
                    let entries =
                        Directory.GetFileSystemEntries(path)
                        |> Array.map (fun e -> sprintf "\"%s\"" (Path.GetFileName(e)))
                        |> String.concat ","
                    return sprintf """{"path":"%s","entries":[%s]}""" (path.Replace("\\", "/")) entries
            }) with
            OutputContentType = ContentMeta.Json }

    let allTools = [ createFolder; writeFile; listFolder ]


/// A code-defined IDefinitionSource that provides pre-built tools.
/// Simulates what an assembly plugin would look like.
type CodeDefinitionSource(tools: Tool list) =
    interface IDefinitionSource with
        member _.Name = "code:E2ETests"
        member _.Load() =
            { LoadedDefinitions.Empty with
                BuiltTools = tools }


/// LLM provider that drives a file-system workflow:
/// 1. Creates a project folder
/// 2. Writes a README file inside it
/// 3. Lists the folder to confirm
type FileSystemWorkflowProvider() =
    let mutable step = 0

    interface ILlmProvider with
        member _.Name = "FileSystemWorkflow"
        member _.CompleteAsync (conversation: Conversation) (_options: CompletionOptions) =
            step <- step + 1
            let lastMsg =
                conversation
                |> List.tryFindBack (fun m -> m.Role = User)
                |> Option.map (fun m -> m.Content)
                |> Option.defaultValue ""

            let response =
                if lastMsg.Contains("[Tool Result") || lastMsg.Contains("tool_result:") then
                    // After a tool result, decide next step based on step count
                    match step with
                    | s when s <= 3 ->
                        // After create_folder, write a file
                        """{"action":"tool","name":"write_file","input":"my-project/README.md|# My Project\nThis is a test project created by Nao E2E tests."}"""
                    | s when s <= 5 ->
                        // After write_file, list the folder
                        """{"action":"tool","name":"list_folder","input":"my-project"}"""
                    | _ ->
                        "I've created the project folder, written a README.md file, and verified the contents. The workspace is ready."
                else
                    // First call — create the folder
                    """{"action":"tool","name":"create_folder","input":"my-project"}"""

            Task.FromResult({ Content = response; FinishReason = "stop"; TokensUsed = Some 100 })


/// LLM provider that uses a JSON-loaded tool (echo) alongside code tools.
type MixedSourceWorkflowProvider() =
    let mutable step = 0

    interface ILlmProvider with
        member _.Name = "MixedSourceWorkflow"
        member _.CompleteAsync (conversation: Conversation) (_options: CompletionOptions) =
            step <- step + 1
            let lastMsg =
                conversation
                |> List.tryFindBack (fun m -> m.Role = User)
                |> Option.map (fun m -> m.Content)
                |> Option.defaultValue ""

            let response =
                if lastMsg.Contains("[Tool Result") || lastMsg.Contains("tool_result:") then
                    match step with
                    | s when s <= 3 ->
                        // After echo tool, use the code tool
                        """{"action":"tool","name":"create_folder","input":"mixed-test"}"""
                    | _ ->
                        "Done! Used both the JSON-defined echo tool and the code-defined create_folder tool."
                else
                    // Start with the JSON-loaded tool
                    """{"action":"tool","name":"echo","input":"hello from json tool"}"""

            Task.FromResult({ Content = response; FinishReason = "stop"; TokensUsed = Some 80 })


// =============================================================================
// Test class: exercises workspace with mixed tool sources + real I/O
// =============================================================================

[<TestClass>]
[<DoNotParallelize>]
type WorkspaceE2ETests() =

    let mutable tempWorkspaceDir = ""
    let mutable tempToolOutputDir = ""

    [<TestInitialize>]
    member _.Setup() =
        // Create a unique base temp dir per test to avoid parallel test interference
        tempToolOutputDir <- Path.Combine(Path.GetTempPath(), sprintf "nao-e2e-ws-out-%s" (Guid.NewGuid().ToString("N").[..7]))
        Directory.CreateDirectory(tempToolOutputDir) |> ignore
        FileSystemTools.baseTempDir <- tempToolOutputDir

        // Create a temp workspace with .nao/ structure containing JSON tool definitions
        tempWorkspaceDir <- Path.Combine(Path.GetTempPath(), sprintf "nao-e2e-ws-%s" (Guid.NewGuid().ToString("N").[..7]))
        let naoDir = Path.Combine(tempWorkspaceDir, ".nao")
        let toolsDir = Path.Combine(naoDir, "tools")
        let agentsDir = Path.Combine(naoDir, "agents")
        Directory.CreateDirectory(toolsDir) |> ignore
        Directory.CreateDirectory(agentsDir) |> ignore

        // JSON tool: a simple echo tool (process-based, uses 'echo' command)
        File.WriteAllText(Path.Combine(toolsDir, "echo.json"), """{
            "name": "echo",
            "description": "Echo the input back. Useful for testing.",
            "execution": {
                "type": "process",
                "command": "echo",
                "args": ["-n"]
            },
            "output_content_type": "text/plain"
        }""")

        // JSON tool: an HTTP tool (won't actually call, but tests loading)
        File.WriteAllText(Path.Combine(toolsDir, "http-status.json"), """{
            "name": "http_status",
            "description": "Check HTTP status of a URL",
            "execution": {
                "type": "http",
                "url": "http://localhost:19999/status",
                "method": "GET",
                "headers": {}
            },
            "output_content_type": "application/json"
        }""")

        // JSON tool: with verify and revert commands
        File.WriteAllText(Path.Combine(toolsDir, "safe-mkdir.json"), """{
            "name": "safe_mkdir",
            "description": "Create a directory safely with verify and revert",
            "execution": {
                "type": "process",
                "command": "mkdir",
                "args": ["-p"]
            },
            "verify": {
                "type": "process",
                "command": "test",
                "args": ["-d"]
            },
            "revert": {
                "type": "process",
                "command": "rmdir",
                "args": []
            },
            "output_content_type": "text/plain"
        }""")

        // JSON agent: a workspace-defined assistant
        File.WriteAllText(Path.Combine(agentsDir, "assistant.json"), """{
            "name": "workspace-assistant",
            "description": "A test assistant that can use tools",
            "provider": "local",
            "model": "test-model",
            "prompt": {
                "role": "You are a helpful assistant for file management.",
                "objective": "Help the user create and organize files and folders.",
                "constraints": ["Only operate within the designated temp directory"],
                "output_format": {
                    "type": "json"
                }
            },
            "tools": ["create_folder", "write_file", "list_folder", "echo"],
            "max_rounds": 10
        }""")

    [<TestCleanup>]
    member _.Cleanup() =
        // Clean up workspace dir
        if Directory.Exists(tempWorkspaceDir) then
            try Directory.Delete(tempWorkspaceDir, true) with _ -> ()
        // Clean up tool output dir
        if Directory.Exists(tempToolOutputDir) then
            try Directory.Delete(tempToolOutputDir, true) with _ -> ()

    // ─────────────────────────────────────────────────────────────────────────
    // Loading Tests: verify workspace loads tools from all sources
    // ─────────────────────────────────────────────────────────────────────────

    [<TestMethod>]
    member _.LoadsJsonToolDefinitions() =
        let source = JsonSource(Path.Combine(tempWorkspaceDir, ".nao"))
        let loaded = (source :> IDefinitionSource).Load()

        Assert.AreEqual(3, loaded.Tools.Length, "Should load 3 JSON tool definitions")
        let names = loaded.Tools |> List.choose (fun r -> match r with Ok d -> Some d.Name | _ -> None)
        Assert.IsTrue(names |> List.contains "echo")
        Assert.IsTrue(names |> List.contains "http_status")
        Assert.IsTrue(names |> List.contains "safe_mkdir")

    [<TestMethod>]
    member _.LoadsJsonAgentDefinition() =
        let source = JsonSource(Path.Combine(tempWorkspaceDir, ".nao"))
        let loaded = (source :> IDefinitionSource).Load()

        Assert.AreEqual(1, loaded.Agents.Length)
        match loaded.Agents.[0] with
        | Ok def ->
            Assert.AreEqual("workspace-assistant", def.Name)
            Assert.AreEqual(4, def.Tools.Length)
            Assert.IsTrue(def.Tools |> List.contains "create_folder")
            Assert.IsTrue(def.Tools |> List.contains "echo")
        | Error e -> Assert.Fail(LoadError.format e)

    [<TestMethod>]
    member _.MergesJsonAndCodeSources() =
        let jsonSource = JsonSource(Path.Combine(tempWorkspaceDir, ".nao")) :> IDefinitionSource
        let codeSource = CodeDefinitionSource(FileSystemTools.allTools) :> IDefinitionSource
        let workspace = WorkspaceLoader.load [ jsonSource; codeSource ]

        // JSON-defined tools
        Assert.AreEqual(3, workspace.ToolDefs.Length)
        // Code-defined tools
        Assert.AreEqual(3, workspace.Tools.Length)
        // Should have no errors
        Assert.AreEqual(0, workspace.Errors.Length, sprintf "Errors: %A" workspace.Errors)

    [<TestMethod>]
    member _.JsonToolDefHasCorrectExecutionMode() =
        let source = JsonSource(Path.Combine(tempWorkspaceDir, ".nao"))
        let loaded = (source :> IDefinitionSource).Load()

        let tools = loaded.Tools |> List.choose (fun r -> match r with Ok d -> Some d | _ -> None)

        // echo: Process mode
        let echo = tools |> List.find (fun t -> t.Name = "echo")
        match echo.Kind with
        | ExecutableTool (ToolExecutionDef.Process (cmd, _), _, _) -> Assert.AreEqual("echo", cmd)
        | _ -> Assert.Fail("Expected Process execution for echo tool")

        // http_status: HTTP mode
        let httpTool = tools |> List.find (fun t -> t.Name = "http_status")
        match httpTool.Kind with
        | ExecutableTool (ToolExecutionDef.Http (url, method, _), _, _) ->
            Assert.AreEqual("http://localhost:19999/status", url)
            Assert.AreEqual("GET", method)
        | _ -> Assert.Fail("Expected HTTP execution for http_status tool")

    [<TestMethod>]
    member _.JsonToolDefHasVerifyAndRevert() =
        let source = JsonSource(Path.Combine(tempWorkspaceDir, ".nao"))
        let loaded = (source :> IDefinitionSource).Load()

        let tools = loaded.Tools |> List.choose (fun r -> match r with Ok d -> Some d | _ -> None)
        let safeMkdir = tools |> List.find (fun t -> t.Name = "safe_mkdir")

        Assert.IsTrue(safeMkdir.VerifyExecution.IsSome, "safe_mkdir should have verify")
        Assert.IsTrue(safeMkdir.RevertExecution.IsSome, "safe_mkdir should have revert")
        match safeMkdir.VerifyExecution.Value with
        | ToolExecutionDef.Process (cmd, _) -> Assert.AreEqual("test", cmd)
        | _ -> Assert.Fail("Expected Process verify execution")

    // ─────────────────────────────────────────────────────────────────────────
    // Code-Defined Tool Tests: real file-system operations
    // ─────────────────────────────────────────────────────────────────────────

    [<TestMethod>]
    member _.CodeTool_CreateFolder_WorksAndVerifies() =
        let tool = FileSystemTools.createFolder
        let output = tool.Execute("test-verify-folder").Result
        Assert.IsTrue(output.Contains("\"created\""), sprintf "Unexpected output: %s" output)

        // Verify should pass (directory exists)
        let verify = tool.Verify.Value
        let result = (verify "test-verify-folder" output).Result
        Assert.IsTrue(Result.isOk result, "Verify should pass after creation")

        // Revert should remove it
        let revert = tool.Revert.Value
        let revertResult = (revert { Input = "test-verify-folder"; Output = output; ExecutedAt = DateTimeOffset.UtcNow; Metadata = Map.empty }).Result
        Assert.IsTrue(Result.isOk revertResult, "Revert should succeed")

        // Verify should now fail (directory removed)
        let result2 = (verify "test-verify-folder" output).Result
        Assert.IsTrue(Result.isError result2, "Verify should fail after revert")

    [<TestMethod>]
    member _.CodeTool_WriteFile_WorksAndVerifies() =
        // Create parent dir first
        FileSystemTools.createFolder.Execute("write-test-dir").Result |> ignore

        let tool = FileSystemTools.writeFile
        let output = tool.Execute("write-test-dir/hello.txt|Hello World").Result
        Assert.IsTrue(output.Contains("\"written\""))

        // Verify
        let verify = tool.Verify.Value
        let result = (verify "write-test-dir/hello.txt|Hello World" output).Result
        Assert.IsTrue(Result.isOk result)

        // Revert
        let revert = tool.Revert.Value
        let revertResult = (revert { Input = "write-test-dir/hello.txt|Hello World"; Output = output; ExecutedAt = DateTimeOffset.UtcNow; Metadata = Map.empty }).Result
        Assert.IsTrue(Result.isOk revertResult)

        // File should be gone
        let result2 = (verify "write-test-dir/hello.txt|Hello World" output).Result
        Assert.IsTrue(Result.isError result2, "Verify should fail after revert")

    [<TestMethod>]
    member _.CodeTool_ListFolder_ReturnsEntries() =
        // Setup: create folder with some files
        FileSystemTools.createFolder.Execute("list-test-dir").Result |> ignore
        FileSystemTools.writeFile.Execute("list-test-dir/a.txt|aaa").Result |> ignore
        FileSystemTools.writeFile.Execute("list-test-dir/b.txt|bbb").Result |> ignore

        let output = FileSystemTools.listFolder.Execute("list-test-dir").Result
        Assert.IsTrue(output.Contains("a.txt"), sprintf "Should list a.txt: %s" output)
        Assert.IsTrue(output.Contains("b.txt"), sprintf "Should list b.txt: %s" output)

    // ─────────────────────────────────────────────────────────────────────────
    // Execution Journal: track tool executions and bulk revert
    // ─────────────────────────────────────────────────────────────────────────

    [<TestMethod>]
    member _.ExecutionJournal_RecordsAndReverts() =
        let journal = InMemoryExecutionJournal() :> IExecutionJournal

        // Execute and record
        let tool = FileSystemTools.createFolder
        let output1 = tool.Execute("journal-dir-1").Result
        journal.RecordAsync(
            { ToolName = tool.Name; Input = "journal-dir-1"; Output = output1
              ContentMeta = tool.OutputContentType; ExecutedAt = DateTimeOffset.UtcNow
              Reverted = false; Metadata = Map.empty }).Wait()

        let output2 = tool.Execute("journal-dir-2").Result
        journal.RecordAsync(
            { ToolName = tool.Name; Input = "journal-dir-2"; Output = output2
              ContentMeta = tool.OutputContentType; ExecutedAt = DateTimeOffset.UtcNow
              Reverted = false; Metadata = Map.empty }).Wait()

        // Both dirs should exist
        let verifyResult1 = (tool.Verify.Value "journal-dir-1" output1).Result
        let verifyResult2 = (tool.Verify.Value "journal-dir-2" output2).Result
        Assert.IsTrue(Result.isOk verifyResult1)
        Assert.IsTrue(Result.isOk verifyResult2)

        // Bulk revert
        let tools = [ tool ]
        let results = (ExecutionJournal.revertAllAsync journal tools).Result
        let failures = results |> List.filter (fun (_, r) -> Result.isError r)
        Assert.AreEqual(0, failures.Length, sprintf "Revert failures: %A" failures)

        // Both dirs should be gone
        let verifyAfter1 = (tool.Verify.Value "journal-dir-1" output1).Result
        let verifyAfter2 = (tool.Verify.Value "journal-dir-2" output2).Result
        Assert.IsTrue(Result.isError verifyAfter1, "Dir 1 should be gone")
        Assert.IsTrue(Result.isError verifyAfter2, "Dir 2 should be gone")

    [<TestMethod>]
    member _.ExecutionJournal_RevertLast_OnlyRevertsLatest() =
        let journal = InMemoryExecutionJournal() :> IExecutionJournal
        let tool = FileSystemTools.createFolder

        tool.Execute("revert-first").Result |> ignore
        journal.RecordAsync(
            { ToolName = tool.Name; Input = "revert-first"; Output = ""; ContentMeta = tool.OutputContentType
              ExecutedAt = DateTimeOffset.UtcNow; Reverted = false; Metadata = Map.empty }).Wait()

        tool.Execute("revert-second").Result |> ignore
        journal.RecordAsync(
            { ToolName = tool.Name; Input = "revert-second"; Output = ""; ContentMeta = tool.OutputContentType
              ExecutedAt = DateTimeOffset.UtcNow.AddSeconds(1.0); Reverted = false; Metadata = Map.empty }).Wait()

        // Revert only the last
        let result = (ExecutionJournal.revertLastAsync journal [ tool ]).Result
        Assert.IsTrue(Result.isOk result, "Should revert successfully")

        // Second should be gone, first should remain
        let v1 = (tool.Verify.Value "revert-first" "").Result
        let v2 = (tool.Verify.Value "revert-second" "").Result
        Assert.IsTrue(Result.isOk v1, "First dir should still exist")
        Assert.IsTrue(Result.isError v2, "Second dir should be reverted")

    // ─────────────────────────────────────────────────────────────────────────
    // Full Orchestration: LLM drives tools through the ETCLOVG harness
    // ─────────────────────────────────────────────────────────────────────────

    [<TestMethod>]
    member _.FullWorkflow_LlmDrivesFileSystemTools() =
        let provider = FileSystemWorkflowProvider() :> ILlmProvider
        let tools = FileSystemTools.allTools

        let config : OrchestratorConfig =
            { Provider = provider
              Tools = tools
              SubAgents = []
              Prompt = Prompt.Empty
              Options = CompletionOptions.Default
              MaxRounds = 10
              EventSink = AgentEventSink.none
              Memory = OrchestratorMemoryConfig.None
              Instructions = None }

        let agent = Orchestrator.createWithConfig config
        let response = agent.RunAsync("Create a project folder with a README file").Result

        // Should have completed successfully
        Assert.IsTrue(response.Contains("created") || response.Contains("ready") || response.Contains("README"),
            sprintf "Unexpected response: %s" response)

        // The folder and file should actually exist
        let projectDir = Path.Combine(tempToolOutputDir, "my-project")
        Assert.IsTrue(Directory.Exists(projectDir), "Project directory should exist")
        let readmePath = Path.Combine(projectDir, "README.md")
        Assert.IsTrue(File.Exists(readmePath), "README.md should exist")
        let content = File.ReadAllText(readmePath)
        Assert.IsTrue(content.Contains("My Project"), "README should have content")

    [<TestMethod>]
    member _.FullWorkflow_LlmWithRevert() =
        let tools = FileSystemTools.allTools
        let journal = InMemoryExecutionJournal() :> IExecutionJournal

        // Wrap the create_folder tool to auto-record to journal
        let recordingCreateFolder =
            { FileSystemTools.createFolder with
                Execute = fun input ->
                    task {
                        let! output = FileSystemTools.createFolder.Execute(input)
                        do! journal.RecordAsync(
                            { ToolName = "create_folder"; Input = input; Output = output
                              ContentMeta = FileSystemTools.createFolder.OutputContentType
                              ExecutedAt = DateTimeOffset.UtcNow; Reverted = false; Metadata = Map.empty })
                        return output
                    } }

        let recordingWriteFile =
            { FileSystemTools.writeFile with
                Execute = fun input ->
                    task {
                        let! output = FileSystemTools.writeFile.Execute(input)
                        do! journal.RecordAsync(
                            { ToolName = "write_file"; Input = input; Output = output
                              ContentMeta = FileSystemTools.writeFile.OutputContentType
                              ExecutedAt = DateTimeOffset.UtcNow; Reverted = false; Metadata = Map.empty })
                        return output
                    } }

        let recordingTools = [ recordingCreateFolder; recordingWriteFile; FileSystemTools.listFolder ]

        let provider = FileSystemWorkflowProvider() :> ILlmProvider
        let orchestratorConfig : OrchestratorConfig =
            { Provider = provider
              Tools = recordingTools
              SubAgents = []
              Prompt = Prompt.Empty
              Options = CompletionOptions.Default
              MaxRounds = 10
              EventSink = AgentEventSink.none
              Memory = OrchestratorMemoryConfig.None
              Instructions = None }

        let agent = Orchestrator.createWithConfig orchestratorConfig
        agent.RunAsync("Create a project folder with README").Result |> ignore

        // Journal should have recorded some tool executions
        let history = journal.GetHistoryAsync().Result
        Assert.IsTrue(history.Length > 0, "Journal should have records")

        // The project folder should exist before revert
        let projectDir = Path.Combine(tempToolOutputDir, "my-project")
        Assert.IsTrue(Directory.Exists(projectDir), "Project dir should exist before revert")

        // Revert all recorded operations
        let results = (ExecutionJournal.revertAllAsync journal tools).Result
        let failures = results |> List.filter (fun (_, r) -> Result.isError r)
        Assert.AreEqual(0, failures.Length, sprintf "Revert failures: %A" failures)

        // The project folder should no longer exist (create_folder reverted)
        Assert.IsFalse(Directory.Exists(projectDir), "Project dir should be reverted")

    // ─────────────────────────────────────────────────────────────────────────
    // Mixed Source: JSON tools + code tools used together
    // ─────────────────────────────────────────────────────────────────────────

    [<TestMethod>]
    member _.MixedSources_JsonAndCodeToolsWorkTogether() =
        // Load workspace with both sources
        let jsonSource = JsonSource(Path.Combine(tempWorkspaceDir, ".nao")) :> IDefinitionSource
        let codeSource = CodeDefinitionSource(FileSystemTools.allTools) :> IDefinitionSource
        let workspace = WorkspaceLoader.load [ jsonSource; codeSource ]

        // Build the JSON echo tool
        let echoDef = workspace.ToolDefs |> List.find (fun t -> t.Name = "echo")
        let echoTool = DefinitionBuilder.buildTool echoDef

        // Combine: built tools from JSON + pre-built tools from code
        let allTools = echoTool :: workspace.Tools

        let provider = MixedSourceWorkflowProvider() :> ILlmProvider
        let config : OrchestratorConfig =
            { Provider = provider
              Tools = allTools
              SubAgents = []
              Prompt = Prompt.Empty
              Options = CompletionOptions.Default
              MaxRounds = 8
              EventSink = AgentEventSink.none
              Memory = OrchestratorMemoryConfig.None
              Instructions = None }

        let agent = Orchestrator.createWithConfig config
        let response = agent.RunAsync("Use the echo tool and then create a folder").Result

        // Should have used both tools
        Assert.IsTrue(response.Contains("Done") || response.Contains("echo") || response.Contains("create_folder"),
            sprintf "Unexpected: %s" response)

        // The code tool should have created the folder
        let folder = Path.Combine(tempToolOutputDir, "mixed-test")
        Assert.IsTrue(Directory.Exists(folder), "mixed-test folder should exist from code tool")

    [<TestMethod>]
    member _.MixedSources_BuildAgentFromJsonDef() =
        // Load workspace
        let jsonSource = JsonSource(Path.Combine(tempWorkspaceDir, ".nao")) :> IDefinitionSource
        let codeSource = CodeDefinitionSource(FileSystemTools.allTools) :> IDefinitionSource
        let workspace = WorkspaceLoader.load [ jsonSource; codeSource ]

        // The agent def references tools by name — resolve them
        let agentDef = workspace.AgentDefs |> List.find (fun a -> a.Name = "workspace-assistant")
        Assert.AreEqual("workspace-assistant", agentDef.Name)
        Assert.AreEqual(4, agentDef.Tools.Length)

        // Build the echo tool from JSON
        let echoTool = workspace.ToolDefs |> List.find (fun t -> t.Name = "echo") |> DefinitionBuilder.buildTool

        // Resolve tool names to actual tools
        let allAvailableTools = echoTool :: workspace.Tools
        let resolvedTools =
            agentDef.Tools
            |> List.choose (fun name -> allAvailableTools |> List.tryFind (fun t -> t.Name = name))

        // Should resolve create_folder, write_file, list_folder from code + echo from JSON
        Assert.AreEqual(4, resolvedTools.Length,
            sprintf "Resolved %d tools: %A" resolvedTools.Length (resolvedTools |> List.map (fun t -> t.Name)))

    // ─────────────────────────────────────────────────────────────────────────
    // ContentMeta: verify tools carry content type through
    // ─────────────────────────────────────────────────────────────────────────

    [<TestMethod>]
    member _.ContentMeta_CodeToolsHaveJsonContentType() =
        Assert.AreEqual("application/json", FileSystemTools.createFolder.OutputContentType.ContentType)
        Assert.AreEqual("application/json", FileSystemTools.writeFile.OutputContentType.ContentType)
        Assert.AreEqual("application/json", FileSystemTools.listFolder.OutputContentType.ContentType)

    [<TestMethod>]
    member _.ContentMeta_JsonToolDefContentTypeRoundTrips() =
        let source = JsonSource(Path.Combine(tempWorkspaceDir, ".nao"))
        let loaded = (source :> IDefinitionSource).Load()
        let tools = loaded.Tools |> List.choose (fun r -> match r with Ok d -> Some d | _ -> None)

        let echo = tools |> List.find (fun t -> t.Name = "echo")
        Assert.AreEqual("text/plain", echo.OutputContentType)

        let httpTool = tools |> List.find (fun t -> t.Name = "http_status")
        Assert.AreEqual("application/json", httpTool.OutputContentType)

    [<TestMethod>]
    member _.ContentMeta_BuiltToolPreservesContentType() =
        let source = JsonSource(Path.Combine(tempWorkspaceDir, ".nao"))
        let loaded = (source :> IDefinitionSource).Load()
        let tools = loaded.Tools |> List.choose (fun r -> match r with Ok d -> Some d | _ -> None)

        let echoDef = tools |> List.find (fun t -> t.Name = "echo")
        let builtTool = DefinitionBuilder.buildTool echoDef

        Assert.AreEqual("text/plain", builtTool.OutputContentType.ContentType)
