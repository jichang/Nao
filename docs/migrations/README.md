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

Copy the [migration guide template](TEMPLATE.md) and replace every placeholder. Keep the validation section concrete: record the commands, tests, data checks, and rollback evidence used for the change.