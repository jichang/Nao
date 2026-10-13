namespace Nao.Agents.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type ArtifactTests() =
    let recordedAt = DateTimeOffset(2026, 10, 13, 3, 45, 0, TimeSpan.Zero)

    [<TestMethod>]
    member _.``Produced artifact has canonical address integrity and execution lineage``() =
        let executionId = ExecutionId.generate ()

        let artifact =
            Artifact.produced
                executionId
                recordedAt
                []
                (IdempotencyKey.forExecution executionId)
                "report"
                "application/json"
                """{"answer":42}"""

        Assert.AreEqual(ArtifactSchemaVersion.current, Artifact.schemaVersion artifact)
        Assert.AreEqual(ArtifactRole.Produced, Artifact.role artifact)

        Assert.AreEqual(
            $"nao://artifacts/{artifact |> Artifact.id |> ArtifactId.serialize}",
            artifact |> Artifact.address |> ArtifactAddress.value
        )

        Assert.AreEqual(artifact |> Artifact.payload |> ArtifactContentHash.compute, Artifact.contentHash artifact)

        let lineage = Artifact.lineage artifact
        Assert.AreEqual(executionId, ArtifactLineage.recordedBy lineage)
        Assert.AreEqual(recordedAt, ArtifactLineage.recordedAt lineage)
        Assert.AreEqual([], ArtifactLineage.parentArtifactIds lineage)

    [<TestMethod>]
    member _.``Consumed artifact retains external address and parent lineage``() =
        let producer = ExecutionId.generate ()

        let parent =
            Artifact.produced
                producer
                recordedAt
                []
                (IdempotencyKey.forExecution producer)
                "source"
                "text/plain"
                "source evidence"

        let consumer = ExecutionId.generate ()
        let parentId = Artifact.id parent

        let artifact =
            Artifact.consumed
                consumer
                recordedAt
                [ parentId ]
                (IdempotencyKey.forExecution consumer)
                (ArtifactAddress.parse "https://evidence.example/reports/42")
                "evidence"
                "text/plain"
                "source evidence"

        Assert.AreEqual(ArtifactRole.Consumed, Artifact.role artifact)
        Assert.AreEqual("https://evidence.example/reports/42", artifact |> Artifact.address |> ArtifactAddress.value)

        let lineage = Artifact.lineage artifact
        Assert.AreEqual(consumer, ArtifactLineage.recordedBy lineage)
        Assert.AreEqual([ parentId ], ArtifactLineage.parentArtifactIds lineage)

    [<TestMethod>]
    member _.``Restore rejects payload tampering and self lineage``() =
        let artifactId = ArtifactId.generate ()
        let executionId = ExecutionId.generate ()
        let payload = "committed"

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            Artifact.restore
                ArtifactSchemaVersion.current
                artifactId
                (IdempotencyKey.forExecution executionId)
                ArtifactRole.Produced
                (ArtifactAddress.forProduced artifactId)
                "report"
                "text/plain"
                "tampered"
                (ArtifactContentHash.compute payload)
                executionId
                recordedAt
                []
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            Artifact.restore
                ArtifactSchemaVersion.current
                artifactId
                (IdempotencyKey.forExecution executionId)
                ArtifactRole.Produced
                (ArtifactAddress.forProduced artifactId)
                "report"
                "text/plain"
                payload
                (ArtifactContentHash.compute payload)
                executionId
                recordedAt
                [ artifactId ]
            |> ignore)
        |> ignore
