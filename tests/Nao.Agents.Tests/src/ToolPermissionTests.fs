namespace Nao.Agents.Tests

open System.Text.Json
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

/// Tests for the permission-aware Tool surface: the static `Permissions` a tool declares are
/// auto-requested through the `AgentContext` before `Execute` runs, a denied one short-circuits
/// with the canonical structured refusal, and tools can request access dynamically mid-run.
[<TestClass>]
type ToolPermissionTests() =

    let createTool name description permissions execute =
        Tool.create
            name
            description
            0
            permissions
            ToolCodec.text
            ToolCodec.text
            (ToolOperation.create (fun context input ->
                task {
                    let! output = execute context input
                    return Ok output
                }))

    let run (tool: Tool) context input : ToolRunResult =
        tool.RunAsync context input |> fun task -> task.Result

    let outputOf (result: ToolRunResult) =
        match result with
        | Ok output -> output
        | Error failure ->
            Assert.Fail(failure.Message)
            ""

    /// An AgentContext whose RequestPermission returns a fixed answer and records what was asked.
    let recordingCtx (sessionKey: string) (answer: bool) =
        let asked = ResizeArray<ResourceAccess>()

        let ctx =
            { (AgentContext.unrestrictedForTests ()) with
                SessionKey = sessionKey
                RequestPermission =
                    fun access _reason _force ->
                        asked.Add access
                        Task.FromResult answer }

        ctx, asked

    [<TestMethod>]
    member _.InvokeAsyncRequestsDeclaredPermissionsThenExecutes() =
        let ran = ref false

        let tool =
            createTool "writer" "writes" [ ResourceAccess.File("write", "/tmp/a.txt") ] (fun _ctx input ->
                task {
                    ran.Value <- true
                    return "ok:" + input
                })

        let ctx, asked = recordingCtx "u/1" true
        let result = run tool ctx "hi" |> outputOf
        Assert.AreEqual("ok:hi", result)
        Assert.IsTrue(ran.Value)
        Assert.AreEqual(1, asked.Count)

    [<TestMethod>]
    member _.InvokeAsyncDeniesAndShortCircuits() =
        let ran = ref false

        let tool =
            createTool "fetcher" "fetches" [ ResourceAccess.Web("GET", "https://example.com") ] (fun _ctx _ ->
                task {
                    ran.Value <- true
                    return "ran"
                })

        let ctx, _ = recordingCtx "u/1" false

        let result =
            match run tool ctx "x" with
            | Error failure -> failure.Message
            | Ok _ ->
                Assert.Fail("Denied tool unexpectedly ran.")
                ""

        Assert.IsFalse(ran.Value)
        use doc = JsonDocument.Parse result
        Assert.AreEqual("permission_denied", doc.RootElement.GetProperty("error").GetString())
        Assert.AreEqual("web", doc.RootElement.GetProperty("kind").GetString())
        Assert.AreEqual("https://example.com", doc.RootElement.GetProperty("resource").GetString())

    [<TestMethod>]
    member _.InvokeAsyncStopsAtFirstDeniedPermission() =
        // Two declared permissions; the first is denied so the second is never requested.
        let asked = ResizeArray<ResourceAccess>()

        let ctx =
            { (AgentContext.unrestrictedForTests ()) with
                SessionKey = ""
                RequestPermission =
                    fun access _ _ ->
                        asked.Add access
                        Task.FromResult false }

        let tool =
            createTool
                "multi"
                "multi"
                [ ResourceAccess.File("read", "/a"); ResourceAccess.File("write", "/b") ]
                (fun _ _ -> task { return "ran" })

        run tool ctx "x" |> ignore
        Assert.AreEqual(1, asked.Count)

    [<TestMethod>]
    member _.FourArgCreateThreadsContextToExecute() =
        let seen = ref ""

        let tool =
            createTool "ctxtool" "ctx" [] (fun ctx _ ->
                task {
                    seen.Value <- ctx.SessionKey
                    return "ok"
                })

        let ctx, _ = recordingCtx "user/42" true
        run tool ctx "x" |> ignore
        Assert.AreEqual("user/42", seen.Value)

    [<TestMethod>]
    member _.DynamicRequestInsideExecuteIsHonored() =
        // No static permissions; the tool asks dynamically once it knows its target.
        let tool =
            createTool "dyn" "dynamic" [] (fun ctx input ->
                task {
                    let! ok = ctx.RequestPermission (ResourceAccess.File("write", input)) "save" false
                    return (if ok then "wrote" else "blocked")
                })

        let allowCtx, _ = recordingCtx "" true
        let denyCtx, _ = recordingCtx "" false
        Assert.AreEqual("wrote", run tool allowCtx "/p" |> outputOf)
        Assert.AreEqual("blocked", run tool denyCtx "/p" |> outputOf)

    [<TestMethod>]
    member _.AllowAllContextPermitsEverything() =
        let tool =
            createTool "w" "w" [ ResourceAccess.Web("GET", "https://x.com") ] (fun _ _ -> task { return "done" })

        Assert.AreEqual("done", run tool (AgentContext.unrestrictedForTests ()) "x" |> outputOf)

    [<TestMethod>]
    member _.CreateHasNoDeclaredPermissionsByDefault() =
        let tool = createTool "strict" "strict" [] (fun _ input -> task { return input })
        Assert.AreEqual(0, tool.Permissions.Length)
        Assert.AreEqual("hi", run tool (AgentContext.unrestrictedForTests ()) "hi" |> outputOf)

    [<TestMethod>]
    member _.PermissionDeniedFormatIncludesHintWhenProvided() =
        let payload =
            PermissionDenied.format (ResourceAccess.File("write", "/etc/x")) (Some "do this")

        use doc = JsonDocument.Parse payload
        Assert.AreEqual("permission_denied", doc.RootElement.GetProperty("error").GetString())
        Assert.AreEqual("file", doc.RootElement.GetProperty("kind").GetString())
        Assert.AreEqual("do this", doc.RootElement.GetProperty("hint").GetString())

    [<TestMethod>]
    member _.PermissionDeniedFormatOmitsHintWhenNone() =
        let payload = PermissionDenied.format (ResourceAccess.ToolCall "danger") None
        use doc = JsonDocument.Parse payload
        let mutable hint = Unchecked.defaultof<JsonElement>
        Assert.IsFalse(doc.RootElement.TryGetProperty("hint", &hint))
        Assert.AreEqual("tool", doc.RootElement.GetProperty("kind").GetString())
