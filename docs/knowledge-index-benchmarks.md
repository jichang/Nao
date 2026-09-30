# Knowledge index benchmarks

Nao's index benchmark runner produces versioned JSON evidence for a complete workload:

- corpus record count;
- indexing duration and records per second;
- mean recall across measured query executions;
- minimum, mean, p50, p95, p99, and maximum query latency;
- managed bytes allocated by the benchmark process;
- adapter-reported physical storage bytes;
- explicit estimated cost in USD.

`KnowledgeIndexBenchmarkWorkload` keeps cost and storage measurement adapter-owned. This avoids
inventing a cost for self-hosted PostgreSQL and avoids presenting database storage as process
memory. Managed allocation measures client pressure; PostgreSQL adapters separately report the
complete table, TOAST, and index footprint with `pg_total_relation_size`.

## Reproducible PostgreSQL workload

The integration benchmark uses:

- PostgreSQL 17 with pgvector;
- 64 records;
- eight relevance groups with eight records each;
- eight-dimensional normalized vectors;
- eight vector and eight lexical queries;
- one warmup and three measured iterations;
- exact expected chunk identities for recall calculation;
- zero provider cost because embeddings are deterministic fixtures.

Start a disposable database and run only the benchmark:

```bash
docker run --rm --name nao-postgres-benchmark \
  -e POSTGRES_PASSWORD=nao_test \
  -e POSTGRES_USER=nao_test \
  -e POSTGRES_DB=nao_test \
  -p 55432:5432 \
  -d pgvector/pgvector:pg17

NAO_TEST_POSTGRES='Host=127.0.0.1;Port=55432;Database=nao_test;Username=nao_test;Password=nao_test' \
  dotnet test tests/Nao.Knowledge.Postgres.Tests/Nao.Knowledge.Postgres.Tests.fsproj \
  -c Release --filter 'TestCategory=Benchmark' \
  --logger 'console;verbosity=detailed'

docker stop nao-postgres-benchmark
```

Run benchmark categories in isolation when comparing latency or allocation. Parallel test activity
can legitimately affect both.

## Reference run

On 2026-09-30, the disposable workload produced:

| Adapter | Recall | Index throughput | Query p50 | Query p95 | Allocated bytes | Storage bytes | Cost |
|---|---:|---:|---:|---:|---:|---:|---:|
| pgvector | 1.000 | 300.0 records/s | 1.850 ms | 2.422 ms | 4,099,784 | 229,376 | $0 |
| PostgreSQL lexical | 1.000 | 880.3 records/s | 1.868 ms | 2.830 ms | 3,230,488 | 237,568 | $0 |

These values are reproducibility evidence, not universal performance thresholds. Hardware,
container load, PostgreSQL settings, corpus distribution, HNSW settings, and query concurrency
must accompany any comparison. Recall is the correctness gate; latency, allocation, storage, and
cost are regression signals interpreted against comparable runs.
