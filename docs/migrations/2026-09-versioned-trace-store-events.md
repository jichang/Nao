# Versioned trace-store events

## Scope

- First incompatible version: `0.2.0` development schema after FND-05 trace-store versioning.
- Affected APIs: `TraceStores.file` and `TraceStores.ado` persisted representations.
- Affected data: previous `trace-store.jsonl` files and rows in the unpartitioned `trace-store` event stream.

## Breaking changes

Each trace-store event is a version-1 envelope containing `schemaVersion` and `event`. The host-authenticated `AuthorizationScope` selects a `trace-store/<identity-sha256>` file or ADO.NET stream; authorization identity and authentication evidence are not serialized into the event. `ExecutionTrace.AgentId` remains actor metadata and cannot select a partition. Earlier unpartitioned events, bare discriminated-union events, unsupported versions, unknown event cases, and malformed events are rejected before replay or mutation. Nao does not include a legacy reader or in-process converter.

## Before upgrade

1. Stop all processes writing the trace store.
2. Back up the JSONL file or event table containing the previous `trace-store` stream.
3. Verify the backup before deploying the new version.

## Migration

Delete development trace history before using the current build. If trace history must be retained, export it and rebuild one current v1 stream per complete authorization identity outside Nao. Do not infer tenant, user, group, workspace, or session ownership from `ExecutionTrace.AgentId`. Validate each complete rebuilt stream before enabling writers, then verify scope-bound baseline, agent, execution, retention, and deletion operations. ADO.NET storage must also follow the [table-schema migration](2026-09-versioned-ado-table-schemas.md).

## Rollback

Older implementations cannot read version-1 envelopes. Restore the complete backup before rollback and never mix bare and enveloped events in one stream.