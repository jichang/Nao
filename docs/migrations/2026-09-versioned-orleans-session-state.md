# Versioned Orleans session state

## Scope

- First incompatible version: `0.2.0` development schema after FND-05 Orleans-state versioning.
- Affected state: `sessionState` and `sessionDirectory` records in the `sessionStore` provider.
- Affected runtime: `Nao.Runtime.Orleans` session and session-directory grains.

## Breaking changes

Session and directory state now carry Nao schema version 1 in addition to Orleans field identifiers. Existing records without that version and records with unsupported versions fail grain activation before state access or mutation. Nao does not include a legacy activation path or in-process converter.

## Before upgrade

1. Stop every silo and client capable of activating or writing session grains.
2. Back up the complete `sessionStore` provider data.
3. Verify the backup can be restored before deploying the new build.

## Migration

Reset development session state when retention is unnecessary. For retained state, transform provider records externally by setting the top-level Nao `SchemaVersion` field to `1` only after validating that every record matches the current `SessionGrainState` or `SessionDirectoryState` shape. Do not infer users, sessions, permissions, conversations, or directory membership. Deploy one build to all silos and validate activation, reads, writes, and destruction before resuming traffic.

## Rollback

Mixed-version silos are unsupported. Stop all silos and restore the complete pre-upgrade provider backup before rollback; older builds cannot activate state written with the current contract.