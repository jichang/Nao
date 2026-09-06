namespace Nao.Agents.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type ToolProtocolTests() =
    let textTool name description execute =
        Tool.create
            name
            description
            0
            []
            ToolCodec.text
            ToolCodec.text
            (ToolOperation.create (fun _ input ->
                task {
                    let! output = execute input
                    return Ok output
                }))

    let tools =
        [ textTool "add" "Add numbers" (fun input -> Task.FromResult(sprintf "result:%s" input))
          textTool "sub" "Subtract numbers" (fun _ -> Task.FromResult "subtracted") ]

    [<TestMethod>]
    member _.ToolFailuresExposePlatformCategory() =
        let cases =
            [ ToolFailureKind.InputContract, true, PlatformErrorCategory.InvalidInput
              ToolFailureKind.PermissionDenied, false, PlatformErrorCategory.PermissionDenied
              ToolFailureKind.NotReady, false, PlatformErrorCategory.NotReady
              ToolFailureKind.ResourceExhausted, true, PlatformErrorCategory.ResourceExhausted
              ToolFailureKind.OutputContract, false, PlatformErrorCategory.InvalidOutput
              ToolFailureKind.InternalFailure, false, PlatformErrorCategory.InternalFailure
              ToolFailureKind.Cancelled, false, PlatformErrorCategory.Cancelled
              ToolFailureKind.Execution, true, PlatformErrorCategory.TransientDependency
              ToolFailureKind.Execution, false, PlatformErrorCategory.PermanentDependency ]

        for kind, retryable, expectedCategory in cases do
            let failure =
                { Kind = kind
                  Message = "failed"
                  Retryable = retryable }

            Assert.AreEqual(expectedCategory, failure.Category)

            let platformFailure = failure.ToPlatformFailure(Some "tool-call-1")
            Assert.AreEqual(expectedCategory, platformFailure.Category)
            Assert.AreEqual(retryable, platformFailure.Retryable)
            Assert.AreEqual("failed", platformFailure.Message)
            Assert.AreEqual(Some "tool-call-1", platformFailure.CorrelationId)

    [<TestMethod>]
    member _.FromToolsListsAll() =
        let protocol = ToolProtocol.fromTools tools
        let listedTools = protocol.ListTools().Result
        Assert.AreEqual(2, listedTools.Length)
        Assert.AreSame(tools.Head, listedTools.Head)

    [<TestMethod>]
    member _.GetToolFindsExisting() =
        let protocol = ToolProtocol.fromTools tools
        let found = (protocol.GetTool "add").Result
        Assert.IsTrue(found.IsSome)
        Assert.AreEqual("add", found.Value.Name)

    [<TestMethod>]
    member _.GetToolReturnsNoneForMissing() =
        let protocol = ToolProtocol.fromTools tools
        let found = (protocol.GetTool "multiply").Result
        Assert.IsTrue(found.IsNone)

    [<TestMethod>]
    member _.InvokeAsyncCallsCorrectTool() =
        let protocol = ToolProtocol.fromTools tools

        let result =
            (protocol.InvokeAsync (AgentContext.unrestrictedForTests ()) "add" "5").Result

        Assert.IsTrue(result.Success)
        Assert.AreEqual("result:5", result.Output)
        Assert.IsTrue(result.DurationMs >= 0L)

    [<TestMethod>]
    member _.InvokeAsyncReturnsErrorForMissingTool() =
        let protocol = ToolProtocol.fromTools tools

        let result =
            (protocol.InvokeAsync (AgentContext.unrestrictedForTests ()) "unknown" "x").Result

        Assert.IsFalse(result.Success)
        Assert.IsTrue(result.Error.IsSome)
        Assert.IsTrue(result.Error.Value.Contains("not found"))
        Assert.AreEqual(Some PlatformErrorCategory.InvalidInput, result.Failure |> Option.map _.Category)

    [<TestMethod>]
    member _.InvokeAsyncHandlesException() =
        let failTools = [ textTool "fail" "Fails" (fun _ -> failwith "boom") ]
        let protocol = ToolProtocol.fromTools failTools

        let result =
            (protocol.InvokeAsync (AgentContext.unrestrictedForTests ()) "fail" "x").Result

        Assert.IsFalse(result.Success)
        Assert.IsTrue(result.Error.Value.Contains("boom"))
        Assert.AreEqual(Some PlatformErrorCategory.InternalFailure, result.Failure |> Option.map _.Category)

    [<TestMethod>]
    member _.IsAvailableReturnsTrueForExisting() =
        let protocol = ToolProtocol.fromTools tools
        Assert.IsTrue((protocol.IsAvailable "add").Result)
        Assert.IsFalse((protocol.IsAvailable "missing").Result)

    [<TestMethod>]
    member _.WithMiddlewareBlocksOnBeforeError() =
        let blockMiddleware =
            { BeforeExecute =
                fun _name _input ->
                    Task.FromResult(
                        Error
                            { Kind = ToolFailureKind.PermissionDenied
                              Message = "blocked"
                              Retryable = false }
                    )
              AfterExecute = fun _name result -> Task.FromResult result }

        let protocol =
            ToolProtocol.fromTools tools |> ToolProtocol.withMiddleware blockMiddleware

        let result =
            (protocol.InvokeAsync (AgentContext.unrestrictedForTests ()) "add" "5").Result

        Assert.IsFalse(result.Success)
        Assert.AreEqual(Some "blocked", result.Error)

    [<TestMethod>]
    member _.RateLimitMiddlewareAllowsWithinLimit() =
        let middleware = ToolProtocol.rateLimitMiddleware 100
        let result = (middleware.BeforeExecute "test" "input").Result

        match result with
        | Ok v -> Assert.AreEqual("input", v)
        | Error _ -> Assert.Fail("Should be allowed")

    [<TestMethod>]
    member _.RateLimitMiddlewareReturnsResourceExhausted() =
        let middleware = ToolProtocol.rateLimitMiddleware 0

        match (middleware.BeforeExecute "test" "input").Result with
        | Ok _ -> Assert.Fail("The zero-call limit must reject the invocation.")
        | Error failure ->
            Assert.AreEqual(PlatformErrorCategory.ResourceExhausted, failure.Category)
            Assert.IsTrue(failure.Retryable)

    [<TestMethod>]
    member _.InvokeAsyncUsesProvidedPermissionContext() =
        let ran = ref false
        let permission = ResourceAccess.File("write", "/tmp/protocol.txt")

        let permissionedTool =
            Tool.create
                "writer"
                "Writes a file"
                0
                [ permission ]
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create (fun _ input ->
                    task {
                        ran.Value <- true
                        return Ok input
                    }))

        let asked = ResizeArray<ResourceAccess>()

        let context =
            { (AgentContext.unrestrictedForTests ()) with
                RequestPermission =
                    fun access _ _ ->
                        asked.Add access
                        Task.FromResult false }

        let protocol = ToolProtocol.fromTools [ permissionedTool ]

        let result = (protocol.InvokeAsync context "writer" "content").Result

        Assert.IsFalse(result.Success)
        Assert.IsFalse(ran.Value)
        Assert.AreEqual<ResourceAccess>(permission, asked[0])
        Assert.AreEqual(Some ToolFailureKind.PermissionDenied, result.Failure |> Option.map _.Kind)

    [<TestMethod>]
    member _.CompositionPassesContextAndChainsOutputs() =
        let protocol = ToolProtocol.fromTools tools
        let composition = ToolComposition.Chain [ ToolStep.Of "add"; ToolStep.Of "sub" ]

        let result =
            ToolComposer.executeAsync (AgentContext.unrestrictedForTests ()) protocol composition "5"
            |> fun task -> task.Result

        Assert.AreEqual("subtracted", result.FinalOutput)
        Assert.AreEqual(2, result.StepResults.Length)

    [<TestMethod>]
    member _.McpToolUsesQualifiedNameAndRemoteDefinition() =
        let mutable invoked = None

        let client =
            { ConnectAsync = fun () -> Task.FromResult(Error "unused")
              ListToolsAsync = fun () -> Task.FromResult([])
              ListResourcesAsync = fun () -> Task.FromResult([])
              InvokeToolAsync =
                fun name arguments ->
                    invoked <- Some(name, arguments)
                    Task.FromResult(Ok "remote-result")
              ReadResourceAsync = fun _ -> Task.FromResult(Error "unused")
              State = fun () -> McpConnectionState.Disconnected
              DisconnectAsync = fun () -> Task.FromResult(()) }

        let definition =
            { Name = "search"
              Description = Some "Search remotely"
              InputSchema = "{\"type\":\"object\"}"
              Annotations = Map.empty }

        let tool = McpTool.create "docs.search" client definition

        let result =
            tool.RunAsync (AgentContext.unrestrictedForTests ()) "{\"query\":\"Nao\"}"
            |> fun task -> task.Result

        Assert.AreEqual("docs.search", tool.Name)
        Assert.AreEqual(definition.InputSchema, tool.Schema.Input)
        Assert.AreEqual(Some("search", "{\"query\":\"Nao\"}"), invoked)
        Assert.AreEqual(Ok "remote-result", result)
