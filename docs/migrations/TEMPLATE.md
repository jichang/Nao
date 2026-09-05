# YYYY-MM change name

## Scope

- First incompatible version:
- Affected packages and APIs:
- Affected files, streams, tables, or Orleans state:

## Breaking changes

- Describe the old and new contracts.

## Before upgrade

1. Stop writers.
2. Back up or export affected data.
3. Validate that the backup can be restored.

## Migration

1. Transform data externally with explicit identity and ownership mappings, or delete development data when retention is unnecessary.
2. Deploy the new version.
3. Validate current-schema reads and writes.

## Validation

List the commands, tests, record counts, and operational checks that prove the migration succeeded.

## Rollback

State whether rollback is possible after new-format writes and how to restore the backup.