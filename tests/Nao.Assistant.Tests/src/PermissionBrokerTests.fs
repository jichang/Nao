namespace Nao.Assistant.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Assistant

[<TestClass>]
type PermissionBrokerTests() =

    let json = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    [<TestMethod>]
    member _.NoClientConnectedDeniesByDefault() =
        // A session nobody is connected to cannot be asked → fail closed.
        let access = ResourceAccess.Web("GET", "https://nobody.example.com")

        let outcome =
            (PermissionBroker.requestAsync (Guid.NewGuid().ToString("N")) access "test").Result

        Assert.AreEqual(PermissionDecision.Deny, outcome.Decision)

    [<TestMethod>]
    member _.AllowOnceRoundTripsOverTransport() =
        let sessionKey = Guid.NewGuid().ToString("N")
        // The "client": parse the pushed request and immediately answer allow/once. This is
        // exactly what the WebSocket loop does when a real user clicks "Allow once".
        let sender (payload: string) : Task =
            let req = JsonSerializer.Deserialize<PermissionRequestDto>(payload, json)

            let resp =
                { PermissionResponseDto.RequestId = req.RequestId
                  Decision = "allow"
                  Scope = "once" }

            PermissionBroker.resolve (JsonSerializer.Serialize(resp, json))
            Task.CompletedTask

        PermissionBroker.registerSession sessionKey sender

        try
            let access = ResourceAccess.Web("GET", "https://ok.example.com")
            let outcome = (PermissionBroker.requestAsync sessionKey access "test").Result
            Assert.AreEqual(PermissionDecision.Allow, outcome.Decision)
        finally
            PermissionBroker.unregisterSession sessionKey

    [<TestMethod>]
    member _.DenyAnswerIsHonoured() =
        let sessionKey = Guid.NewGuid().ToString("N")

        let sender (payload: string) : Task =
            let req = JsonSerializer.Deserialize<PermissionRequestDto>(payload, json)

            let resp =
                { PermissionResponseDto.RequestId = req.RequestId
                  Decision = "deny"
                  Scope = "" }

            PermissionBroker.resolve (JsonSerializer.Serialize(resp, json))
            Task.CompletedTask

        PermissionBroker.registerSession sessionKey sender

        try
            let access = ResourceAccess.Web("GET", "https://blocked.example.com")
            let outcome = (PermissionBroker.requestAsync sessionKey access "test").Result
            Assert.AreEqual(PermissionDecision.Deny, outcome.Decision)
        finally
            PermissionBroker.unregisterSession sessionKey

    [<TestMethod>]
    member _.NoAnswerTimesOutToDeny() =
        let sessionKey = Guid.NewGuid().ToString("N")
        // A connected client that never answers must not hang the tool forever.
        let sender (_: string) : Task = Task.CompletedTask
        PermissionBroker.registerSession sessionKey sender
        let saved = PermissionBroker.Timeout
        PermissionBroker.Timeout <- TimeSpan.FromMilliseconds 150.0

        try
            let access = ResourceAccess.Web("GET", "https://slow.example.com")
            let outcome = (PermissionBroker.requestAsync sessionKey access "test").Result
            Assert.AreEqual(PermissionDecision.Deny, outcome.Decision)
        finally
            PermissionBroker.Timeout <- saved
            PermissionBroker.unregisterSession sessionKey
