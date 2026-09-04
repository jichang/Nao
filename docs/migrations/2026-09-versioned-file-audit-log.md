# Versioned file audit log

## Scope

- First incompatible version: `0.2.0` development schema after FND-05 audit versioning.
- Affected API: `AuditLogs.file` persisted representation.
- Affected file: `audit-log.json` beneath the configured audit directory.

## Breaking changes

The file audit log is a version-1 document containing `schemaVersion` and `value`. Earlier raw JSON arrays and empty files are rejected before recording or deletion. Nao does not include a legacy reader or in-process converter.

## Before upgrade

1. Stop all writers using the audit directory.
2. Export required audit evidence and back up `audit-log.json`.
3. Verify the backup before deploying the new version.

## Migration

Delete the old development audit log when retention is unnecessary. For retained data, externally wrap the old JSON array as `{ "schemaVersion": 1, "value": <old-array> }` without changing action kinds, identities, timestamps, or metadata. Deploy and validate recording, queries, denied counts, and owner deletion.

## Rollback

Older implementations cannot read the version-1 document. Restore the backup before rollback; do not unwrap or transform the active audit log while a Nao process is running.