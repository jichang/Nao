namespace Nao.Knowledge

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type KnowledgeIndexBenchmarkLatency =
    private
        { Samples: int
          MinimumMs: float
          MeanMs: float
          P50Ms: float
          P95Ms: float
          P99Ms: float
          MaximumMs: float }

[<RequireQualifiedAccess>]
module KnowledgeIndexBenchmarkLatency =
    let private percentile percentile (ordered: float array) =
        let rank = int (Math.Ceiling(percentile * float ordered.Length)) - 1
        ordered[max 0 rank]

    let create samples =
        let ordered =
            samples
            |> Seq.map (fun value ->
                if value < 0.0 || Double.IsNaN value || Double.IsInfinity value then
                    invalidArg (nameof samples) "Benchmark latency samples must be finite and non-negative."

                value)
            |> Seq.sort
            |> Array.ofSeq

        if ordered.Length = 0 then
            invalidArg (nameof samples) "Benchmark latency requires at least one sample."

        { Samples = ordered.Length
          MinimumMs = ordered[0]
          MeanMs = Array.average ordered
          P50Ms = percentile 0.50 ordered
          P95Ms = percentile 0.95 ordered
          P99Ms = percentile 0.99 ordered
          MaximumMs = ordered[ordered.Length - 1] }

    let samples value = value.Samples
    let minimumMs value = value.MinimumMs
    let meanMs value = value.MeanMs
    let p50Ms value = value.P50Ms
    let p95Ms value = value.P95Ms
    let p99Ms value = value.P99Ms
    let maximumMs value = value.MaximumMs

type KnowledgeIndexBenchmarkQuery =
    private
        { Name: string
          RelevantChunkIds: Set<KnowledgeChunkId>
          RetrieveAsync: CancellationToken -> Task<KnowledgeChunkId list> }

[<RequireQualifiedAccess>]
module KnowledgeIndexBenchmarkQuery =
    let create name relevantChunkIds retrieveAsync =
        if String.IsNullOrWhiteSpace name || name <> name.Trim() then
            invalidArg (nameof name) "Benchmark query name must be non-blank and trimmed."

        let relevantChunkIds = Set.ofSeq relevantChunkIds

        if relevantChunkIds.IsEmpty then
            invalidArg (nameof relevantChunkIds) "Benchmark query requires at least one relevant chunk."

        if isNull (box retrieveAsync) then
            nullArg (nameof retrieveAsync)

        { Name = name
          RelevantChunkIds = relevantChunkIds
          RetrieveAsync = retrieveAsync }

    let name value = value.Name
    let relevantChunkIds value = value.RelevantChunkIds
    let internal retrieveAsync value = value.RetrieveAsync

type KnowledgeIndexBenchmarkWorkload =
    private
        { Name: string
          CorpusRecords: int
          IndexAsync: CancellationToken -> Task
          Queries: KnowledgeIndexBenchmarkQuery list
          MeasureStorageBytesAsync: CancellationToken -> Task<int64>
          MeasureCostUsdAsync: CancellationToken -> Task<decimal> }

[<RequireQualifiedAccess>]
module KnowledgeIndexBenchmarkWorkload =
    let create name corpusRecords indexAsync queries measureStorageBytesAsync measureCostUsdAsync =
        if String.IsNullOrWhiteSpace name || name <> name.Trim() then
            invalidArg (nameof name) "Benchmark workload name must be non-blank and trimmed."

        if corpusRecords <= 0 then
            invalidArg (nameof corpusRecords) "Benchmark corpus must contain at least one record."

        if isNull (box indexAsync) then
            nullArg (nameof indexAsync)

        if List.isEmpty queries then
            invalidArg (nameof queries) "Benchmark workload requires at least one query."

        if isNull (box measureStorageBytesAsync) then
            nullArg (nameof measureStorageBytesAsync)

        if isNull (box measureCostUsdAsync) then
            nullArg (nameof measureCostUsdAsync)

        { Name = name
          CorpusRecords = corpusRecords
          IndexAsync = indexAsync
          Queries = queries
          MeasureStorageBytesAsync = measureStorageBytesAsync
          MeasureCostUsdAsync = measureCostUsdAsync }

    let name value = value.Name
    let corpusRecords value = value.CorpusRecords
    let queries value = value.Queries
    let internal indexAsync value = value.IndexAsync
    let internal measureStorageBytesAsync value = value.MeasureStorageBytesAsync
    let internal measureCostUsdAsync value = value.MeasureCostUsdAsync

type KnowledgeIndexBenchmarkConfig =
    private
        { WarmupIterations: int
          MeasuredIterations: int }

[<RequireQualifiedAccess>]
module KnowledgeIndexBenchmarkConfig =
    let create warmupIterations measuredIterations =
        if warmupIterations < 0 then
            invalidArg (nameof warmupIterations) "Benchmark warmup iterations cannot be negative."

        if measuredIterations <= 0 then
            invalidArg (nameof measuredIterations) "Benchmark measured iterations must be positive."

        { WarmupIterations = warmupIterations
          MeasuredIterations = measuredIterations }

    let defaultConfig = create 1 10
    let warmupIterations value = value.WarmupIterations
    let measuredIterations value = value.MeasuredIterations

