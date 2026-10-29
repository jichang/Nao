namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Knowledge

[<TestClass; TestCategory("Performance")>]
type IndexBenchmarkTests() =
    let chunk value =
        KnowledgeChunkId.parse ("chunk_" + String.replicate 63 "0" + value)

    [<TestMethod>]
    member _.``Benchmark reports throughput recall latency memory storage and cost``() =
        let mutable indexed = false

        let queries =
            [ KnowledgeIndexBenchmarkQuery.create "exact" [ chunk "1"; chunk "2" ] (fun _ ->
                  Assert.IsTrue(indexed)
                  Task.FromResult [ chunk "1"; chunk "2"; chunk "3" ])
              KnowledgeIndexBenchmarkQuery.create "partial" [ chunk "3"; chunk "4" ] (fun _ ->
                  Task.FromResult [ chunk "3" ]) ]

        let workload =
            KnowledgeIndexBenchmarkWorkload.create
                "deterministic"
                4
                (fun _ ->
                    indexed <- true
                    Task.CompletedTask)
                queries
                (fun _ -> Task.FromResult 4096L)
                (fun _ -> Task.FromResult 0.25m)

        let report =
            KnowledgeIndexBenchmark.runAsync (KnowledgeIndexBenchmarkConfig.create 1 2) workload CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(1, KnowledgeIndexBenchmarkReport.version report)
        Assert.AreEqual("deterministic", KnowledgeIndexBenchmarkReport.workload report)
        Assert.AreEqual(4, KnowledgeIndexBenchmarkReport.corpusRecords report)
        Assert.AreEqual(4, KnowledgeIndexBenchmarkReport.queryExecutions report)
        Assert.AreEqual(0.75, KnowledgeIndexBenchmarkReport.recall report, 0.000001)
        Assert.IsTrue(KnowledgeIndexBenchmarkReport.indexRecordsPerSecond report > 0.0)
        Assert.IsTrue(KnowledgeIndexBenchmarkReport.indexDurationMs report >= 0.0)
        Assert.IsTrue(KnowledgeIndexBenchmarkReport.managedAllocatedBytes report >= 0L)
        Assert.AreEqual(4096L, KnowledgeIndexBenchmarkReport.storageBytes report)
        Assert.AreEqual(0.25m, KnowledgeIndexBenchmarkReport.estimatedCostUsd report)

        let latency = KnowledgeIndexBenchmarkReport.queryLatency report
        Assert.AreEqual(4, KnowledgeIndexBenchmarkLatency.samples latency)
        Assert.IsTrue(KnowledgeIndexBenchmarkLatency.minimumMs latency >= 0.0)

        Assert.IsTrue(
            KnowledgeIndexBenchmarkLatency.p50Ms latency
            <= KnowledgeIndexBenchmarkLatency.p95Ms latency
        )

        Assert.IsTrue(
            KnowledgeIndexBenchmarkLatency.p95Ms latency
            <= KnowledgeIndexBenchmarkLatency.p99Ms latency
        )

        Assert.IsTrue(
            KnowledgeIndexBenchmarkLatency.p99Ms latency
            <= KnowledgeIndexBenchmarkLatency.maximumMs latency
        )

        StringAssert.Contains(KnowledgeIndexBenchmarkReport.toJson report, "\"recall\": 0.75")

    [<TestMethod>]
    member _.``Benchmark rejects invalid workload and measurements``() =
        Assert.ThrowsExactly<ArgumentException>(fun () -> KnowledgeIndexBenchmarkConfig.create -1 1 |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeIndexBenchmarkWorkload.create
                "empty"
                0
                (fun _ -> Task.CompletedTask)
                []
                (fun _ -> Task.FromResult 0L)
                (fun _ -> Task.FromResult 0m)
            |> ignore)
        |> ignore

        let query =
            KnowledgeIndexBenchmarkQuery.create "query" [ chunk "1" ] (fun _ -> Task.FromResult [ chunk "1" ])

        let workload =
            KnowledgeIndexBenchmarkWorkload.create
                "invalid-storage"
                1
                (fun _ -> Task.CompletedTask)
                [ query ]
                (fun _ -> Task.FromResult -1L)
                (fun _ -> Task.FromResult 0m)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeIndexBenchmark.runAsync (KnowledgeIndexBenchmarkConfig.create 0 1) workload CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore
