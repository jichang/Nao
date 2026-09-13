# Identity-partitioned audit events

## Scope

- Current unreleased format: version 1.
- Affected API: `AuditLogs.file` persisted representation.
- Affected files: `audit/<identity-sha256>.jsonl` beneath the configured directory.

## Breaking changes

Every audit operation now requires an authenticated `AuthorizationScope`. Each claim-free authorization identity selects its own JSONL stream, whose version-1 events record accepted entries, scope deletion, or retention deletion. `AuditEntry.AgentId` remains actor metadata and does not select ownership. Event payloads contain no authorization identity or authentication evidence. The former whole-file document is not read through compatibility logic.

## Before upgrade

1. Stop all writers using the audit directory.
2. Delete the unreleased `audit-log.json` development file.
3. Deploy the identity-partitioned implementation and begin new version-1 streams.

## Migration

Nao is unreleased, so there is no compatibility reader or retained-data migration. Development audit data from the former abstraction must be discarded.

## Rollback

Rollback is unsupported for this unreleased breaking redesign. Never copy unpartitioned audit entries into an authenticated identity stream.