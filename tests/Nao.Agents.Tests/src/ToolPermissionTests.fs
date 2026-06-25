namespace Nao.Agents.Tests

open System.Text.Json
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

/// Tests for the permission-aware Tool surface: the static `Permissions` a tool declares are
/// auto-requested through the `ToolContext` before `Execute` runs, a denied one short-circuits
/// with the canonical structured refusal, and tools can request access dynamically mid-run.
[<TestClass>]
type ToolPermissionTests() =

    /// A ToolContext whose RequestPermission returns a fixed answer and records what was asked.
    let recordingCtx (sessionKey: string) (answer: bool) =
        let asked = ResizeArray<ResourceAccess>()
        let ctx =
            { SessionKey = sessionKey
              RequestPermission =
                fun access _reason ->
                    asked.Add access
                    Task.FromResult answer }
        ctx, asked

    [<TestMethod>]
    member _.InvokeAsyncRequestsDeclaredPermissionsThenExecutes() =
        let ran = ref false
        let tool =
            Tool.Create(
                "writer",
                "writes",
                [ ResourceAccess.File("write", "/tmp/a.txt") ],
                fun _ctx input ->
                    task {
                        ran.Value <- true
                        return "ok:" + input
                    })
        let ctx, asked = recordingCtx "u/1" true
        let result = tool.InvokeAsync(ctx, "hi").Result
        Assert.AreEqual("ok:hi", result)
        Assert.IsTrue(ran.Value)
        Assert.AreEqual(1, asked.Count)

    [<TestMethod>]
    member _.InvokeAsyncDeniesAndShortCircuits() =
        let ran = ref false
        let tool =
            Tool.Create(
                "fetcher",
                "fetches",
                [ ResourceAccess.Web("GET", "https://example.com") ],
                fun _ctx _ ->
                    task {
                        ran.Value <- true
                        return "ran"
                    })
        let ctx, _ = recordingCtx "u/1" false
        let result = tool.InvokeAsync(ctx, "x").Result
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
            { SessionKey = ""
              RequestPermission =
                fun access _ ->
                    asked.Add access
                    Task.FromResult false }
        let tool =
            Tool.Create(
                "multi",
                "multi",
                [ ResourceAccess.File("read", "/a"); ResourceAccess.File("write", "/b") ],
                fun _ _ -> task { return "ran" })
        tool.InvokeAsync(ctx, "x").Result |> ignore
        Assert.AreEqual(1, asked.Count)

    [<TestMethod>]
    member _.FourArgCreateThreadsContextToExecute() =
        let seen = ref ""
        let tool =
            Tool.Create("ctxtool", "ctx", [], fun ctx _ -> task {
                seen.Value <- ctx.SessionKey
                return "ok" })
        let ctx, _ = recordingCtx "user/42" true
        tool.InvokeAsync(ctx, "x").Result |> ignore
        Assert.AreEqual("user/42", seen.Value)

    [<TestMethod>]
    member _.DynamicRequestInsideExecuteIsHonored() =
        // No static permissions; the tool asks dynamically once it knows its target.
        let tool =
            Tool.Create("dyn", "dynamic", [], fun ctx input -> task {
                let! ok = ctx.RequestPermission (ResourceAccess.File("write", input)) "save"
                return (if ok then "wrote" else "blocked") })
        let allowCtx, _ = recordingCtx "" true
        let denyCtx, _ = recordingCtx "" false
        Assert.AreEqual("wrote", tool.InvokeAsync(allowCtx, "/p").Result)
        Assert.AreEqual("blocked", tool.InvokeAsync(denyCtx, "/p").Result)

    [<TestMethod>]
    member _.AllowAllContextPermitsEverything() =
        let tool =
            Tool.Create(
                "w",
                "w",
                [ ResourceAccess.Web("GET", "https://x.com") ],
                fun _ _ -> task { return "done" })
        Assert.AreEqual("done", tool.InvokeAsync(ToolContext.allowAll, "x").Result)

    [<TestMethod>]
    member _.LegacyCreateHasNoDeclaredPermissions() =
        let tool = Tool.Create("legacy", "legacy", fun input -> task { return input })
        Assert.AreEqual(0, tool.Permissions.Length)
        Assert.AreEqual("hi", tool.InvokeAsync(ToolContext.allowAll, "hi").Result)

    [<TestMethod>]
    member _.PermissionDeniedFormatIncludesHintWhenProvided() =
        let payload = PermissionDenied.format (ResourceAccess.File("write", "/etc/x")) (Some "do this")
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
