namespace Nao.Assistant.Tests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text.Json
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Feedback
open Nao.Assistant

/// Integration tests for the assistant app's feedback & suggestion enhancement loop.
///
/// These exercise the real HTTP surface the desktop app drives through `NaoClient`,
/// hosted by `EmbeddedServer.startEnhancementHost` — a lightweight Kestrel host that
/// maps the same enhancement endpoints as production but WITHOUT the Orleans silo or
/// an LLM, so the full loop can be tested deterministically and offline.
module TestHost =

    /// A sample tool definition (JSON-sourced, so it carries provenance) used as the
    /// target of seeded feedback and improvement suggestions.
    let echoToolJson = """{
  "name": "echo",
  "description": "Echo back the input text.",
  "execution": { "type": "process", "command": "echo", "args": ["{{input}}"] },
  "output_content_type": "text"
}"""

    let agentJson = """{
  "name": "nao-assistant",
  "description": "Test assistant agent.",
  "prompt": {
    "role": "You are Nao, a helpful assistant.",
    "objective": "Help users.",
    "constraints": ["Be concise"]
  },
  "tools": ["echo"],
  "max_rounds": 5
}"""

    /// Reserve an ephemeral loopback port for a test host.
    let freePort () =
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        listener.Stop()
        port

    let private writeJson (path: string) (content: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    /// A self-contained, disposable test fixture: a temp workspace (with a JSON-sourced
    /// echo tool + agent), a temp feedback directory, a running enhancement host, and a
    /// connected `NaoClient`. A direct `FeedbackService` over the same feedback dir lets
    /// tests seed turns/feedback the way the Orleans-hosted grain would in production.
    type Fixture() =
        let root = Path.Combine(Path.GetTempPath(), "nao-assistant-tests", Guid.NewGuid().ToString("N"))
        let workspaceRoot = Path.Combine(root, "workspace")
        let feedbackDir = Path.Combine(root, "feedback")
        let echoToolPath = Path.Combine(workspaceRoot, ".nao", "tools", "echo.json")

        do
            writeJson echoToolPath echoToolJson
            writeJson (Path.Combine(workspaceRoot, ".nao", "agents", "nao-assistant.json")) agentJson
            Directory.CreateDirectory feedbackDir |> ignore

        let port = freePort ()
        let host = EmbeddedServer.startEnhancementHost workspaceRoot feedbackDir port
        let baseUrl = sprintf "http://127.0.0.1:%d" port
        let client = new NaoClient(baseUrl)
        let feedback = FeedbackService.File feedbackDir

        member _.Client = client
        member _.Feedback = feedback
        member _.WorkspaceRoot = workspaceRoot
        member _.EchoToolPath = echoToolPath

        /// Record a turn that used the echo tool and attach negative feedback to it —
        /// mirrors what `SessionGrain.SubmitFeedbackAsync` does, seeding both a live
        /// annotation and the cross-session suggestion pipeline.
        member _.SeedNegativeEchoFeedbackAsync(turnId: string) : Task =
            task {
                let turn =
                    { TurnRecord.Empty with
                        TurnId = turnId
                        SessionId = "s1"
                        UserId = "tester"
                        WorkspaceKey = "default"
                        AgentName = "nao-assistant"
                        Input = "echo please"
                        Output = "please"
                        ToolCalls =
                            [ { Name = "echo"
                                Version = None
                                Input = "please"
                                Output = "please"
                                Provenance = Some (ToolProvenance.json echoToolPath) } ] }
                do! feedback.RecordTurnAsync turn
                let fb =
                    { Id = Guid.NewGuid()
                      TurnId = turnId
                      SessionId = "s1"
                      UserId = "tester"
                      Sentiment = FeedbackSentiment.Negative
                      Comment = Some "the echo output was confusing"
                      CreatedAt = DateTimeOffset.UtcNow
                      Metadata = Map.empty }
                let! _ = feedback.SubmitFeedbackAsync fb
                return ()
            }

        interface IDisposable with
            member _.Dispose() =
                (client :> IDisposable).Dispose()
                try (host :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
                with _ -> ()
                try Directory.Delete(root, true) with _ -> ()

    /// Build an `AnnotationRequest` with only the fields a test cares about.
    let annotationRequest (kind: string) (target: string) (descriptionAppend: string) : AnnotationRequest =
        { Kind = kind
          TargetName = target
          BaseVersion = ""
          DescriptionOverride = ""
          DescriptionAppend = descriptionAppend
          InputPrefix = ""
          OutputSuffix = ""
          GuidanceAppend = ""
          Reason = "test" }


[<TestClass>]
type AnnotationFlowTests() =

    [<TestMethod>]
    member _.``add, list, disable and drop an annotation``() =
        use fx = new TestHost.Fixture()
        (task {
            let! ann = fx.Client.AddAnnotationAsync(TestHost.annotationRequest "tool" "echo" "Prefer concise output.")
            Assert.AreEqual("echo", ann.TargetName)
            Assert.AreEqual(AnnotationKind.Tool, ann.Kind)
            Assert.AreEqual(AnnotationStatus.Active, ann.Status)

            let! anns = fx.Client.ListAnnotationsAsync()
            Assert.IsTrue(anns |> List.exists (fun a -> a.Id = ann.Id), "annotation should appear in the list")

            let! disabled = fx.Client.SetAnnotationStatusAsync(ann.Id, "disabled")
            Assert.IsTrue(disabled, "disabling an existing annotation should succeed")

            let! dropped = fx.Client.DropAnnotationAsync(ann.Id)
            Assert.IsTrue(dropped, "dropping an existing annotation should succeed")

            let! droppedAgain = fx.Client.DropAnnotationAsync(ann.Id)
            Assert.IsFalse(droppedAgain, "dropping a missing annotation should report not-found")
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``invalid annotation kind is rejected``() =
        use fx = new TestHost.Fixture()
        (task {
            let mutable threw = false
            try
                let! _ = fx.Client.AddAnnotationAsync(TestHost.annotationRequest "bogus" "echo" "x")
                ()
            with _ -> threw <- true
            Assert.IsTrue(threw, "an unknown kind should produce an API error")
        }).GetAwaiter().GetResult()


[<TestClass>]
type VersionFlowTests() =

    [<TestMethod>]
    member _.``promote, confirm and deprecate a version``() =
        use fx = new TestHost.Fixture()
        (task {
            do! fx.SeedNegativeEchoFeedbackAsync("t-version")

            let! before = fx.Client.ListVersionsAsync()
            Assert.AreEqual(0, before.Length, "no versions before promotion")

            let! version = fx.Client.PromoteVersionAsync({ Kind = "tool"; TargetName = "echo"; Version = "" })
            Assert.AreEqual("echo", version.TargetName)
            Assert.AreEqual(VersionStatus.Draft, version.Status)

            let! after = fx.Client.ListVersionsAsync()
            Assert.IsTrue(after |> List.exists (fun v -> v.Id = version.Id))

            let! confirmed = fx.Client.ConfirmVersionAsync(version.Id, true)
            Assert.IsTrue(confirmed)

            let! deprecated = fx.Client.DeprecateVersionAsync(version.Id)
            Assert.IsTrue(deprecated)

            let! confirmMissing = fx.Client.ConfirmVersionAsync(Guid.NewGuid())
            Assert.IsFalse(confirmMissing, "confirming an unknown version should report not-found")
        }).GetAwaiter().GetResult()


[<TestClass>]
type SuggestionFlowTests() =

    [<TestMethod>]
    member _.``generate, confirm, build candidate and upgrade``() =
        use fx = new TestHost.Fixture()
        (task {
            do! fx.SeedNegativeEchoFeedbackAsync("t-suggest")

            let! generated = fx.Client.GenerateSuggestionsAsync()
            Assert.IsTrue(generated.Length >= 1, "negative feedback should produce a suggestion")
            let suggestion = generated |> List.find (fun s -> s.TargetName = "echo")
            Assert.AreEqual(AnnotationKind.Tool, suggestion.Kind)
            Assert.AreEqual(SuggestionStatus.Proposed, suggestion.Status)

            let! listed = fx.Client.ListSuggestionsAsync()
            Assert.IsTrue(listed |> List.exists (fun s -> s.Id = suggestion.Id))

            let! confirmed = fx.Client.ConfirmSuggestionAsync(suggestion.Id)
            Assert.IsTrue(confirmed)

            let! improvements = fx.Client.BuildCandidateAsync()
            Assert.IsTrue(improvements >= 1, "candidate should contain the confirmed improvement")

            let! results = fx.Client.UpgradeCandidateAsync()
            Assert.IsTrue(results.Length >= 1, "upgrade should report at least one applied improvement")
            Assert.IsTrue(results |> List.exists (fun r -> r.Target = "echo"))
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``rejecting a suggestion prevents confirmation``() =
        use fx = new TestHost.Fixture()
        (task {
            do! fx.SeedNegativeEchoFeedbackAsync("t-reject")

            let! generated = fx.Client.GenerateSuggestionsAsync()
            let suggestion = generated |> List.find (fun s -> s.TargetName = "echo")

            let! rejected = fx.Client.RejectSuggestionAsync(suggestion.Id)
            Assert.IsTrue(rejected)

            let! confirmAfter = fx.Client.ConfirmSuggestionAsync(suggestion.Id)
            Assert.IsFalse(confirmAfter, "a rejected suggestion can no longer be confirmed")
        }).GetAwaiter().GetResult()


[<TestClass>]
type RegisterFlowTests() =

    [<TestMethod>]
    member _.``register a new tool definition writes it to the workspace``() =
        use fx = new TestHost.Fixture()
        (task {
            let toolJson = """{
  "name": "greet",
  "description": "Greet the user.",
  "execution": { "type": "process", "command": "echo", "args": ["hi {{input}}"] },
  "output_content_type": "text"
}"""
            use doc = JsonDocument.Parse(toolJson)
            let request = { Name = "greet"; Definition = doc.RootElement }
            let! path = fx.Client.RegisterToolAsync(request)
            Assert.IsTrue(File.Exists path, "the registered tool definition should be written to disk")
            Assert.IsTrue(path.EndsWith("greet.json"))
        }).GetAwaiter().GetResult()
