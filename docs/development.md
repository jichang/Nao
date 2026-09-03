# Development and Contributing

This guide describes repository conventions for extending the current Nao implementation.

## Build workflow

```bash
dotnet tool restore
dotnet paket install
dotnet build Nao.slnx
dotnet test Nao.slnx
```

Enable the pre-commit hook:

```bash
git config core.hooksPath .githooks
```

The [foundations roadmap](roadmap/00-foundations.md) tracks alignment of all source and test projects with the supported solution and CI surface.

## Package management

Nao uses Paket:

1. Add a dependency to `paket.dependencies`.
2. Add its package name to the relevant project's `paket.references`.
3. Run `dotnet paket install`.
4. Review lock-file changes and avoid unrelated upgrades.
5. Build and test every affected project.

Optional vendor integrations should not add dependencies to core contract packages.

## F# file organization

- Prefer one primary public type, interface, or discriminated union per file.
- Match the file name to the primary type.
- List files in explicit dependency order in the project file.
- Keep helper modules near the type they operate on when practical.
- Avoid reformatting unrelated code.

## Naming

- Types: PascalCase, for example `CompletionResult`
- Modules: PascalCase, often matching their primary type
- Functions: camelCase, for example `routeAsync`
- Discriminated-union cases: PascalCase
- Functional capabilities: domain nouns such as `Agent` and `LlmProvider`

## F# design

- Prefer discriminated unions for closed state and outcome models.
- Prefer immutable records for data contracts.
- Use `option` rather than null in F# APIs.
- Use `Task<'T>` for asynchronous interoperation.
- Make cancellation, timeout, identity, and ownership explicit at external boundaries.
- Use structured errors for expected platform outcomes.
- Add XML documentation to public APIs.

## Project boundaries

- `Nao.Agents` contains stable agent, tool, memory, harness, governance, and observability contracts.
- `Nao.Protocols` contains response protocols.
- `Nao.Persistence` contains storage implementations.
- `Nao.Providers` contains model-provider implementations.
- `Nao.Eval` contains evaluation infrastructure.
- `Nao.Runtime.Orleans` contains the optional distributed runtime.
- Vendor-specific knowledge, identity, telemetry, and reasoner integrations should be optional packages.

Do not create cyclic dependencies or make a vendor SDK a transitive requirement of the core.

## Testing

- Test project names use the `<ProjectName>.Tests` convention where applicable.
- Current test projects use MSTest.
- Keep one focused test file per feature or module.
- Use descriptive behavior-oriented test names.
- Cover success, invalid input, boundary, timeout, cancellation, retry, and persistence behavior.
- Add cross-tenant and fail-closed tests to protected operations.
- Use real adapter integration tests where practical and deterministic fixtures elsewhere.
- Keep evaluation datasets versioned separately from ordinary unit tests.

## Public contract changes

Before changing a public F# record, union, interface, Orleans state type, event, or persisted document:

- Determine source and binary compatibility effects.
- Determine serialized-state compatibility effects.
- Add versioning or migration behavior.
- Add compatibility fixtures.
- Update documentation and release notes.
- Follow the versioning policy established by the foundations roadmap.

## Security-sensitive changes

Changes involving permissions, identity, tools, execution environments, secrets, retrieval, or administration require:

- A threat-model update
- Fail-closed tests
- Tenant-boundary tests
- Audit and telemetry behavior
- Secret/redaction tests
- Resource-limit and cancellation behavior
- Documentation of unsupported guarantees

Configuration objects are not proof of enforcement. Do not describe process or container isolation as supported until the corresponding execution environment and escape-oriented tests exist.

## Roadmap workflow

The [AI platform roadmap](roadmap.md) is the planning source of truth.

- Use the stable task ID in issues, branches, commits, and pull requests.
- Keep a task unchecked while any required acceptance criterion remains incomplete.
- Update implementation, tests, documentation, compatibility, and roadmap state in the same change.
- Do not check a parent milestone until all required child tasks are complete.
- Record scope changes in the roadmap rather than silently changing the definition of done.

## Documentation

Conceptual documentation lives under `docs/`. The root README is a navigation page. Public API reference is generated from XML documentation comments through FSharp.Formatting.

When adding documentation:

- Add the page to the README and documentation index when it is a primary topic.
- Use relative Markdown links in source pages.
- Use generated `.html` links from the FSharp.Formatting documentation index.
- Keep examples compilable and add automated snippet coverage where practical.
- Distinguish current behavior from roadmap behavior.
- Avoid claiming a security, durability, or scalability guarantee based only on an interface or configuration type.

Every release and every pull request that changes a public capability must also review the [capability inventory](capabilities.md). Update its status, source/test evidence, limitations, ownership, roadmap mapping, and known gaps in the same change. The documentation workflow validates source links before generation and generated links afterward.

## Definition of done

Use the cross-cutting definition of done in the [roadmap](roadmap.md). Applicable work includes tests, documentation, compatibility, security, telemetry, persistence lifecycle, performance baselines, evaluation metadata, and upgrade/rollback guidance.
