# Versioned trace-store events

## Scope

- First incompatible version: `0.2.0` development schema after FND-05 trace-store versioning.
- Affected APIs: `TraceStores.file` and `TraceStores.ado` persisted representations.
- Affected data: `trace-store.jsonl` and rows in the `trace-store` event stream.

## Breaking changes

Each trace-store event is now a version-1 envelope containing `schemaVersion` and `event`. Earlier bare discriminated-union events, unsupported versions, unknown event cases, and malformed events are rejected before replay or mutation. Nao does not include a legacy reader or in-process converter.

## Before upgrade

1. Stop all processes writing the trace store.
2. Back up the JSONL file or event table containing the `trace-store` stream.
3. Verify the backup before deploying the new version.

## Migration

Delete development trace history when retention is unnecessary. For retained data, externally wrap each bare event as `{ "schemaVersion": 1, "event": <old-event> }` without changing trace identifiers, owners, timestamps, or event cases. ADO.NET storage must also follow the [table-schema migration](2026-09-versioned-ado-table-schemas.md). Validate the complete transformed stream before replacing the active data, then verify trace queries and owner deletion.

## Rollback

Older implementations cannot read version-1 envelopes. Restore the complete backup before rollback and never mix bare and enveloped events in one stream.