# PostgreSQL index lifecycle management

## Change

The pgvector and PostgreSQL lexical adapters now expose `ManagedKnowledgeVectorIndex` and
`ManagedKnowledgeLexicalIndex` capabilities. Management includes:

- idempotent schema creation;
- health and searchable-record evidence;
- complete namespace rebuild;
- authorization-owner-scoped backup;
- fail-closed restore.

Backups retain the exact indexed documents and, for pgvector, the persisted embedding values and
profile identity. They use Nao backup contract version 1. The returned backup value is a
host-managed snapshot; Nao does not silently choose an object store or serialize it to an
unconfigured destination.

## Authorization and deletion semantics

Backup selects only rows owned by the caller's complete authorization identity. ACL access does
not grant backup authority. Restore requires that same owner identity and replaces the complete
namespace through one index mutation.

Logical source retirement remains authoritative. Restore does not clear source retirement state,
so an older backup cannot resurrect a source deleted by lifecycle policy. Explicit index-row loss
or corruption can be restored while current source tombstones continue to fail closed.

## Qualification

The external PostgreSQL suite exercises both production indexes through:

1. initial incremental ingestion;
2. idempotent unchanged upsert;
3. source-filtered re-indexing;
4. explicit chunk deletion;
5. owner-scoped backup;
6. complete restore;
7. post-restore vector and lexical queries;
8. health evidence and cross-owner restore rejection.

The test runs against PostgreSQL with pgvector and verifies exact document and vector restoration.
Nao is unreleased, so the version-1 index and backup contracts changed in place without
compatibility adapters.
