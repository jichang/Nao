namespace Nao.DeveloperTools.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.DeveloperTools
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

type RecordingAdministrationTransport() =
    let mutable lastPrincipal = None
    let mutable lastRequest = None

    member _.LastPrincipal = lastPrincipal
    member _.LastRequest = lastRequest

    interface IRuntimeAdministrationTransport with
        member _.PreviewAsync(principal, _, request, _) =
            lastPrincipal <- Some principal
            lastRequest <- Some request

            let preview =
                AdministrativeImpactPreview(
                    Summary = "Migration preview accepted.",
                    CurrentRevision = request.ExpectedRevision,
                    TargetRevision = request.ExpectedRevision + 1L
                )

            preview.AffectedResourceIds.Add request.ResourceId
            Task.FromResult preview

        member _.MutateAsync(_, _, request, _) =
            AdministrativeMutationResult(OperationId = request.OperationId, Revision = request.ExpectedRevision + 1L)
            |> Task.FromResult

        member _.GetResourceAsync(principal, _, resourceKind, resourceId, _) =
            lastPrincipal <- Some principal

            let snapshot =
                AdministrativeResourceSnapshot(
                    ResourceKind = resourceKind,
                    ResourceId = resourceId,
                    Revision = 7L,
                    UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
                )

            snapshot.Metadata.Add("status", "completed")
            snapshot.Metadata.Add("searchableRecords", "42")
            Task.FromResult snapshot

