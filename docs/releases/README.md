# Versioned release documentation

Every Nao package and image release has one immutable document named `<version>.md` in this directory. The document records behavior, breaking changes, migration guides, validation, rollback limits, known limitations, and supply-chain evidence for that exact version.

Nao has no stable release. [`1.0.0-alpha1.md`](1.0.0-alpha1.md) is the current prerelease draft and keeps its release date as `Unreleased`. The protected publication workflow rejects that value; the owner must replace it with the actual ISO date before publishing.

The matching schema-v1 [`release/1.0.0-alpha1.json`](https://github.com/jichang/Nao/blob/main/release/1.0.0-alpha1.json) manifest is the reviewed source for version, channel, compatibility baseline, changelog entries, release gates, artifact requirements, and completed roadmap capabilities. [`release/current.json`](https://github.com/jichang/Nao/blob/main/release/current.json) selects the active target. See the [release and compatibility process](../release-process.md).

## Breaking changes and migrations

Write `None` in the **Breaking changes** section only when the release has no incompatible public API or durable-format changes relative to its supported predecessor. Otherwise, every breaking change must link to a guide under [`docs/migrations`](../migrations/README.md). Publication validation rejects missing or non-migration links.

Because the first Nao release has no supported predecessor, changes made before `1.0.0` do not require compatibility machinery or an upgrade path from development snapshots. Durable development data may still require reset or explicit external transformation as described by the relevant migration guides.

## Validation

```bash
python3 scripts/validate-release-docs.py
python3 -m unittest scripts/test_validate_release_docs.py
```

For publication:

```bash
python3 scripts/validate-release-docs.py --version 1.0.0-alpha1 --publishing
```
