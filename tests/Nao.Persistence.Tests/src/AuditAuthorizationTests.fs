namespace Nao.Persistence.Tests

open System
open System.Collections.Generic
open System.IO
open Microsoft.Data.Sqlite
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type AuditAuthorizationTests() =
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

    let entry id actor permitted executionId =
        { Id = id
          Timestamp = timestamp
          AgentId = actor
          Action = AuditAction.ToolInvocation "tool"
          Input = Some "input"
          Output = Some "output"
          Permitted = permitted
          Decision =
            if permitted then
                PermissionDecision.Allow
            else
                PermissionDecision.Deny
          PermissionEvidence = None
          PolicyEvidence = None
          ConstitutionViolations = []
          ExecutionId = Some executionId
          Metadata = Map.empty }

    let withStorage action =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-audit-scope-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        let factory =
            DbConnectionFactory.ofFunc (fun () ->
                new SqliteConnection(sprintf "Data Source=%s;Pooling=False" (Path.Combine(root, "audit.db")))
                :> Data.Common.DbConnection)

        try
            action root factory
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.PermissionDecisionEvidenceSurvivesReload() =
        withStorage (fun root _ ->
            let evidence =
                { Id = Guid.NewGuid()
                  Access = ResourceAccess.memory "durable" MemoryAction.Read
                  Decision = PermissionDecision.Allow
                  Basis = PermissionDecisionBasis.ReusableGrant
                  PolicyVersions = Map.ofList [ "nao.permission", "1"; "tenant.policy", "42" ]
                  Grants = [ { Id = Guid.NewGuid(); Version = 3 } ]
                  EvaluatedAt = timestamp }

            let executionId = ExecutionId.generate ()

            AuditLogs.file root
            |> fun audit ->
                audit.RecordAsync owner (AuditLog.permissionDecision "memory-agent" evidence (Some executionId))
            |> _.Wait()

            let reloaded =
                AuditLogs.file root
                |> fun audit -> audit.QueryByExecutionAsync owner executionId
                |> _.Result
                |> List.exactlyOne

            Assert.AreEqual(Some evidence, reloaded.PermissionEvidence)
            Assert.AreEqual(PermissionDecision.Allow, reloaded.Decision))

    [<TestMethod>]
    member _.PolicyEvaluationEvidenceSurvivesReloadWithoutScopeOrInputDisclosure() =
        withStorage (fun root _ ->
            let evidence =
                { Id = Guid.NewGuid()
                  PolicyId = "data-boundary"
                  PolicyVersion = "2026-09-13"
                  Scope = PolicyScopeKind.Workspace
                  Stage = PolicyStage.PreAction
                  AgentId = "data-agent"
                  ToolName = Some "search"
                  Action = "invoke"
                  Outcome = PolicyEvaluationOutcome.Modified
                  FailureCode = None
                  MessageDigest = Some(String.replicate 64 "c")
                  InputDigest = Some(String.replicate 64 "a")
                  EffectiveInputDigest = Some(String.replicate 64 "b")
                  Confirmation = None
                  EvaluatedAt = timestamp }

            let executionId = ExecutionId.generate ()

            AuditLogs.file root
            |> fun audit -> audit.RecordAsync owner (AuditLog.policyEvaluation evidence (Some executionId))
            |> _.Wait()

            let reloaded =
                AuditLogs.file root
                |> fun audit -> audit.QueryByExecutionAsync owner executionId
                |> _.Result
                |> List.exactlyOne

            Assert.AreEqual(Some evidence, reloaded.PolicyEvidence)
            Assert.AreEqual(PermissionDecision.Allow, reloaded.Decision)
            Assert.AreEqual(None, reloaded.Input)
            Assert.AreEqual(None, reloaded.Output)

            let payload = File.ReadAllText(Path.Combine(root, "audit", key owner + ".jsonl"))

            for secret in [ "tenant"; "user"; "group"; "workspace"; "session"; "raw-sensitive-input" ] do
                Assert.IsFalse(payload.Contains(secret), $"Policy evidence disclosed '{secret}'."))

    [<TestMethod>]
    member _.CompleteAuthorizationIdentityPartitionsEveryOperation() =
        withStorage (fun root factory ->
            let inMemory = InMemoryAuditLog.create ()

            for make in
                [ (fun () -> inMemory)
                  (fun () -> AuditLogs.file root)
                  (fun () -> AuditLogs.ado factory) ] do
                let id = Guid.NewGuid()
                let executionId = ExecutionId.generate ()

                for index, authorization in List.indexed owners do
                    make ()
                    |> fun audit ->
                        audit.RecordAsync authorization (entry id (string index) (index % 2 = 0) executionId)
                    |> _.GetAwaiter().GetResult()

                for index, authorization in List.indexed owners do
                    let current = make ()

                    Assert.AreEqual(
                        [ string index ],
                        current.QueryAsync authorization (string index) DateTimeOffset.MinValue
                        |> _.Result
                        |> List.map _.AgentId
                    )

                    Assert.AreEqual(
                        1,
                        current.QueryByExecutionAsync authorization executionId
                        |> _.Result
                        |> List.length
                    )

                    Assert.AreEqual(
                        (if index % 2 = 0 then 0 else 1),
                        current.GetDeniedCountAsync authorization (string index) DateTimeOffset.MinValue
                        |> _.Result
                    )

                    Assert.AreEqual(Ok 1, current.DeleteScopeAsync authorization |> _.Result)
                    Assert.AreEqual(Ok 0, (make ()).DeleteScopeAsync authorization |> _.Result)

                    for pending in List.skip (index + 1) owners do
                        Assert.AreEqual(
                            1,
                            (make ()).QueryByExecutionAsync pending executionId |> _.Result |> List.length
                        ))

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

            let executionId = ExecutionId.generate ()

            for make in [ (fun () -> AuditLogs.file root); (fun () -> AuditLogs.ado factory) ] do
                let audit = make ()

                audit.RecordAsync owner (entry (Guid.NewGuid()) "actor" true executionId)
                |> _.GetAwaiter().GetResult()

                Assert.AreEqual(1, (make ()).QueryByExecutionAsync sameOwner executionId |> _.Result |> List.length)

            let path = Path.Combine(root, "audit", key owner + ".jsonl")
            let payloads = File.ReadAllLines path |> Array.toList

            let rows =
                Ado.query factory "SELECT stream, payload FROM nao_events" [] (fun row ->
                    Ado.getString row "stream", Ado.getString row "payload")
                |> _.Result

            Assert.AreEqual([ "audit/" + key owner ], rows |> List.map fst)

            for payload in payloads @ (rows |> List.map snd) do
                let document = FSharpJson.deserialize<AuditDocument> payload
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
            let executionId = ExecutionId.generate ()

            for make, corrupt in
                [ (fun () -> AuditLogs.file root),
                  (fun () -> File.AppendAllText(Path.Combine(root, "audit", key foreign + ".jsonl"), "{invalid\n"))
                  (fun () -> AuditLogs.ado factory),
                  (fun () ->
                      Ado.executeNonQuery
                          factory
                          "UPDATE nao_events SET payload = 'invalid' WHERE stream = @stream"
                          [ "@stream", box ("audit/" + key foreign) ]
                      |> _.GetAwaiter().GetResult()
                      |> ignore) ] do
                let audit = make ()

                audit.RecordAsync foreign (entry (Guid.NewGuid()) "foreign" true executionId)
                |> _.GetAwaiter().GetResult()

                corrupt ()

                audit.RecordAsync owner (entry (Guid.NewGuid()) "own" true executionId)
                |> _.GetAwaiter().GetResult()

                Assert.AreEqual(
                    1,
                    (make ()).QueryAsync owner "own" DateTimeOffset.MinValue
                    |> _.Result
                    |> List.length
                )

                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    audit.QueryAsync foreign "foreign" DateTimeOffset.MinValue
                    |> _.GetAwaiter().GetResult()
                    |> ignore)
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

        let audit = PersistentAuditLog.create (fun _ -> store)
        let executionId = ExecutionId.generate ()
        let original = entry (Guid.NewGuid()) "original" true executionId
        audit.RecordAsync owner original |> _.GetAwaiter().GetResult()
        failAppend <- true

        Assert.ThrowsExactly<IOException>(fun () ->
            audit.RecordAsync owner (entry (Guid.NewGuid()) "new" true executionId)
            |> _.GetAwaiter().GetResult())
        |> ignore

        Assert.AreEqual([ original ], audit.QueryByExecutionAsync owner executionId |> _.Result)

        Assert.IsTrue(audit.DeleteScopeAsync owner |> _.Result |> Result.isError)
        failAppend <- false

        let unsupported: AuditDocument =
            { Version = 99
              Event = AuditEvent.DeleteScope }

        let unsupportedStore =
            { Append = fun _ -> Assert.Fail("Unsupported streams must reject before append.")
              LoadAll = fun () -> [ FSharpJson.serialize unsupported ] }

        let unsupportedAudit = PersistentAuditLog.create (fun _ -> unsupportedStore)

        Assert.ThrowsExactly<InvalidDataException>(fun () ->
            unsupportedAudit.QueryAsync owner "actor" DateTimeOffset.MinValue
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        Assert.AreEqual(1, lines.Count)
