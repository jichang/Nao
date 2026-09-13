# Versioned ADO.NET memory tables

## Scope

- First incompatible version: `0.2.0` development schema after FND-05 database versioning.
- Affected APIs: `MemoryStores.ado` and `SemanticMemories.ado`.
- Affected tables: `nao_memory`, `nao_semantic`, and the new `nao_schema_versions` marker table.

## Breaking changes

ADO.NET key/value and semantic-memory tables require component schema version 1 in `nao_schema_versions`. `nao_memory` and `nao_semantic` use `owner_key` as the complete claim-free authorization identity's SHA-256 partition key; all reads and mutations are scoped to it. Existing unmarked tables, missing current tables, malformed markers, incompatible v1 column layouts, and unsupported versions reject before table or row mutation. Nao does not stamp existing tables automatically or include an in-process converter.

## Before upgrade

1. Stop every process using the database.
2. Back up the database and export retained memory with complete authorization ownership identity and timestamps.
3. Verify the backup can be restored before deploying the new build.

## Migration

Reset development tables when retention is unnecessary. For retained data, externally transform every legacy owner partition to its complete authorization identity SHA-256 key, validate each table against the current `owner_key` column and ownership contract, create `nao_schema_versions (component TEXT NOT NULL PRIMARY KEY, schema_version INTEGER NOT NULL)`, and insert version `1` for components `memory` and `semantic-memory` only after their corresponding tables pass validation. Deploy one build everywhere and validate scope-bound recall, save, expiry, and deletion.

## Rollback

Older implementations ignore schema markers but are not supported against state written by the new build. Stop all writers and restore the complete pre-upgrade backup before rollback.