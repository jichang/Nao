namespace Nao.Persistence.Tests

open System
open System.Collections.Generic
open System.IO
open Microsoft.Data.Sqlite
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type TracerAuthorizationTests() =
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

    let withStorage action =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-tracer-scope-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        let factory =
            DbConnectionFactory.ofFunc (fun () ->
                new SqliteConnection(sprintf "Data Source=%s;Pooling=False" (Path.Combine(root, "tracer.db")))
                :> Data.Common.DbConnection)

        try
            action root factory
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.CompleteAuthorizationIdentityPartitionsEveryOperation() =
        withStorage (fun root factory ->
            let inMemory = InMemoryTracer.create ()

            for make in
                [ (fun () -> inMemory)
                  (fun () -> Tracers.file root)
                  (fun () -> Tracers.ado factory) ] do
                let correlation = CorrelationContext.root ()

                let roots =
                    owners
                    |> List.mapi (fun index authorization ->
                        let tracer = make ()
                        let root = tracer.StartTrace authorization correlation (string index)
                        let child = tracer.StartSpan authorization root "child"
                        tracer.EndSpan authorization child SpanStatus.Ok
                        authorization, root)

                for index, (authorization, root) in List.indexed roots do
                    let current = make ()
                    Assert.AreEqual(2, current.GetTrace authorization root.TraceId |> List.length)

                    Assert.AreEqual(
                        [ string index; "child" ] |> List.sort,
                        current.GetByExecution authorization correlation.ExecutionId
                        |> List.map _.OperationName
                        |> List.sort
                    ))

    [<TestMethod>]
    member _.CrossScopeParentsAndMutationsAreRejected() =
        let foreign = owners.[1]

        withStorage (fun root _ ->
            for tracer in [ InMemoryTracer.create (); Tracers.file root ] do
                let root = tracer.StartTrace owner (CorrelationContext.root ()) "root"

                Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                    tracer.StartSpan foreign root "child" |> ignore)
                |> ignore

                Assert.ThrowsExactly<UnauthorizedAccessException>(fun () -> tracer.EndSpan foreign root SpanStatus.Ok)
                |> ignore

                Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                    tracer.AddEvent foreign root "event" Map.empty)
                |> ignore

                Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                    tracer.SetAttributes foreign root Map.empty)
                |> ignore)

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

            for make in [ (fun () -> Tracers.file root); (fun () -> Tracers.ado factory) ] do
                let tracer = make ()
                let span = tracer.StartTrace owner (CorrelationContext.root ()) "operation"
                Assert.AreEqual(1, (make ()).GetTrace sameOwner span.TraceId |> List.length)

            let path = Path.Combine(root, "tracer", key owner + ".jsonl")
            let payloads = File.ReadAllLines path |> Array.toList

            let rows =
                Ado.query factory "SELECT stream, payload FROM nao_events" [] (fun row ->
                    Ado.getString row "stream", Ado.getString row "payload")
                |> _.Result

            Assert.AreEqual([ "tracer/" + key owner ], rows |> List.map fst)

            for payload in payloads @ (rows |> List.map snd) do
                let document = FSharpJson.deserialize<TracerDocument> payload
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
                [ (fun () -> Tracers.file root),
                  (fun () -> File.AppendAllText(Path.Combine(root, "tracer", key foreign + ".jsonl"), "{invalid\n"))
                  (fun () -> Tracers.ado factory),
                  (fun () ->
                      Ado.executeNonQuery
                          factory
                          "UPDATE nao_events SET payload = 'invalid' WHERE stream = @stream"
                          [ "@stream", box ("tracer/" + key foreign) ]
                      |> _.GetAwaiter().GetResult()
                      |> ignore) ] do
                let tracer = make ()
                let foreignSpan = tracer.StartTrace foreign (CorrelationContext.root ()) "foreign"
                corrupt ()
                let ownSpan = tracer.StartTrace owner (CorrelationContext.root ()) "own"
                Assert.AreEqual(1, (make ()).GetTrace owner ownSpan.TraceId |> List.length)

                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    tracer.GetTrace foreign foreignSpan.TraceId |> ignore)
                |> ignore)

    [<TestMethod>]
    member _.FailedAppendUnsupportedVersionAndStaleSnapshotsAreSafe() =
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

        let tracer = PersistentTracer.create (fun _ -> store)
        let root = tracer.StartTrace owner (CorrelationContext.root ()) "root"
        tracer.SetAttributes owner root (Map.ofList [ "attribute", "retained" ])
        tracer.AddEvent owner root "retained" Map.empty
        tracer.EndSpan owner root SpanStatus.Ok

        let retained = tracer.GetTrace owner root.TraceId |> List.exactlyOne
        Assert.AreEqual(Some "retained", retained.Attributes |> Map.tryFind "attribute")
        Assert.AreEqual([ "retained" ], retained.Events |> List.map _.Name)

        failAppend <- true

        Assert.ThrowsExactly<IOException>(fun () -> tracer.StartSpan owner root "not-persisted" |> ignore)
        |> ignore

        Assert.AreEqual(1, tracer.GetTrace owner root.TraceId |> List.length)
        failAppend <- false

        let unsupported: TracerDocument = { SchemaVersion = 99; Value = root }

        let unsupportedStore =
            { Append = fun _ -> Assert.Fail("Unsupported streams must reject before append.")
              LoadAll = fun () -> [ FSharpJson.serialize unsupported ] }

        let unsupportedTracer = PersistentTracer.create (fun _ -> unsupportedStore)

        Assert.ThrowsExactly<InvalidDataException>(fun () -> unsupportedTracer.GetTrace owner root.TraceId |> ignore)
        |> ignore

        Assert.AreEqual(4, lines.Count)
