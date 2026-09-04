# Migration guides

Nao is under active development and has not declared a stable public release. Public APIs and durable schemas may therefore change without an embedded compatibility layer.

## Policy

- Prefer the simplest current contract over dual reads, fallback deserialization, compatibility shims, or legacy branches.
- Durable formats carry a schema version and reject unsupported versions before mutation.
- Every breaking API or durable-format change includes a migration guide in this directory.
- A guide must identify the affected contracts and data, the first incompatible version, export or backup steps, the required external transformation or reset, validation after upgrade, and rollback limits.
- Never infer ownership, identity, authorization scope, or security-sensitive defaults while transforming old data.
- Keep migration tools outside runtime libraries unless a released support commitment explicitly requires an in-process migration path.

After Nao declares a stable release, semantic-versioning and support commitments determine whether a change needs a major version, a deprecation period, or a maintained migration tool. Backward-compatible code is a deliberate product commitment, not the default implementation strategy.

## Guide template

```markdown
# <version or date> <change name>

## Scope

- First incompatible version:
- Affected packages and APIs:
- Affected files, streams, tables, or Orleans state:

## Breaking changes

- <old contract> becomes <new contract>.

## Before upgrade

1. Stop writers.
2. Back up or export affected data.
3. Validate that the backup can be restored.

## Migration

1. Transform data externally with explicit identity and ownership mappings, or delete development data when retention is unnecessary.
2. Deploy the new version.
3. Validate current-schema reads and writes.

## Rollback

State whether rollback is possible after new-format writes and how to restore the backup.
```