# API documentation and lifecycle

Nao publishes conceptual guides and generated API references from the same source revision. The documentation build runs in strict mode and fails on malformed XML comments, unresolved projects, broken source links, broken generated links or anchors, and unavailable external links.

## Public API reference

Every packable library is discovered from the supported solution and emitted under `reference/`. FSharp.Formatting reads the Release assembly, compiler-generated XML documentation, package metadata, and source locations. Executable hosts and test projects are intentionally excluded.

The reusable developer-command engine is therefore a library in `Nao.DeveloperTools`; `Nao.DeveloperTools.Cli` is only its non-packable process host. Similar worker executables keep their contracts in the owning library.

Build and validate the reference locally:

```bash
dotnet build Nao.slnx --configuration Release
dotnet fsdocs build \
  --strict \
  --clean \
  --output ./site \
  --properties Configuration=Release \
  --parameters root /Nao/
python3 scripts/validate-docs.py --site ./site --external-links
```

The agreed warning threshold is zero. `--strict` converts API-generation problems into failures. F# compiler warning 3390 is enabled repository-wide to validate XML structure, parameter names, and references. Public declarations use `///` comments; generated references enumerate every public member even when the compiler does not provide a missing-comment warning.

## Conceptual documentation

The documentation index publishes:

- conceptual architecture and platform ownership;
- the security threat model and governed tool boundaries;
- deployment, operations, recovery, administration, SLO, and supply-chain guidance;
- memory, knowledge, retrieval, grounding, and citation behavior;
- evaluation, replay, observability, and release gates;
- extension lifecycle, conformance, trust, and developer tooling.

Capability status remains authoritative. A generated type page proves that an API exists; it does not prove durability, security, scale, or support beyond the boundary documented in the capability inventory.

## Stability markers

Nao is unreleased, so supported public APIs may still change in place before `1.0.0`. No compatibility alias or deprecation period is required for those pre-release changes.

After a stable release:

- supported APIs have no experimental or obsolete marker;
- experimental APIs use `System.Diagnostics.CodeAnalysis.ExperimentalAttribute` with a diagnostic identifier and are listed as **Experimental** in the capability inventory;
- deprecated APIs use `System.ObsoleteAttribute` with an actionable replacement and, when removal is planned, a release target;
- experimental or obsolete status must appear in conceptual guidance, generated reference metadata, and release notes;
- durable-format changes require a migration guide regardless of API stability.

Nao currently exposes no API marked experimental or obsolete. Domain values such as `ProviderLifecycle.Deprecated` describe an external provider deployment; they do not deprecate the Nao API that represents that state.

## Versioned documentation and migrations

Each release has an immutable document under [`docs/releases`](releases/README.md). A release with breaking changes must link every affected contract to a guide under [`docs/migrations`](migrations/README.md). The protected package publication workflow validates the exact version document and refuses an `Unreleased` date, template text, empty required sections, or breaking changes without migration links.

The `1.0.0-alpha1` document is the current prerelease draft. Documentation for the default branch describes the active target; published release notes identify the immutable validation and supply-chain evidence for a released version.

## Tested snippets

Supported F# snippets live in [`Nao.DocumentationSnippets`](tested-snippets.md), compile in the solution, and execute in CI. Conceptual Markdown links to those sources instead of copying uncompiled code. Shell and JSON blocks are operator commands or data shapes and remain covered by their owning command, schema, deployment, or policy tests.
