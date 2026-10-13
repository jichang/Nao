# Artifact address and lineage boundary

## Change

The payload-only produced-artifact record has been replaced in place. `Artifact` is now a private,
version-1 contract for an addressable object consumed or produced by an execution.

Every artifact requires:

- a stable artifact identity and idempotency key;
- an explicit `Consumed` or `Produced` role;
- an absolute address;
- a non-blank kind and content type;
- a redacted payload and matching canonical SHA-256 content hash;
- the recording execution and timestamp;
- unique parent artifact identities.

Produced artifacts receive a canonical `nao://artifacts/<artifact-id>` address. Consumed artifacts
retain a caller-supplied absolute source address. Restore rejects unsupported schema versions,
content tampering, duplicate parents, and self-parent lineage.

Runtime conversation persistence and event transport now retain the complete artifact contract.

## API break

`Artifact.create` was removed. Callers must use `Artifact.produced` or `Artifact.consumed` and
supply exact execution lineage. Artifact record fields are private; callers use module accessors.
The persisted Orleans record shape changed directly and no legacy reader or compatibility fallback
is provided.

## Compatibility

Nao is unreleased. This intentionally replaces the earlier incomplete abstraction rather than
preserving a payload-only API or schema.
