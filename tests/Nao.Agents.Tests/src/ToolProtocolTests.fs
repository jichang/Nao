namespace Nao.Agents.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type ToolSchemaTests() =

    let makeTool name desc =
        Tool.Create(name, desc, fun _ -> Task.FromResult "ok")

    [<TestMethod>]
    member _.FromToolCreatesBasicSchema() =
        let tool = makeTool "search" "Search the web"
        let schema = ToolSchema.fromTool tool
        Assert.AreEqual("search", schema.Name)
        Assert.AreEqual("Search the web", schema.Description)
        Assert.AreEqual(1, schema.Parameters.Length)
        Assert.AreEqual(ToolCostCategory.Unknown, schema.CostCategory)

    [<TestMethod>]
    member _.RenderProducesFormattedText() =
        let schema =
            { Name = "calc"
              Description = "Calculate expression"
              Category = Some "math"
              Parameters = [ { Name = "expr"; Description = "Expression"; Type = "string"; Required = true; Default = None; Examples = [] } ]
              ReturnDescription = Some "Numeric result"
              Examples = [ { Scenario = "Addition"; Input = "2+2"; ExpectedOutput = "4" } ]
              IsSideEffectFree = true
              CostCategory = ToolCostCategory.Free
              Version = "1.0" }
        let rendered = ToolSchema.render schema
        Assert.IsTrue(rendered.Contains("calc"))
        Assert.IsTrue(rendered.Contains("Calculate expression"))
        Assert.IsTrue(rendered.Contains("expr"))
        Assert.IsTrue(rendered.Contains("Addition"))

[<TestClass>]
type ToolProtocolTests() =

    let tools =
        [ Tool.Create("add", "Add numbers", fun input -> Task.FromResult (sprintf "result:%s" input))
          Tool.Create("sub", "Subtract numbers", fun _ -> Task.FromResult "subtracted") ]

    [<TestMethod>]
    member _.FromToolsListsAll() =
        let protocol = ToolProtocol.fromTools tools
        let schemas = protocol.ListTools().Result
        Assert.AreEqual(2, schemas.Length)

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
        let failTools = [ Tool.Create("fail", "Fails", fun _ -> failwith "boom") ]
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

[<TestClass>]
type ToolRouterTests() =

    let schemas =
                [ { Name = "search"; Description = "Search the web"; Category = None; Parameters = []; ReturnDescription = None; Examples = []; IsSideEffectFree = true; CostCategory = ToolCostCategory.Free; Version = ToolVersion.Default }
                    ;
                    { Name = "calculate"; Description = "Math calculator"; Category = None; Parameters = []; ReturnDescription = None; Examples = []; IsSideEffectFree = true; CostCategory = ToolCostCategory.Free; Version = ToolVersion.Default } ]

    [<TestMethod>]
    member _.SelectByNameFindsExact() =
        let result = ToolRouter.selectByName "search" schemas
        Assert.IsTrue(result.IsSome)
        Assert.AreEqual("search", result.Value.Tool.Name)
        Assert.AreEqual(1.0, result.Value.Confidence)

    [<TestMethod>]
    member _.SelectByNameReturnsNoneForMissing() =
        let result = ToolRouter.selectByName "missing" schemas
        Assert.IsTrue(result.IsNone)

    [<TestMethod>]
    member _.SelectByPatternMatchesKeywords() =
        let patterns = Map.ofList [ "search", ["find"; "look"; "web"] ]
        let result = ToolRouter.selectByPattern patterns "find something on the web" schemas
        Assert.IsTrue(result.IsSome)
        Assert.AreEqual("search", result.Value.Tool.Name)
        Assert.IsTrue(result.Value.Confidence > 0.0)

    [<TestMethod>]
    member _.SelectByPatternReturnsNoneIfNoMatch() =
        let patterns = Map.ofList [ "search", ["find"; "look"] ]
        let result = ToolRouter.selectByPattern patterns "calculate 2+2" schemas
        Assert.IsTrue(result.IsNone)
