namespace Nao.Agents.Tests

open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Assistant

/// Tests for the ```application/json+nao``` action-block planner protocol.
[<TestClass>]
type ActionBlockParseTests() =

    let provider: ILlmProvider =
        { new ILlmProvider with
            member _.CompleteAsync _conversation _options =
                Task.FromResult { Content = "unused"; FinishReason = "stop"; TokensUsed = None }
            member _.Name = "stub" }

    let scriptedProvider (responses: string list) (conversations: List<Conversation>) : ILlmProvider =
        let queue = Queue<string>(responses)
        { new ILlmProvider with
            member _.CompleteAsync conversation _options =
                conversations.Add conversation
                let content = if queue.Count > 0 then queue.Dequeue() else "done"
                Task.FromResult { Content = content; FinishReason = "stop"; TokensUsed = None }
            member _.Name = "scripted" }

    let convertTool = Tool.Create("convert_document", "Converts documents.", fun _ -> Task.FromResult "ok")

    let converterAgent: IAgent =
        { new IAgent with
            member _.Id = { Name = "converter"; Description = "doc converter" }
            member _.RunAsync(_input) = Task.FromResult "done"
            member _.HandleMessageAsync(_msg) = Task.FromResult None }

    let orchestrator = NaoOrchestrator({ Id = { Name = "orchestrator"; Description = "test orchestrator" }; Provider = provider; Tools = [ convertTool ]; SubAgents = [ converterAgent ]; Prompt = Prompt.Empty; Options = CompletionOptions.Default; MaxRounds = 5; Bus = EventBus.none; Scope = EventScope.Empty; Memory = OrchestratorMemoryConfig.None; Instructions = None; Context = ToolContext.allowAll })

    let makeOrchestrator provider tools =
        NaoOrchestrator({ Id = { Name = "orchestrator"; Description = "test orchestrator" }; Provider = provider; Tools = tools; SubAgents = [ converterAgent ]; Prompt = Prompt.Empty; Options = CompletionOptions.Default; MaxRounds = 5; Bus = EventBus.none; Scope = EventScope.Empty; Memory = OrchestratorMemoryConfig.None; Instructions = None; Context = ToolContext.allowAll })

    let fence (inner: string) =
        sprintf "```application/json+nao\n%s\n```" inner

    [<TestMethod>]
    member _.ParsesFencedActionBlock() =
        let content = fence """{"actions":[{"type":"tool","name":"convert_document","params":"a.md|pdf"}]}"""
        let actions = orchestrator.DefaultTryParseActions(content)
        Assert.AreEqual(1, List.length actions)
        match actions.[0] with
        | InvokeTool ("convert_document", "a.md|pdf") -> ()
        | other -> Assert.Fail(sprintf "Unexpected action: %A" other)
        Assert.IsFalse(orchestrator.HasMalformedActionBlock(content))

    [<TestMethod>]
    member _.ParsesFencedRespondActionWithoutName() =
        let content = fence """{"actions":[{"type":"respond","response":"Hello."}]}"""
        let actions = orchestrator.DefaultTryParseActions(content)
        Assert.AreEqual(1, List.length actions)
        match actions.[0] with
        | Respond response -> Assert.AreEqual("Hello.", response)
        | other -> Assert.Fail(sprintf "Unexpected action: %A" other)
        Assert.IsFalse(orchestrator.HasMalformedActionBlock(content))

    [<TestMethod>]
    member _.ParsesFencedBlockSurroundedByProse() =
        let content =
            "Sure, converting now.\n" + fence """{"actions":[{"type":"delegate","name":"converter","params":"Convert a.md to PDF and HTML"}]}""" + "\nLet me know if you need more."
        let actions = orchestrator.DefaultTryParseActions(content)
        Assert.AreEqual(1, List.length actions)
        match actions.[0] with
        | DelegateToAgent ("converter", "Convert a.md to PDF and HTML") -> ()
        | other -> Assert.Fail(sprintf "Unexpected action: %A" other)

    [<TestMethod>]
    member _.DetectsMalformedFencedBlock() =
        // The exact failure seen in production: a missing ']' before the final '}'.
        let content = fence """{"actions":[{"type":"delegate","name":"converter","params":"convert a.md|html"}}"""
        Assert.IsTrue(orchestrator.HasMalformedActionBlock(content), "malformed JSON in the block should be flagged")
        Assert.AreEqual(0, List.length (orchestrator.DefaultTryParseActions(content)))

    [<TestMethod>]
    member _.ParsesObjectParamsAsRawJson() =
        // Tools take JSON-object inputs, so "params" is normally an object — it must be passed
        // through to the tool as its raw JSON text.
        let content = fence """{"actions":[{"type":"tool","name":"convert_document","params":{"source":"a.md","target":"pdf"}}]}"""
        let actions = orchestrator.DefaultTryParseActions(content)
        Assert.AreEqual(1, List.length actions)
        match actions.[0] with
        | InvokeTool ("convert_document", raw) ->
            StringAssert.Contains(raw, "\"source\":\"a.md\"")
            StringAssert.Contains(raw, "\"target\":\"pdf\"")
        | other -> Assert.Fail(sprintf "Unexpected action: %A" other)

    [<TestMethod>]
    member _.ExplicitBlockMustBeCompleteJsonNotPartiallyRecovered() =
        let content = fence """{"actions":[{"type":"tool","name":"convert_document","params":{"source":"a.md","target":"html"}},{"type":"delegate","name":"convert_document","params":"{\"source\":\"a.md\",\"target\":\"pdf\"}"}}"""
        Assert.IsTrue(orchestrator.HasMalformedActionBlock(content), "an explicit action block with a missing closing array must be repaired, not partially executed")
        Assert.AreEqual(0, List.length (orchestrator.DefaultTryParseActions(content)))

    [<TestMethod>]
    member _.DelegateToKnownToolIsTreatedAsToolInvocation() =
        let content = fence """{"actions":[{"type":"delegate","name":"convert_document","params":{"source":"a.md","target":"pdf"}}]}"""
        let actions = orchestrator.DefaultTryParseActions(content)
        Assert.AreEqual(1, List.length actions)
        match actions.[0] with
        | InvokeTool ("convert_document", raw) ->
            StringAssert.Contains(raw, "\"source\":\"a.md\"")
            StringAssert.Contains(raw, "\"target\":\"pdf\"")
        | other -> Assert.Fail(sprintf "Unexpected action: %A" other)

    [<TestMethod>]
    member _.ParsesTopLevelArrayActionBlock() =
        let content = fence """[{"type":"tool","name":"convert_document","params":{"source":"a.md","target":"html"}},{"type":"tool","name":"convert_document","params":{"source":"a.md","target":"pdf"}}]"""
        let actions = orchestrator.DefaultTryParseActions(content)
        Assert.AreEqual(2, List.length actions)
        match actions.[0], actions.[1] with
        | InvokeTool ("convert_document", html), InvokeTool ("convert_document", pdf) ->
            StringAssert.Contains(html, "\"target\":\"html\"")
            StringAssert.Contains(pdf, "\"target\":\"pdf\"")
        | other -> Assert.Fail(sprintf "Unexpected actions: %A" other)

    [<TestMethod>]
    member _.ReportsSchemaGuidanceForActionBlockMissingOnlyRootClosingBrace() =
        let content = fence """{"actions":[{"type":"tool","name":"convert_document","params":{"source":"a.md","target":"html"}},{"type":"tool","name":"convert_document","params":{"source":"a.md","target":"pdf"}}]"""
        Assert.AreEqual(0, List.length (orchestrator.DefaultTryParseActions(content)))
        Assert.IsTrue(orchestrator.HasMalformedActionBlock(content), "invalid JSON should be sent back to the LLM for repair")
        match orchestrator.TryGetActionBlockValidationError(content) with
        | Some error ->
            StringAssert.Contains(error, "JSON syntax error")
            StringAssert.Contains(error, "line")
        | None -> Assert.Fail("Expected validation guidance for malformed JSON")

    [<TestMethod>]
    member _.ReportsSchemaGuidanceForWrongActionShape() =
        let content = fence """{"actions":[{"name":"convert_document","params":{"source":"a.md","target":"pdf"}}]}"""
        Assert.AreEqual(0, List.length (orchestrator.DefaultTryParseActions(content)))
        match orchestrator.TryGetActionBlockValidationError(content) with
        | Some error ->
            StringAssert.Contains(error, "JSON schema validation failed")
            StringAssert.Contains(error, "type")
        | None -> Assert.Fail("Expected schema validation guidance for an action missing type")

    [<TestMethod>]
    member _.RunLoopAsksModelToRepairInvalidActionBlockWithValidationGuidance() =
        let conversations = List<Conversation>()
        let invoked = ref false
        let tool =
            Tool.Create(
                "convert_document",
                "Converts documents.",
                fun _ ->
                    invoked.Value <- true
                    Task.FromResult "converted")
        let invalid = fence """{"actions":[{"type":"tool","name":"convert_document","params":{"source":"a.md","target":"pdf"}}]"""
        let corrected = fence """{"actions":[{"type":"tool","name":"convert_document","params":{"source":"a.md","target":"pdf"}}]}"""
        let provider = scriptedProvider [ invalid; corrected; "done" ] conversations
        let result = ((makeOrchestrator provider [ tool ]) :> IAgent).RunAsync("convert a.md to pdf").Result
        Assert.AreEqual("done", result)
        Assert.IsTrue(invoked.Value, "The corrected action should execute after repair")
        let repairPrompt =
            conversations
            |> Seq.collect id
            |> Seq.map (fun msg -> msg.Content)
            |> Seq.tryFind (fun content -> content.Contains("Validation error:"))
        match repairPrompt with
        | Some prompt ->
            StringAssert.Contains(prompt, "JSON syntax error")
            StringAssert.Contains(prompt, "application/json+nao")
        | None -> Assert.Fail("Expected the repair request to include validation guidance")

    [<TestMethod>]
    member _.PlainAnswerIsNotAnActionAndNotMalformed() =
        let content = "Here is your summary: the document has three sections."
        Assert.AreEqual(0, List.length (orchestrator.DefaultTryParseActions(content)))
        Assert.IsFalse(orchestrator.HasMalformedActionBlock(content))

    [<TestMethod>]
    member _.JsonFinalAnswerWithoutBlockIsNotMalformed() =
        // A user may ask for a JSON answer; without the nao block it is a final answer,
        // never a "malformed action block".
        let content = """{"title":"Report","sections":3}"""
        Assert.IsFalse(orchestrator.HasMalformedActionBlock(content))

    [<TestMethod>]
    member _.LegacyBareJsonActionStillParses() =
        let content = """{"actions":[{"type":"tool","name":"convert_document","params":"a.md|pdf"}]}"""
        let actions = orchestrator.DefaultTryParseActions(content)
        Assert.AreEqual(1, List.length actions)
        match actions.[0] with
        | InvokeTool ("convert_document", "a.md|pdf") -> ()
        | other -> Assert.Fail(sprintf "Unexpected action: %A" other)
