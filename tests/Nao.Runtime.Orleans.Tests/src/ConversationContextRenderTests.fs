namespace Nao.Runtime.Orleans.Tests

open System.Collections.Generic
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Runtime.Orleans.Grains

/// Tests for the recent-transcript rendering that gives an agent turn the context it needs
/// to resolve follow-up references such as "convert it to html".
[<TestClass>]
type ConversationContextRenderTests() =

    let msg (role: string) (content: string) (attachments: string list) =
        let r = MessageRecord(Role = role, Content = content)
        r.Attachments <- ResizeArray<string>(attachments)
        r

    [<TestMethod>]
    member _.EmptyHistory_ReturnsInputUnchanged() =
        let input = "convert it to html"
        let result = ConversationContextRender.withHistory 8 (Seq.empty) input
        Assert.AreEqual(input, result)

    [<TestMethod>]
    member _.WithHistory_PrefixesTranscriptAndKeepsNewRequest() =
        let history =
            [ msg "User" "Convert this markdown file to pdf" [ "report.md" ]
              msg "Assistant" "Conversion started." [] ]

        let result = ConversationContextRender.withHistory 8 history "convert it to html"
        // The earlier turn and the new request must both be present so "it" can be resolved.
        StringAssert.Contains(result, "Convert this markdown file to pdf")
        StringAssert.Contains(result, "convert it to html")

    [<TestMethod>]
    member _.WithHistory_SurfacesAttachmentNames() =
        // The attachment name is the real subject of the follow-up; it must appear so the
        // agent uses the actual file instead of inventing one (e.g. "notes.md").
        let history = [ msg "User" "Convert this markdown file to pdf" [ "report.md" ] ]
        let result = ConversationContextRender.withHistory 8 history "convert it to html"
        StringAssert.Contains(result, "report.md")

    [<TestMethod>]
    member _.RecentTranscript_KeepsOnlyTheLastNMessages() =
        let history = [ for i in 1..10 -> msg "User" (sprintf "message %d" i) [] ]
        let result = ConversationContextRender.recentTranscript 3 history
        StringAssert.Contains(result, "message 8")
        StringAssert.Contains(result, "message 9")
        StringAssert.Contains(result, "message 10")
        Assert.IsFalse(result.Contains("message 7"))
