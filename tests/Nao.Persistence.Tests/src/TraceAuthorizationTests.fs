namespace Nao.Persistence.Tests

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type TraceAuthorizationTests() =
    let scope tenant user group workspace session =
        let principal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse tenant)
                (UserId.parse user)
                (Some(ServiceId.parse "private-service"))
                [ GroupId.parse "group"; GroupId.parse "other" ]
                [ "private-role" ]
                (Map.ofList [ "private-claim", "private-value" ])
                AuthenticationStrength.MultiFactor

        AuthorizationScope.tryCreate
            principal
            (group |> Option.map GroupId.parse)
            (WorkspaceId.parse workspace)
            (session |> Option.map SessionId.parse)
        |> Option.get

    let owner = scope "tenant" "user" (Some "group") "workspace" (Some "session")

    let owners =
        [ owner
          scope "other" "user" (Some "group") "workspace" (Some "session")
          scope "tenant" "other" (Some "group") "workspace" (Some "session")
          scope "tenant" "user" (Some "other") "workspace" (Some "session")
          scope "tenant" "user" None "workspace" (Some "session")
          scope "tenant" "user" (Some "group") "other" (Some "session")
          scope "tenant" "user" (Some "group") "workspace" (Some "other")
          scope "tenant" "user" (Some "group") "workspace" None
          scope "a/b" "c" None "workspace" None
          scope "a_b" "c" None "workspace" None
          scope "a" "b/c" None "workspace" None ]

    let key authorization =
        AuthorizationScope.identity authorization |> AuthorizationIdentity.storageKey

    let timestamp = DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero)

    let trace agentId input =
        { Id = Guid.NewGuid()
          Correlation = CorrelationContext.root ()
          AgentId = agentId
          Input = input
          Output = Some "output"
          Steps = []
          StartedAt = timestamp
          CompletedAt = Some(timestamp.AddSeconds 1.0)
          Success = true
          Metadata = Map.empty }

    let withStorage action =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-trace-scope-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        let factory =
            DbConnectionFactory.ofFunc (fun () ->
                new SqliteConnection(sprintf "Data Source=%s;Pooling=False" (Path.Combine(root, "trace.db")))
                :> Data.Common.DbConnection)

        try
            action root factory
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.CompleteAuthorizationIdentityPartitionsEveryOperation() =
        withStorage (fun root factory ->
            let inMemory = InMemoryTraceStore.create ()

            for make in
                [ (fun () -> inMemory)
                  (fun () -> TraceStores.file root)
                  (fun () -> TraceStores.ado factory) ] do
                let saved =
                    owners
                    |> List.mapi (fun index authorization ->
                        let value = trace "shared-agent" (string index)
                        (make ()).SaveAsync authorization value |> _.GetAwaiter().GetResult()
                        authorization, value)

                for index, (authorization, expected) in List.indexed saved do
                    let current = make ()
                    Assert.AreEqual([ expected ], current.GetTracesAsync authorization "shared-agent" 10 |> _.Result)

                    Assert.AreEqual(
                        [ expected ],
                        current.GetByExecutionAsync authorization expected.Correlation.ExecutionId
                        |> _.Result
                    )

                    Assert.AreEqual(
                        Some expected,
                        current.GetBaselineAsync authorization "shared-agent" expected.Input |> _.Result
                    )

                    Assert.AreEqual(Ok 1, current.DeleteScopeAsync authorization |> _.Result)
                    Assert.AreEqual(Ok 0, (make ()).DeleteScopeAsync authorization |> _.Result)

                    for pending, _ in List.skip (index + 1) saved do
                        Assert.AreEqual(1, ((make ()).GetTracesAsync pending "shared-agent" 10).Result.Length))

    [<TestMethod>]
    member _.PersistenceUsesClaimFreeKeysAndOwnerFreeV1Payloads() =
        withStorage (fun root factory ->
            let principal =
                SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") [ GroupId.parse "group" ]

            let sameOwner =
                AuthorizationScope.tryCreate
                    principal
                    (Some(GroupId.parse "group"))
                    (WorkspaceId.parse "workspace")
                    (Some(SessionId.parse "session"))
                |> Option.get

            for make in [ (fun () -> TraceStores.file root); (fun () -> TraceStores.ado factory) ] do
                let store = make ()
                store.SaveAsync owner (trace "agent" "input") |> _.GetAwaiter().GetResult()
                Assert.AreEqual(1, ((make ()).GetTracesAsync sameOwner "agent" 10).Result.Length)

            let path = Path.Combine(root, "trace-store", key owner + ".jsonl")
            let payloads = File.ReadAllLines path |> Array.toList

            let rows =
                Ado.query factory "SELECT stream, payload FROM nao_events" [] (fun row ->
                    Ado.getString row "stream", Ado.getString row "payload")
                |> _.Result

            Assert.AreEqual([ "trace-store/" + key owner ], rows |> List.map fst)

            for payload in payloads @ (rows |> List.map snd) do
                let document = FSharpJson.deserialize<TraceStoreDocument> payload
                Assert.AreEqual(1, document.SchemaVersion)

                for secret in
                    [ "tenant"
                      "user"
                      "group"
                      "workspace"
                      "session"
                      "private-service"
                      "private-role"
                      "private-claim"
                      "private-value"
                      "MultiFactor"
                      "Authorization"
                      "Owner" ] do
                    Assert.IsFalse(payload.Contains secret))

    [<TestMethod>]
    member _.CorruptForeignScopeDoesNotBlockOwnOperations() =
        withStorage (fun root factory ->
            let foreign = owners.[1]

            for make, corrupt in
                [ (fun () -> TraceStores.file root),
                  (fun () ->
                      File.AppendAllText(Path.Combine(root, "trace-store", key foreign + ".jsonl"), "{invalid\n"))
                  (fun () -> TraceStores.ado factory),
                  (fun () ->
                      Ado.executeNonQuery
                          factory
                          "UPDATE nao_events SET payload = 'invalid' WHERE stream = @stream"
                          [ "@stream", box ("trace-store/" + key foreign) ]
                      |> _.GetAwaiter().GetResult()
                      |> ignore) ] do
                let store = make ()
                store.SaveAsync foreign (trace "agent" "foreign") |> _.GetAwaiter().GetResult()
                corrupt ()
                store.SaveAsync owner (trace "agent" "own") |> _.GetAwaiter().GetResult()
                Assert.AreEqual(1, ((make ()).GetTracesAsync owner "agent" 10).Result.Length)

                Assert.ThrowsExactlyAsync<InvalidDataException>(fun () ->
                    store.GetTracesAsync foreign "agent" 10 :> Task)
                |> _.GetAwaiter().GetResult()
                |> ignore)

    [<TestMethod>]
    member _.FailedAppendAndUnsupportedVersionCannotPublishState() =
        let lines = ResizeArray<string>()
        let mutable failAppend = false

        let store =
            { Append =
                fun line ->
                    if failAppend then
                        raise (IOException "write failed")
                    else
                        lines.Add line
              LoadAll = fun () -> List.ofSeq lines }

        let make () =
            PersistentTraceStore.create (fun _ -> store)

        let traces = make ()
        let original = trace "agent" "original"
        traces.SaveAsync owner original |> _.GetAwaiter().GetResult()
        failAppend <- true

        Assert.ThrowsExactlyAsync<IOException>(fun () -> traces.SaveAsync owner (trace "agent" "new") :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        Assert.AreEqual([ original ], traces.GetTracesAsync owner "agent" 10 |> _.Result)
        Assert.IsTrue(traces.DeleteScopeAsync owner |> _.Result |> Result.isError)

        failAppend <- false

        let unsupported: TraceStoreDocument =
            { SchemaVersion = 99
              Event = TraceStoreEvent.DeleteScope }

        let unsupportedStore =
            { Append = fun _ -> Assert.Fail("Unsupported streams must reject before append.")
              LoadAll = fun () -> [ FSharpJson.serialize unsupported ] }

        let unsupportedTraces = PersistentTraceStore.create (fun _ -> unsupportedStore)

        Assert.ThrowsExactlyAsync<InvalidDataException>(fun () ->
            unsupportedTraces.GetTracesAsync owner "agent" 10 :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        Assert.AreEqual(1, lines.Count)
