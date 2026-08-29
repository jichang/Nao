namespace Nao.Agents.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type ToolProtocolTests() =

    let tools =
        [ Tool.Create("add", "Add numbers", ToolSignature.Text, (fun input -> Task.FromResult (sprintf "result:%s" input)))
          Tool.Create("sub", "Subtract numbers", ToolSignature.Text, (fun _ -> Task.FromResult "subtracted")) ]

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
    member _.GetToolRejectsUnqualifiedReference() =
        let protocol = ToolProtocol.fromTools tools
        Assert.ThrowsExactly<ArgumentException>(fun () -> protocol.GetTool "add" |> ignore)
        |> ignore

    [<TestMethod>]
    member _.InvokeAsyncCallsCorrectTool() =
        let protocol = ToolProtocol.fromTools tools
        let result = (protocol.InvokeAsync "add" "5").Result
        Assert.IsTrue(result.Success)
        Assert.AreEqual("result:5", result.Output)
        Assert.IsTrue(result.DurationMs >= 0L)

    [<TestMethod>]
    member _.InvokeAsyncReturnsErrorForMissingTool() =
        let protocol = ToolProtocol.fromTools tools
        let result = (protocol.InvokeAsync "unknown" "x").Result
        Assert.IsFalse(result.Success)
        Assert.IsTrue(result.Error.IsSome)
        Assert.IsTrue(result.Error.Value.Contains("not found"))

    [<TestMethod>]
    member _.InvokeAsyncHandlesException() =
        let failTools = [ Tool.Create("fail", "Fails", ToolSignature.Text, (fun _ -> failwith "boom")) ]
        let protocol = ToolProtocol.fromTools failTools
        let result = (protocol.InvokeAsync "fail" "x").Result
        Assert.IsFalse(result.Success)
        Assert.IsTrue(result.Error.Value.Contains("boom"))

    [<TestMethod>]
    member _.IsAvailableReturnsTrueForExisting() =
        let protocol = ToolProtocol.fromTools tools
        Assert.IsTrue((protocol.IsAvailable "add").Result)
        Assert.IsFalse((protocol.IsAvailable "missing").Result)

    [<TestMethod>]
    member _.WithMiddlewareBlocksOnBeforeError() =
        let blockMiddleware =
            { new IToolMiddleware with
                member _.BeforeExecute _name _input = Task.FromResult(Error "blocked")
                member _.AfterExecute _name result = Task.FromResult result }
        let protocol = ToolProtocol.fromTools tools |> ToolProtocol.withMiddleware blockMiddleware
        let result = (protocol.InvokeAsync "add" "5").Result
        Assert.IsFalse(result.Success)
        Assert.AreEqual(Some "blocked", result.Error)

    [<TestMethod>]
    member _.RateLimitMiddlewareAllowsWithinLimit() =
        let middleware = ToolProtocol.rateLimitMiddleware 100
        let result = (middleware.BeforeExecute "test" "input").Result
        match result with
        | Ok v -> Assert.AreEqual("input", v)
        | Error _ -> Assert.Fail("Should be allowed")

