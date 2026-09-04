# Versioned ADO.NET memory tables

## Scope

- First incompatible version: `0.2.0` development schema after FND-05 database versioning.
- Affected APIs: `MemoryStores.ado` and `SemanticMemories.ado`.
- Affected tables: `nao_memory`, `nao_semantic`, and the new `nao_schema_versions` marker table.

## Breaking changes

ADO.NET key/value and semantic-memory tables require component schema version 1 in `nao_schema_versions`. Existing unmarked tables, missing current tables, malformed markers, and unsupported versions reject before table or row mutation. Nao does not stamp existing tables automatically or include an in-process converter.

## Before upgrade

1. Stop every process using the database.
2. Back up the database and export retained memory with explicit owners and timestamps.
3. Verify the backup can be restored before deploying the new build.

## Migration

Reset development tables when retention is unnecessary. For retained data, externally validate each table against the current column and ownership contract, create `nao_schema_versions (component TEXT NOT NULL PRIMARY KEY, schema_version INTEGER NOT NULL)`, and insert version `1` for components `memory` and `semantic-memory` only after their corresponding tables pass validation. Deploy one build everywhere and validate recall, save, expiry, and owner deletion.

## Rollback

Older implementations ignore schema markers but are not supported against state written by the new build. Stop all writers and restore the complete pre-upgrade backup before rollback.