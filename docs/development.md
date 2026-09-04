# Development and Contributing

This guide describes repository conventions for extending the current Nao implementation.

## Build workflow

```bash
dotnet tool restore
dotnet paket install
dotnet fantomas --check .
dotnet build Nao.slnx
dotnet test Nao.slnx
```

Format all F# source files before committing:

```bash
dotnet fantomas .
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

Dependencies point inward from runtimes and adapters to core contracts:

| Project | Role | Allowed project dependencies |
|---|---|---|
| `Nao.Protocols` | Core response contracts | None |
| `Nao.Agents` | Core agent, tool, memory, governance, and observability contracts and execution | `Nao.Protocols` |
| `Nao.Eval` | Evaluation implementation | `Nao.Agents` |
| `Nao.Persistence.Infrastructure` | Provider-neutral ADO.NET, serialization, events, and backend selection contracts | `Nao.Agents` |
| `Nao.Persistence.Memory` | Memory storage implementations | `Nao.Agents`, `Nao.Persistence.Infrastructure` |
| `Nao.Persistence.Observability` | Trace, metric, audit, and journal implementations | `Nao.Agents`, `Nao.Persistence.Infrastructure` |
| `Nao.Persistence.Feedback` | Feedback and turn storage implementations | `Nao.Agents`, `Nao.Persistence.Infrastructure` |
| `Nao.Persistence` | Opt-in persistence composition | `Nao.Agents`, all persistence capability packages |
| `Nao.Providers.OpenAICompatible` | OpenAI-compatible model adapters | `Nao.Agents` |
| `Nao.Providers.Anthropic` | Anthropic Messages adapter | `Nao.Agents` |
| `Nao.Providers.Ollama` | Ollama adapter | `Nao.Agents`, `Nao.Providers.OpenAICompatible` |
| `Nao.Providers` | Opt-in provider selection and composition | `Nao.Agents`, all provider adapter packages |
| `Nao.Runtime.Orleans` | Optional distributed runtime | `Nao.Agents` |
| `Nao.Runtime.Orleans.Codegen` | Runtime build-time code generation | `Nao.Runtime.Orleans` |

Core projects must not reference runtime, persistence, database, ontology, vector-store, or model-vendor packages. Adapter and implementation projects may depend on core projects but never the reverse. Runtime projects compose core and adapters; an adapter must not depend on a runtime. Project-reference cycles are forbidden.

Create a separate package when a capability introduces a replaceable backend, optional third-party dependency, independent release or security cadence, or deployment-specific runtime. Extend the owning package when the change uses its existing dependencies, lifecycle, and release cadence and does not create a new replaceable boundary.

Optional adapter packages use `Nao.<Domain>.<Adapter>` names. Reserved families are `Nao.Knowledge.<Adapter>`, `Nao.Telemetry.<Adapter>`, `Nao.Identity.<Adapter>`, `Nao.Vector.<Adapter>`, `Nao.Graph.<Adapter>`, and `Nao.Reasoning.<Adapter>`. Vendor-specific packages place the vendor or protocol in `<Adapter>` and remain outside core.

Run the architecture policy and its rejection tests locally with:

```bash
python3 scripts/validate-project-dependencies.py
python3 -m unittest scripts/test_validate_project_dependencies.py
```

Adding a production project or changing an allowed edge requires updating the validator and this table in the same change. CI rejects unregistered projects, forbidden edges, and known runtime, database, vector-store, or model-vendor packages in core.

`Nao.Providers.Tests` owns conformance coverage for the provider composition and adapter packages. `Nao.Persistence.Tests` owns backend parity, lifecycle, and isolation coverage for the persistence composition and capability packages.

## Runtime and API support

Nao currently supports .NET 10 and the F# toolchain selected by that SDK. CI follows the latest .NET 10 feature band and centrally pins `FSharp.Core`; dependency lock updates are reviewed explicitly. The supported SDK and F# core are reviewed quarterly and before each minor release. Moving to a new .NET major or F# language generation requires a documented compatibility decision, a clean solution build, the full supported test surface, and migration notes.

Public APIs are stable only when documented as supported. Experimental APIs use a `.Experimental` namespace or an `Experimental` module and may change or be removed in any minor release without a compatibility shim. Experimental durable formats still require an explicit schema version and fail-fast diagnostics; they must not be silently read as stable formats.

## Testing

- Test project names use the `<ProjectName>.Tests` convention where applicable.
- Current test projects use MSTest.
- Keep one focused test file per feature or module.
- Use descriptive behavior-oriented test names.
- Cover success, invalid input, boundary, timeout, cancellation, retry, and persistence behavior.
- Add cross-tenant and fail-closed tests to protected operations.
- Use real adapter integration tests where practical and deterministic fixtures elsewhere.
- Keep evaluation datasets versioned separately from ordinary unit tests.

The supported solution uses these primary test categories:

| Category | Projects | Purpose |
|---|---|---|
| Unit | `Nao.Agents.Tests`, `Nao.Protocols.Tests` | In-process contract and behavior checks |
| Integration | `Nao.Persistence.Tests`, `Nao.Providers.Tests`, `Nao.Runtime.Orleans.Tests` | Deterministic adapter, storage, and runtime checks |
| End-to-end | `Nao.E2E.Tests` | Full harness and orchestration flows using local fakes |
| Evaluation | `Nao.Eval.Tests` | Evaluation runner, metric, and report behavior |
| Security | MSTest `Security` category within an owning project | Permission, policy, isolation, and tenant-boundary checks |
| Performance | MSTest `Performance` category within an owning project | Explicit benchmark or regression-threshold checks |

All currently supported projects are deterministic and run with `dotnet test Nao.slnx`. Tests that require an external database, container, network service, or model must use the corresponding MSTest category (`ExternalDatabase`, `Container`, `Network`, or `ExternalModel`) and an explicit opt-in CI job before entering the supported solution. No such tests currently exist.

The CI workflow validates solution membership, then publishes TRX and Cobertura files under category and project directories. Run the same inventory check locally with:

```bash
python3 scripts/validate-test-surface.py
```

`Nao.Runtime.Orleans.Tests` owns runtime and generated-code integration coverage. `Nao.Assistant.Tests` remains outside `Nao.slnx` because it is an application-owned cross-solution integration project for the sibling Assistant solution. There is no current `Nao.Loader.Tests` project; generated artifacts with that name do not define a supported test surface.

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
