# Versioned release documentation

Every Nao package and image release has one immutable document named `<version>.md` in this directory. The document records behavior, breaking changes, migration guides, validation, rollback limits, known limitations, and supply-chain evidence for that exact version.

Nao has no stable release. Main maintains the reviewed [development notes](template.md)
and uses `1.0.0-dev`, never published. A valid pushed `v<SemVer>` tag selects a release
version independently of schemas and without changing source-code version literals.

The [development manifest](https://github.com/jichang/Nao/blob/main/release/template.json) contains the reviewed
compatibility baseline, changes, gates, artifacts, and completed capabilities.
[`release/current.json`](https://github.com/jichang/Nao/blob/main/release/current.json) selects this template on main.
The workflow generates versioned notes and a manifest, resolves the date, and selects
the generated manifest only in its checkout. Historical records are never overwritten.
Generated records are retained with release artifacts, not committed back to main.
See the [release and compatibility process](../release-process.md).

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
python3 scripts/validate-release-docs.py --version "$(python3 scripts/release.py version)" --publishing
```
