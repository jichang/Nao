# 2026-09 atomic index mutations

## Scope

- Replaced separate vector and lexical `UpsertAsync` and `DeleteAsync` operations.
- Added `KnowledgeIndexMutation<'Record>`.
- Added `KnowledgeIndexMutationResult`.
- Added `ApplyAsync` to vector and lexical index capabilities.

Nao is unreleased, so callers must move to the atomic API directly. No compatibility adapter is
provided.

## Mutation shape

A mutation binds exactly one index namespace and may include:

- a bulk list of records to upsert;
- explicit chunk IDs to delete;
- one typed source/version/metadata/time deletion filter.
- source IDs to retire permanently from the owner and namespace.

At least one operation is required. Adapters must reject duplicate upsert chunk identities,
records from another namespace, incompatible vector profiles, and records owned by another
authorization identity before opening a transaction.

Chunk-ID and filter deletion use union semantics. If a row matches both, it contributes once to
the deleted-row count.

Source retirement is different from ordinary deletion. It creates a durable index-local tombstone,
deletes every row for the source, and rejects all later upserts for that source identity. A retired
source ID must not also appear among a mutation's upserts.

## Transaction semantics

PostgreSQL vector and lexical adapters execute all deletion and upsert statements through one
connection and one database transaction. Delete statements run before upserts, permitting an
explicit delete-and-reinsert replacement in one mutation. Any statement failure rolls back all
earlier writes.

Each physical index has a source-state registry keyed by owner, namespace, and source ID. Mutations
lock affected source rows in stable order before checking or changing retirement state. This
serializes upsert and retirement races. Queries require a non-retired source-state row inside SQL
in addition to their authorization predicate.

`KnowledgeIndexMutationResult` is returned only after commit and contains:

- namespace;
- submitted upsert count;
- rows actually deleted;
- source retirements ensured by the mutation;
- completion timestamp.

## Migration

Replace:

```fsharp
do! index.UpsertAsync authorization records cancellationToken
let! deleted = index.DeleteAsync authorization namespaceName filter cancellationToken
```

with:

```fsharp
let mutation =
    KnowledgeIndexMutation.create namespaceName records chunkIds (Some filter) []

let! result = index.ApplyAsync authorization mutation cancellationToken
```

Use `KnowledgeIndexMutationResult.deletedRecords` when lifecycle orchestration must prove that
stale rows were removed.

When deleting a source permanently, pass its ID as the final argument instead of relying only on a
source filter:

```fsharp
let retirement =
    KnowledgeIndexMutation.create namespaceName [] [] None [ sourceId ]

let! result = index.ApplyAsync authorization retirement cancellationToken
```

Retirement is idempotent. A successful retry reports the number of requested sources whose retired
state was ensured, while `deletedRecords` reports only rows removed by that attempt.

## Validation

The PostgreSQL integration suite deliberately places a database-invalid record after a valid row
in both vector and lexical batches. The mutation fails and subsequent queries prove that the
earlier row was rolled back.
