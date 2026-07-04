namespace Nao.Assistant.Tests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text.Json
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence
open Nao.Agents
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
        let feedback = FeedbackDb.file feedbackDir

        member _.Client = client
        member _.Feedback = feedback
        member _.WorkspaceRoot = workspaceRoot
        member _.EchoToolPath = echoToolPath

        /// Record a turn that used the echo tool and attach negative feedback to it —
        /// mirrors what `SessionGrain.SubmitFeedbackAsync` does, seeding the
        /// cross-session suggestion pipeline.
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
                do! feedback.SubmitFeedbackAsync fb
                return ()
            }

        interface IDisposable with
            member _.Dispose() =
                (client :> IDisposable).Dispose()
                try (host :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
                with _ -> ()
                try Directory.Delete(root, true) with _ -> ()


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
