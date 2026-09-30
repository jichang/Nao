# 2026-09 pgvector knowledge index

## Scope

- New optional package: `Nao.Knowledge.Postgres`.
- New dependencies: Npgsql and Pgvector.
- Current Nao-owned index and profile schemas remain version 1.

## Storage model

The adapter creates the `vector` extension when necessary and creates its objects under a
validated, host-selected PostgreSQL schema. Each complete embedding profile maps to a
profile-derived table whose vector column has the profile's fixed dimensions. The HNSW operator
class matches the profile's distance metric.

Rows contain:

- complete authorization ownership and user/group/role ACL fields;
- namespace, source, immutable document-version, and chunk identities;
- content hash and source-backed text;
- prefixed source/version/chunk metadata;
- source-modification, ingestion, and index timestamps;
- one fixed-profile vector.

The shared `vector_profiles` marker records the complete compatibility identity. Adapter creation
fails if an existing marker differs from the requested profile.

## Host integration

Construct an `NpgsqlDataSource` with `PostgresKnowledgeDataSource.create`, or configure a host-owned data
source with Pgvector's `UseVector` extension. Pass it to `PgvectorKnowledgeIndex.createAsync`.
The host retains responsibility for credentials, secret rotation, pooling, health supervision,
and disposal.

The database role needs permission to create the configured schema and its tables and indexes.
It also needs permission to create the `vector` extension when the extension is not preinstalled.
Production operators may install the extension ahead of time and grant only schema-local DDL and
DML permissions afterward.

## Authorization and mutation semantics

Authorization is not a post-query filter. Tenant, workspace, session, owner identity, and
user/group/role ACL predicates execute inside the nearest-neighbor SQL query. Additional
namespace, source/version, metadata, and time filters execute in the same boundary.

Only the complete owning `AuthorizationIdentity` may mutate rows. `KnowledgeIndexMutation`
combines bulk upserts with chunk-ID and/or typed-filter deletion. The adapter applies their union
in one database transaction and reports upserted and deleted counts only after commit. A failure
rolls back earlier statements in the same mutation.

Permanent source deletion must use the mutation's retirement set. The adapter transaction-locks a
durable owner/namespace/source state row, marks it retired, and deletes all matching vectors.
Queries join this state inside PostgreSQL and later upserts for the retired identity fail.

## Testing

Set `NAO_TEST_POSTGRES` to a PostgreSQL connection string for a server with pgvector available,
then run:

```bash
dotnet test tests/Nao.Knowledge.Postgres.Tests/Nao.Knowledge.Postgres.Tests.fsproj -c Release
```

The integration test creates and removes an isolated schema. It verifies schema creation,
fixed-profile ranking, SQL-bound ACL isolation, metadata filtering, owner-only writes, deletion,
and stale-row disappearance.

## Rollback

Stop writes, drop the profile-derived tables and `vector_profiles` marker in the configured
schema, and remove the optional package reference. Do not reinterpret profile-bound vectors as a
different model, dimension, preprocessing version, normalization, or distance metric.
