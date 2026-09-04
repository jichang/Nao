# Versioned file memory

## Scope

- First incompatible version: `0.2.0` development schema after FND-05 file-memory versioning.
- Affected APIs: `MemoryStores.file` and `SemanticMemories.file` persisted representations.
- Affected files: owner JSON files beneath configured key/value and semantic-memory directories.

## Breaking changes

Memory files are version-1 documents containing `schemaVersion` and `value`. Earlier raw JSON arrays and empty files are rejected before save, removal, expiry deletion, or owner deletion. Nao does not include a legacy reader or in-process converter.

## Before upgrade

1. Stop all writers using the memory directories.
2. Export retained memory or back up the complete directories.
3. Verify the backup before deploying the new version.

## Migration

Delete old development memory when retention is unnecessary. For retained data, externally wrap each old JSON array as `{ "schemaVersion": 1, "value": <old-array> }` without inferring owner, identity, timestamps, tags, or embeddings. Deploy and validate owner-scoped recall, save, and deletion.

## Rollback

Older implementations cannot read version-1 documents. Restore the complete backup before rollback and do not mix old and new files in one memory directory.