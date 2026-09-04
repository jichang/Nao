namespace Nao.Protocols.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Protocols

[<TestClass>]
type ProtocolTests() =
    let descriptor =
        { Name = "test actions"
          Description = "A compact test response protocol."
          Instructions = [ "Return action <name>." ]
          Examples = [ "action sample" ]
          MediaType = Some "text/x-test-actions"
          Metadata = Map.ofList [ "compact", "true" ] }

    let parse response =
        if response = "action sample" then
            Ok [ "sample" ]
        else
            Error
                { Summary = "Unknown action syntax."
                  Location = Some "line 1"
                  Expected = Some "action <name>"
                  SuggestedFix = Some "Return the example exactly."
                  Details = Map.ofList [ "received", response ] }

    let protocol =
        ResponseProtocol.create descriptor parse (fun error -> "Repair: " + ResponseParseError.format error)

    [<TestMethod>]
    member _.``protocol bundles prompt description rules and examples``() =
        let prompt = ResponseProtocol.promptInstructions protocol
        StringAssert.Contains(prompt, "# Response Protocol: test actions")
        StringAssert.Contains(prompt, "Return action <name>.")
        StringAssert.Contains(prompt, "action sample")
        Assert.AreEqual(Some "text/x-test-actions", protocol.Descriptor.MediaType)

    [<TestMethod>]
    member _.``protocol returns values or structured repair diagnostics``() =
        Assert.AreEqual(Ok [ "sample" ], protocol.Parse "action sample")

        match protocol.Parse "invalid" with
        | Error error ->
            Assert.AreEqual(Some "line 1", error.Location)
            StringAssert.Contains(ResponseParseError.format error, "Expected: action <name>")
            StringAssert.Contains(protocol.BuildRepairMessage error, "Return the example exactly")
        | Ok actions -> Assert.Fail(sprintf "Expected an error, got %A" actions)

    [<TestMethod>]
    member _.``JSON5-compatible values normalize to strict compact JSON``() =
        let input = "{source:'sample}.md',targets:['sample.html',],}"

        match JsonValueFormat.json5Compatible.Normalize input with
        | Ok canonical -> Assert.AreEqual("{\"source\":\"sample}.md\",\"targets\":[\"sample.html\"]}", canonical)
        | Error error -> Assert.Fail(ValueFormatError.format error)

    [<TestMethod>]
    member _.``balanced object extraction respects single-quoted braces``() =
        let input = "params {source:'sample}.md',targets:['sample.html']} trailing"
        Assert.AreEqual(Some "{source:'sample}.md',targets:['sample.html']}", JsonText.tryExtractBalancedObject input)

    [<TestMethod>]
    member _.``redundant object delimiters normalize nested objects but preserve strings``() =
        let doubled = "{{source:'sample}}.md',model:{{body:{{value:'{{literal}}'}}}}}}"
        let expected = "{source:'sample}}.md',model:{body:{value:'{{literal}}'}}}"
        Assert.AreEqual(expected, JsonText.normalizeRedundantObjectDelimiters doubled)

        Assert.AreEqual(
            "{{source:'sample.md'} trailing}",
            JsonText.normalizeRedundantObjectDelimiters "{{source:'sample.md'} trailing}"
        )
