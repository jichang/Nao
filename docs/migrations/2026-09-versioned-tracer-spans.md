# Versioned tracer spans

## Scope

- First incompatible version: `0.2.0` development schema after FND-05 tracer versioning.
- Affected APIs: `Tracers.file` and `Tracers.ado` persisted representations.
- Affected data: `tracer.jsonl` and rows in the `tracer` event stream.

## Breaking changes

Each persisted span snapshot is now a version-1 envelope containing `schemaVersion` and `value`. Earlier bare spans, unsupported versions, and malformed snapshots are rejected before replay or mutation. Nao does not include a legacy reader or in-process converter.

## Before upgrade

1. Stop all processes writing tracer data.
2. Back up the JSONL file or event table containing the `tracer` stream.
3. Verify the backup before deploying the new version.

## Migration

Delete development traces when retention is unnecessary. For retained data, externally wrap each bare span as `{ "schemaVersion": 1, "value": <old-span> }` without changing identifiers, relationships, timestamps, status, attributes, or events. Validate every transformed row before replacing active data, then verify trace reconstruction and span updates.

## Rollback

Older implementations cannot read version-1 envelopes. Restore the complete backup before rollback and never mix bare spans and envelopes in one stream.