type KnowledgeIndexBenchmarkReport =
    private
        { Version: int
          Workload: string
          CorpusRecords: int
          QueryExecutions: int
          IndexDurationMs: float
          IndexRecordsPerSecond: float
          Recall: float
          QueryLatency: KnowledgeIndexBenchmarkLatency
          ManagedAllocatedBytes: int64
          StorageBytes: int64
          EstimatedCostUsd: decimal
          StartedAt: DateTimeOffset
          CompletedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeIndexBenchmarkReport =
    let currentVersion = 1
    let version value = value.Version
    let workload value = value.Workload
    let corpusRecords value = value.CorpusRecords
    let queryExecutions value = value.QueryExecutions
    let indexDurationMs value = value.IndexDurationMs
    let indexRecordsPerSecond value = value.IndexRecordsPerSecond
    let recall value = value.Recall
    let queryLatency value = value.QueryLatency
    let managedAllocatedBytes value = value.ManagedAllocatedBytes
    let storageBytes value = value.StorageBytes
    let estimatedCostUsd value = value.EstimatedCostUsd
    let startedAt value = value.StartedAt
    let completedAt value = value.CompletedAt

    let toJson value =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        let latency = value.QueryLatency
        writer.WriteStartObject()
        writer.WriteNumber("version", value.Version)
        writer.WriteString("workload", value.Workload)
        writer.WriteNumber("corpusRecords", value.CorpusRecords)
        writer.WriteNumber("queryExecutions", value.QueryExecutions)
        writer.WriteNumber("indexDurationMs", value.IndexDurationMs)
        writer.WriteNumber("indexRecordsPerSecond", value.IndexRecordsPerSecond)
        writer.WriteNumber("recall", value.Recall)
        writer.WriteStartObject("queryLatency")
        writer.WriteNumber("samples", latency.Samples)
        writer.WriteNumber("minimumMs", latency.MinimumMs)
        writer.WriteNumber("meanMs", latency.MeanMs)
        writer.WriteNumber("p50Ms", latency.P50Ms)
        writer.WriteNumber("p95Ms", latency.P95Ms)
        writer.WriteNumber("p99Ms", latency.P99Ms)
        writer.WriteNumber("maximumMs", latency.MaximumMs)
        writer.WriteEndObject()
        writer.WriteNumber("managedAllocatedBytes", value.ManagedAllocatedBytes)
        writer.WriteNumber("storageBytes", value.StorageBytes)
        writer.WriteNumber("estimatedCostUsd", value.EstimatedCostUsd)
        writer.WriteString("startedAt", value.StartedAt)
        writer.WriteString("completedAt", value.CompletedAt)
        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

[<RequireQualifiedAccess>]
module KnowledgeIndexBenchmark =
    let runAsync
        (config: KnowledgeIndexBenchmarkConfig)
        (workload: KnowledgeIndexBenchmarkWorkload)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeIndexBenchmarkReport> =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let startedAt = DateTimeOffset.UtcNow
            let allocatedBefore = GC.GetTotalAllocatedBytes(false)
            let indexTimer = Stopwatch.StartNew()
            do! KnowledgeIndexBenchmarkWorkload.indexAsync workload cancellationToken
            indexTimer.Stop()

            let corpusRecords = KnowledgeIndexBenchmarkWorkload.corpusRecords workload
            let queries = KnowledgeIndexBenchmarkWorkload.queries workload

            for _ in 1 .. KnowledgeIndexBenchmarkConfig.warmupIterations config do
                for query in queries do
                    let! _ = KnowledgeIndexBenchmarkQuery.retrieveAsync query cancellationToken
                    ()

            let latencies = ResizeArray<float>()
            let mutable recallTotal = 0.0

            for _ in 1 .. KnowledgeIndexBenchmarkConfig.measuredIterations config do
                for query in queries do
                    let timer = Stopwatch.StartNew()
                    let! retrieved = KnowledgeIndexBenchmarkQuery.retrieveAsync query cancellationToken
                    timer.Stop()
                    latencies.Add timer.Elapsed.TotalMilliseconds
                    let relevant = KnowledgeIndexBenchmarkQuery.relevantChunkIds query
                    let retrieved = Set.ofList retrieved

                    recallTotal <-
                        recallTotal
                        + float (Set.intersect relevant retrieved).Count / float relevant.Count

            let queryExecutions = latencies.Count

            let! storageBytes = KnowledgeIndexBenchmarkWorkload.measureStorageBytesAsync workload cancellationToken

            if storageBytes < 0L then
                invalidOp "Benchmark storage measurement cannot be negative."

            let! estimatedCostUsd = KnowledgeIndexBenchmarkWorkload.measureCostUsdAsync workload cancellationToken

            if estimatedCostUsd < 0m then
                invalidOp "Benchmark cost measurement cannot be negative."

            let indexDurationMs = indexTimer.Elapsed.TotalMilliseconds
            let indexSeconds = max indexTimer.Elapsed.TotalSeconds Double.Epsilon

            return
                { Version = KnowledgeIndexBenchmarkReport.currentVersion
                  Workload = KnowledgeIndexBenchmarkWorkload.name workload
                  CorpusRecords = corpusRecords
                  QueryExecutions = queryExecutions
                  IndexDurationMs = indexDurationMs
                  IndexRecordsPerSecond = float corpusRecords / indexSeconds
                  Recall = recallTotal / float queryExecutions
                  QueryLatency = KnowledgeIndexBenchmarkLatency.create latencies
                  ManagedAllocatedBytes = max 0L (GC.GetTotalAllocatedBytes(false) - allocatedBefore)
                  StorageBytes = storageBytes
                  EstimatedCostUsd = estimatedCostUsd
                  StartedAt = startedAt
                  CompletedAt = DateTimeOffset.UtcNow }
        }
