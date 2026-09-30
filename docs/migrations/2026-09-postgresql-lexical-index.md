# 2026-09 PostgreSQL lexical knowledge index

## Scope

- Extended optional package: `Nao.Knowledge.Postgres`.
- New adapter: `PostgresLexicalKnowledgeIndex`.
- New configuration: `PostgresLexicalIndexConfig`.
- Renamed shared data-source factory: `PostgresKnowledgeDataSource`.
- Current Nao-owned index schema remains version 1.

## Storage model

Each configured PostgreSQL text-search configuration maps to a stable, configuration-derived
table. Rows contain the same source-backed document and authorization fields used by the
pgvector adapter, plus a persisted `tsvector`. The table has:

- a GIN index over `search_vector`;
- tenant, workspace, and namespace boundary indexes;
- source and immutable document-version indexes;
- a GIN metadata index;
- an owner, namespace, and chunk primary key.

The shared `lexical_indexes` marker binds the derived table to the exact text-search
configuration. Adapter creation fails when an existing marker conflicts.

## Query semantics

Text-search configuration names are values, not SQL fragments. Adapter initialization resolves
the configured name through PostgreSQL's `regconfig` type. Writes build vectors with
`to_tsvector`; queries parse user text with `websearch_to_tsquery` and rank matching rows with
`ts_rank_cd`.

Tenant, workspace, session, owner identity, and user/group/role ACL predicates execute in the
same SQL query as full-text matching and ranking. Namespace, source/version, metadata, and time
filters also execute before results leave PostgreSQL.

## Mutation semantics

Only the complete owning `AuthorizationIdentity` may mutate rows. `KnowledgeIndexMutation`
combines bulk upserts with chunk-ID and/or typed-filter deletion. The adapter applies their union
in one database transaction and reports upserted and deleted counts only after commit. A failure
rolls back earlier statements in the same mutation.

Permanent source deletion must use the mutation's retirement set. The adapter transaction-locks a
durable owner/namespace/source state row, marks it retired, and deletes all matching lexical rows.
Queries join this state inside PostgreSQL and later upserts for the retired identity fail.

## Host integration

Create or supply an `NpgsqlDataSource`, then call:

```fsharp
PostgresLexicalKnowledgeIndex.createAsync dataSource config cancellationToken
```

`PostgresKnowledgeDataSource.create` builds a data source suitable for both pgvector and lexical
adapters. The host owns credentials, pooling, rotation, health supervision, and disposal.

The configured PostgreSQL text-search configuration must already exist. `pg_catalog.simple` is
the default because it is language-neutral and deterministic. Hosts may select a stemming
configuration such as `pg_catalog.english` explicitly.

## Testing

Set `NAO_TEST_POSTGRES` to a PostgreSQL connection string and run:

```bash
dotnet test tests/Nao.Knowledge.Postgres.Tests/Nao.Knowledge.Postgres.Tests.fsproj -c Release
```

The integration suite verifies real `tsvector` persistence, GIN-backed matching, ranking,
SQL-bound ACL isolation, metadata filtering, owner-only writes, filtered deletion, and stale-row
disappearance.

## Rollback

Stop lexical writes and queries, drop the configuration-derived lexical tables and
`lexical_indexes` marker, and remove lexical adapter construction. Vector tables are independent
and do not need to be removed.
