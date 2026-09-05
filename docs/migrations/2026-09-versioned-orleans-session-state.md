# Versioned Orleans session state

## Scope

- First incompatible version: `0.2.0` development schema after FND-05 Orleans-state versioning.
- Affected state: `sessionState` and `sessionDirectory` records in the `sessionStore` provider.
- Affected runtime: `Nao.Runtime.Orleans` session and session-directory grains.

## Breaking changes

Session and directory state carry independent Nao schema versions in addition to Orleans field identifiers. Both current schemas are version 1; session messages require complete execution correlation, and session metadata requires tenant identity from the host-authenticated principal. Every session operation revalidates that principal against persisted tenant/user/group/session lineage. Existing records without the expected version and records with unsupported versions fail grain activation before state access or mutation. Nao does not include a legacy activation path or in-process converter.

## Before upgrade

1. Stop every silo and client capable of activating or writing session grains.
2. Back up the complete `sessionStore` provider data.
3. Verify the backup can be restored before deploying the new build.

## Migration

Reset development session state when retention is unnecessary. For retained session state, populate every conversation message's correlation from authoritative execution data and populate tenant identity from authoritative authentication data before rebuilding the state externally in the current version-1 shape; reset records whose exact correlation or authorization lineage cannot be reconstructed. Register `Func<SecurityPrincipal>` from the host authentication context before activating session grains. Retained session-directory records also use version `1`. Do not infer users, groups, sessions, permissions, execution identity, conversations, or directory membership. Deploy one build to all silos and validate activation, authorization rejection, reads, writes, and destruction before resuming traffic.

## Rollback

Mixed-version silos are unsupported. Stop all silos and restore the complete pre-upgrade provider backup before rollback; older builds cannot activate state written with the current contract.