# Authenticated conversation streams

## Scope

- Current unreleased format: version 1.
- Affected API: `ConversationStore` and `FileConversationStore`.
- Affected files: `conversation/<identity-sha256>.jsonl`.

## Breaking changes

Every conversation operation now requires an authenticated `AuthorizationScope`. `ConversationMeta.SessionId` and caller-selected session storage keys were removed. Saved messages, appended messages, conversation deletion, and scope deletion are version-1 events in a stream selected from the complete claim-free identity. Payloads contain no authorization evidence.

## Before upgrade

1. Stop every writer using the sessions directory.
2. Delete unreleased conversation development directories.
3. Deploy the identity-partitioned implementation.

## Migration

Nao is unreleased, so there is no compatibility reader or retained-data migration. Do not copy caller-keyed conversation files into authenticated streams.

## Rollback

Rollback is unsupported for this unreleased breaking redesign.