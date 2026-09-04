# Versioned conversation files

## Scope

- First incompatible version: `0.2.0` development schema after FND-05 conversation versioning.
- Affected API: `FileConversationStore` persisted representation only.
- Affected files: `messages.json`, `meta.json`, and `conversations.json` under session conversation directories.

## Breaking changes

Conversation files are version-1 documents containing `schemaVersion` and `value`. Earlier raw message arrays, metadata objects, and index arrays are rejected before append or rewrite. Nao does not include a legacy reader or in-process converter.

## Before upgrade

1. Stop every writer using the sessions directory.
2. Export conversations that must be retained or back up the complete sessions directory.
3. Verify the backup before deploying the new version.

## Migration

For development data that need not be retained, delete the old session directories. For retained data, transform each file externally into `{ "schemaVersion": 1, "value": <old-json> }`, preserving the old JSON value exactly, then deploy and validate conversation listing, load, and append behavior.

Do not infer session identity, conversation identity, ownership, or message roles while transforming data.

## Rollback

The older implementation cannot read version-1 documents. Restore the complete backup before running it; do not mix old and new conversation files in one sessions directory.