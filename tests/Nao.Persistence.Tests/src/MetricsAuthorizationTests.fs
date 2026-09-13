namespace Nao.Persistence.Tests

open System
open System.Collections.Generic
open System.IO
open Microsoft.Data.Sqlite
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type MetricsAuthorizationTests() =
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

    let withStorage action =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-metrics-scope-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        let factory =
            DbConnectionFactory.ofFunc (fun () ->
                new SqliteConnection(sprintf "Data Source=%s;Pooling=False" (Path.Combine(root, "metrics.db")))
                :> Data.Common.DbConnection)

        try
            action root factory
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.CompleteAuthorizationIdentityPartitionsEveryOperation() =
        withStorage (fun root factory ->
            let inMemory = InMemoryMetricsCollector.create ()

            for make in
                [ (fun () -> inMemory)
                  (fun () -> MetricsCollectors.file root)
                  (fun () -> MetricsCollectors.ado factory) ] do
                let correlation = CorrelationContext.root ()

                for index, authorization in List.indexed owners do
                    (make ()).Record authorization (MetricRecord.toolCall correlation timestamp (string index) 1L true)

                for index, authorization in List.indexed owners do
                    let current = make ()
                    Assert.AreEqual(1, (current.GetMetrics authorization).TotalToolCalls)

                    Assert.AreEqual(
                        [ string index ],
                        current.GetByExecution authorization correlation.ExecutionId
                        |> List.choose (fun record ->
                            match record.Payload with
                            | MetricPayload.ToolCall(name, _, _) -> Some name
                            | _ -> None)
                    )

                    Assert.AreEqual(Ok 1, current.DeleteScopeAsync authorization |> _.Result)
                    Assert.AreEqual(Ok 0, (make ()).DeleteScopeAsync authorization |> _.Result)

                    for pending in List.skip (index + 1) owners do
                        Assert.AreEqual(1, ((make ()).GetMetrics pending).TotalToolCalls))

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

            let correlation = CorrelationContext.root ()

            for make in
                [ (fun () -> MetricsCollectors.file root)
                  (fun () -> MetricsCollectors.ado factory) ] do
                let metrics = make ()
                metrics.Record owner (MetricRecord.llmCall correlation timestamp 10 5 20L)
                Assert.AreEqual(1, ((make ()).GetMetrics sameOwner).TotalLlmCalls)

            let path = Path.Combine(root, "metrics", key owner + ".jsonl")
            let payloads = File.ReadAllLines path |> Array.toList

            let rows =
                Ado.query factory "SELECT stream, payload FROM nao_events" [] (fun row ->
                    Ado.getString row "stream", Ado.getString row "payload")
                |> _.Result

            Assert.AreEqual([ "metrics/" + key owner ], rows |> List.map fst)

            for payload in payloads @ (rows |> List.map snd) do
                let document = FSharpJson.deserialize<MetricsDocument> payload
                Assert.AreEqual(1, document.Version)

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
            let correlation = CorrelationContext.root ()

            for make, corrupt in
                [ (fun () -> MetricsCollectors.file root),
                  (fun () -> File.AppendAllText(Path.Combine(root, "metrics", key foreign + ".jsonl"), "{invalid\n"))
                  (fun () -> MetricsCollectors.ado factory),
                  (fun () ->
                      Ado.executeNonQuery
                          factory
                          "UPDATE nao_events SET payload = 'invalid' WHERE stream = @stream"
                          [ "@stream", box ("metrics/" + key foreign) ]
                      |> _.GetAwaiter().GetResult()
                      |> ignore) ] do
                let metrics = make ()
                metrics.Record foreign (MetricRecord.llmCall correlation timestamp 1 1 1L)
                corrupt ()
                metrics.Record owner (MetricRecord.llmCall correlation timestamp 2 2 2L)
                Assert.AreEqual(1, ((make ()).GetMetrics owner).TotalLlmCalls)

                Assert.ThrowsExactly<InvalidDataException>(fun () -> metrics.GetMetrics foreign |> ignore)
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
            PersistentMetricsCollector.create (fun _ -> store)

        let correlation = CorrelationContext.root ()
        let metrics = make ()
        metrics.Record owner (MetricRecord.llmCall correlation timestamp 1 1 1L)
        failAppend <- true

        Assert.ThrowsExactly<IOException>(fun () ->
            metrics.Record owner (MetricRecord.llmCall correlation timestamp 2 2 2L))
        |> ignore

        Assert.AreEqual(1, (metrics.GetMetrics owner).TotalLlmCalls)
        Assert.IsTrue(metrics.DeleteScopeAsync owner |> _.Result |> Result.isError)

        failAppend <- false

        let unsupported: MetricsDocument =
            { Version = 99
              Event = MetricsEvent.DeleteScope }

        let unsupportedStore =
            { Append = fun _ -> Assert.Fail("Unsupported streams must reject before append.")
              LoadAll = fun () -> [ FSharpJson.serialize unsupported ] }

        let unsupportedMetrics =
            PersistentMetricsCollector.create (fun _ -> unsupportedStore)

        Assert.ThrowsExactly<InvalidDataException>(fun () -> unsupportedMetrics.GetMetrics owner |> ignore)
        |> ignore

        Assert.AreEqual(1, lines.Count)
