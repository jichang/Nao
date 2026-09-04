# Versioned ADO.NET table schemas

## Scope

- First incompatible version: `0.2.0` development schema after FND-05 database versioning.
- Affected APIs: all ADO.NET persistence factories.
- Affected tables: `nao_events`, `nao_feedback_turns`, `nao_feedback_entries`, `nao_memory`, `nao_semantic`, `nao_audit`, `nao_execution_journal`, and `nao_schema_versions`.

## Breaking changes

Every current ADO.NET persistence table requires component schema version 1 in `nao_schema_versions`. Fresh marker and component tables are created atomically. Existing unmarked tables, missing marked tables, malformed markers, and unsupported versions reject before table or row mutation. Event payload schema versions remain independently required. Nao does not stamp existing tables automatically or include an in-process converter.

The current component keys are `events`, `feedback-turns`, `feedback-entries`, `memory`, `semantic-memory`, `audit`, and `execution-journal`.

## Before upgrade

1. Stop every process using the database.
2. Back up the complete database and export data that must be retained.
3. Verify the backup can be restored before deploying the new build.

## Migration

Reset development tables when retention is unnecessary. For retained data, externally validate each table against its current column, payload, identity, and ownership contract. Create `nao_schema_versions (component TEXT NOT NULL PRIMARY KEY, schema_version INTEGER NOT NULL)` and insert version `1` for a component only after its corresponding table passes validation. Validate event envelopes separately. Deploy one build everywhere and verify reads, writes, retention, and owner deletion before resuming traffic.

## Rollback

Older implementations ignore component markers but are unsupported against data written by the new build. Stop all writers and restore the complete pre-upgrade backup before rollback.