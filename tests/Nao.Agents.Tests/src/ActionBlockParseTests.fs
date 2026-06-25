namespace Nao.Agents.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Core
open Nao.Agents

/// Tests for the tagged ```application/json+nao``` action-block protocol: the orchestrator
/// only treats a response as an action request when it carries that fenced block, parses
/// just its payload, and flags a block whose JSON is malformed so the run loop can ask the
/// model to repair it. A bare legacy JSON object is still accepted for backward compat.
[<TestClass>]
type ActionBlockParseTests() =

    let provider: ILlmProvider =
        { new ILlmProvider with
            member _.CompleteAsync _conversation _options =
                Task.FromResult { Content = "unused"; FinishReason = "stop"; TokensUsed = None }
            member _.Name = "stub" }

    let convertTool = Tool.Create("convert_document", "Converts documents.", fun _ -> Task.FromResult "ok")

    let converterAgent: IAgent =
        { new IAgent with
            member _.Id = { Name = "converter"; Description = "doc converter" }
            member _.State = AgentState.Empty
            member _.RunAsync(_input) = Task.FromResult "done"
            member _.HandleMessageAsync(_msg) = Task.FromResult None }

    let orchestrator =
        Orchestrator(
            { Provider = provider
              Tools = [ convertTool ]
              SubAgents = [ converterAgent ]
              Prompt = Prompt.Empty
              Options = CompletionOptions.Default
              MaxRounds = 5
              EventSink = AgentEventSink.none
              Memory = OrchestratorMemoryConfig.None
              Instructions = None })

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