[<TestClass>]
type DeveloperToolsTests() =
    let temporaryDirectory () =
        let path =
            Path.Combine(Path.GetTempPath(), $"nao-developer-tools-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let write (root: string) (name: string) (content: string) =
        let path = Path.Combine(root, name)
        File.WriteAllText(path, content)
        path

    let run services arguments =
        DeveloperCommandLine.runAsync services arguments CancellationToken.None

    [<TestMethod>]
    member _.ConfigurationValidationExplainsUnsafeSettings() =
        let root = temporaryDirectory ()

        try
            let path =
                write
                    root
                    "platform.json"
                    """{
  "schemaVersion": 1,
  "profile": "single-node-production",
  "instanceId": "test",
  "identityMode": "development-permissive",
  "administrationMode": "anonymous",
  "toolPolicy": "unrestricted",
  "workerIsolation": "in-process",
  "durability": "ephemeral",
  "clusterMembership": "standalone",
  "secretReferences": []
}"""

            let result = ConfigurationDiagnostics.validateFile path
            Assert.IsFalse result.Succeeded
            Assert.IsTrue(result.Issues |> List.exists (fun issue -> issue.Code = "permissive_identity"))

            Assert.IsTrue(
                result.Issues
                |> List.exists (fun issue -> issue.Code = "anonymous_administration")
            )
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.DatasetCommandsLintAndEvaluateDeterministically() =
        let root = temporaryDirectory ()

        try
            let path =
                write
                    root
                    "dataset.json"
                    """{
  "schemaVersion": 1,
  "cases": [
    { "id": "pass", "input": "one", "expected": "1", "actual": "1" },
    { "id": "fail", "input": "two", "expected": "2", "actual": "3" }
  ]
}"""

            let linted = DatasetDiagnostics.inspect false path
            Assert.IsTrue linted.Succeeded

            let evaluated = DatasetDiagnostics.inspect true path
            Assert.IsFalse evaluated.Succeeded
            Assert.AreEqual("1", evaluated.Data["passedCount"])
            Assert.AreEqual("1", evaluated.Data["failedCount"])
            Assert.AreEqual("exact_match_failed", evaluated.Issues.Head.Code)
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.AdapterDiagnosticsExplainCompatibilityAndDependencyFailures() =
        let root = temporaryDirectory ()

        try
            let path =
                write
                    root
                    "adapter.json"
                    $"""{{
  "schemaVersion": 1,
  "sdkVersion": "2.0.0",
  "protocolVersion": 1,
  "minimumHostVersion": "1.0.0",
  "maximumHostVersion": "1.0.0",
  "targetFramework": "net10.0",
  "dependencies": {{ "runtime": "{String('a', 64)}" }},
  "approvedDependencies": {{ "runtime": "{String('b', 64)}" }}
}}"""

            let result = AdapterDiagnostics.inspect path
            Assert.IsFalse result.Succeeded

            Assert.IsTrue(
                result.Issues
                |> List.exists (fun issue -> issue.Code = "compatibility_mismatch")
            )

            Assert.IsTrue(result.Issues |> List.exists (fun issue -> issue.Code = "dependency_rejected"))
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.TraceAndReplayInspectionReturnStructuredEvidence() =
        let root = temporaryDirectory ()

        try
            let tracePath =
                write
                    root
                    "traces.json"
                    """{
  "schemaVersion": 1,
  "traces": [
    { "id": "trace-1", "success": true, "durationMs": 12 },
    { "id": "trace-2", "success": false, "durationMs": 8 }
  ]
}"""

            let replayPath =
                write
                    root
                    "replay.json"
                    """{
  "schemaVersion": 1,
  "fixtures": [
    { "id": "provider-1", "replayable": true },
    { "id": "tool-1", "replayable": false }
  ]
}"""

            let traces = TraceReplayDiagnostics.inspect false tracePath
            Assert.IsTrue traces.Succeeded
            Assert.AreEqual("20", traces.Data["totalDurationMs"])

            let replay = TraceReplayDiagnostics.inspect true replayPath
            Assert.IsTrue replay.Succeeded
            Assert.AreEqual("non_replayable_fixture", replay.Issues.Head.Code)
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.EveryExtensionKindProducesACompleteScaffold() =
        for kind in
            [ ExtensionTemplate.Agent
              ExtensionTemplate.Tool
              ExtensionTemplate.Provider
              ExtensionTemplate.Evaluator
              ExtensionTemplate.Adapter ] do
            let root = temporaryDirectory ()

            try
                let _, files = ExtensionTemplate.create root kind $"Example{kind}" None
                Assert.AreEqual(4, files.Length)
                files |> List.iter (fun path -> Assert.IsTrue(File.Exists path, path))
            finally
                Directory.Delete(root, true)

    [<TestMethod>]
    member _.ScaffoldedAdapterCanBeTestedAndPackaged() : Task =
        task {
            let root = temporaryDirectory ()

            try
                let contractProject =
                    Path.GetFullPath(
                        Path.Combine(
                            __SOURCE_DIRECTORY__,
                            "..",
                            "..",
                            "..",
                            "src",
                            "Nao.Extensions",
                            "Nao.Extensions.fsproj"
                        )
                    )

                let _, _ =
                    ExtensionTemplate.create root ExtensionTemplate.Adapter "ExampleAdapter" (Some contractProject)

                let testProject =
                    Path.Combine(root, "tests", "ExampleAdapter.Tests", "ExampleAdapter.Tests.fsproj")

                let sourceProject =
                    Path.Combine(root, "src", "ExampleAdapter", "ExampleAdapter.fsproj")

                let! tested = ExtensionWorkflow.runAsync "test" testProject "" CancellationToken.None

                Assert.AreEqual(0, tested.ExitCode, tested.StandardError + tested.StandardOutput)

                let packages = Path.Combine(root, "packages")

                let! packed = ExtensionWorkflow.runAsync "package" sourceProject packages CancellationToken.None

                Assert.AreEqual(0, packed.ExitCode, packed.StandardError + packed.StandardOutput)

                Assert.AreEqual(1, Directory.GetFiles(packages, "ExampleAdapter.1.0.0.nupkg").Length)
            finally
                Directory.Delete(root, true)
        }

    [<TestMethod>]
    member _.LiveDiagnosticsRequireTheAuthenticatedAdministrationClient() : Task =
        task {
            let! missing = run DeveloperCommandServices.localOnly [| "knowledge"; "status"; "source-1" |]

            Assert.IsFalse missing.Succeeded
            Assert.AreEqual("authenticated_control_plane_required", missing.Issues.Head.Code)

            let tenant = TenantId.parse "tenant"

            let principal =
                SecurityPrincipal.createAuthenticated
                    tenant
                    (UserId.parse "operator")
                    None
                    []
                    [ RuntimeAdministrationAuthorization.resourceRole AdministrativeResourceKind.Knowledge ]
                    Map.empty
                    AuthenticationStrength.MultiFactor

            let transport = RecordingAdministrationTransport()
            let client = RuntimeAdministrationClient(principal, tenant, transport)

            let! result = run { Administration = Some client } [| "knowledge"; "status"; "source-1" |]

            Assert.IsTrue result.Succeeded
            Assert.AreEqual("7", result.Data["revision"])
            Assert.AreEqual("completed", result.Data["status"])
            Assert.AreEqual("42", result.Data["searchableRecords"])
            Assert.AreEqual("ingestion:source-1", result.Data["resourceId"])
            Assert.AreSame(principal, transport.LastPrincipal.Value)
        }

    [<TestMethod>]
    member _.MigrationPlanningUsesPlatformPlanAndAuthenticatedPreview() : Task =
        task {
            let root = temporaryDirectory ()

            try
                let path =
                    write
                        root
                        "migration.json"
                        """{
  "schemaVersion": 1,
  "id": "knowledge-v1",
  "sourceRevision": "source-v1",
  "targetRevision": "target-v1",
  "verifiedBackupId": "backup-1",
  "verifiedAt": "2026-01-01T00:00:00Z",
  "activeWriters": 0,
  "availableBytes": 2048,
  "requiredBytes": 1024,
  "steps": [ "copy", "verify" ]
}"""

                let! plan = run DeveloperCommandServices.localOnly [| "migration"; "plan"; path |]

                Assert.IsTrue plan.Succeeded
                Assert.AreEqual("2", plan.Data["stepCount"])
                Assert.AreEqual(64, plan.Data["planDigest"].Length)

                let tenant = TenantId.parse "tenant"

                let principal =
                    SecurityPrincipal.createAuthenticated
                        tenant
                        (UserId.parse "operator")
                        None
                        []
                        [ RuntimeAdministrationAuthorization.resourceRole AdministrativeResourceKind.Operation ]
                        Map.empty
                        AuthenticationStrength.MultiFactor

                let transport = RecordingAdministrationTransport()
                let client = RuntimeAdministrationClient(principal, tenant, transport)

                let! preview = run { Administration = Some client } [| "migration"; "dry-run"; path |]

                Assert.IsTrue preview.Succeeded
                Assert.AreEqual(plan.Data["planDigest"], preview.Data["planDigest"])
                Assert.AreSame(principal, transport.LastPrincipal.Value)
                Assert.AreEqual("migrate", transport.LastRequest.Value.Action)
            finally
                Directory.Delete(root, true)
        }

    [<TestMethod>]
    member _.ResultsSerializeAsVersionedMachineReadableJson() =
        let result = DeveloperCommandResult.success "test.command" (Map [ "value", "ok" ])

        let json = DeveloperCommandResult.serialize result
        StringAssert.Contains(json, "\"schemaVersion\": 1")
        StringAssert.Contains(json, "\"succeeded\": true")
        StringAssert.Contains(json, "\"value\": \"ok\"")
