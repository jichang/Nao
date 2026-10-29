# Developer Tooling

`Nao.DeveloperTools` provides deterministic scaffolding, validation, inspection, evaluation, packaging, and operational-diagnostic commands. Every command writes one schema-v1 JSON document to standard output and exits with zero only when `succeeded` is true.

Run the repository version with:

```bash
dotnet run \
  --project src/Nao.DeveloperTools.Cli/Nao.DeveloperTools.Cli.fsproj \
  --configuration Release \
  -- <command>
```

The command result contains:

- `schemaVersion`;
- the canonical command name;
- `succeeded`;
- string-valued machine-readable data;
- structured issues with code, severity, optional location, and detail.

Automation should branch on `succeeded` and issue codes rather than parsing human prose.

## Scaffold an extension

Create an agent, tool, provider, evaluator, or common adapter:

```bash
dotnet run --project src/Nao.DeveloperTools.Cli/Nao.DeveloperTools.Cli.fsproj -- \
  scaffold adapter ExampleAdapter ./generated
```

Supported template names are `agent`, `tool`, `provider`, `evaluator`, and `adapter`. The scaffold contains a source project, source contract, MSTest project, package identity, and current Nao package version `1.0.0-alpha1`.

Because Nao is not published yet, repository contributors can bind the generated source project directly to a local contract project:

```bash
dotnet run --project src/Nao.DeveloperTools.Cli/Nao.DeveloperTools.Cli.fsproj -- \
  scaffold adapter ExampleAdapter ./generated \
  ./src/Nao.Extensions/Nao.Extensions.fsproj
```

The local project-reference argument is for unreleased repository development. Published extension scaffolds use the corresponding `Nao.Agents`, `Nao.Eval`, or `Nao.Extensions` package.

Test and package any generated extension through the same command surface:

```bash
dotnet run --project src/Nao.DeveloperTools.Cli/Nao.DeveloperTools.Cli.fsproj -- \
  extension test ./generated/tests/ExampleAdapter.Tests/ExampleAdapter.Tests.fsproj

dotnet run --project src/Nao.DeveloperTools.Cli/Nao.DeveloperTools.Cli.fsproj -- \
  extension package ./generated/src/ExampleAdapter/ExampleAdapter.fsproj
```

Packaging uses Release configuration and writes to an `artifacts` directory beside the source project.

## Validate platform configuration

```bash
dotnet run --project src/Nao.DeveloperTools.Cli/Nao.DeveloperTools.Cli.fsproj -- \
  config validate ./deploy/profiles/v1/single-node-production.json
```

The command uses `PlatformConfigurationJson` and `PlatformConfigurationValidator`, so startup and command-line validation share the same schema and safety rules. Failures identify the exact field and stable code, including permissive identity, anonymous administration, unrestricted tools, in-process untrusted execution, ephemeral production state, and invalid cluster membership.

## Diagnose adapter compatibility and dependencies

Create a local diagnostic manifest:

```json
{
  "schemaVersion": 1,
  "sdkVersion": "1.0.0",
  "protocolVersion": 1,
  "minimumHostVersion": "1.0.0",
  "maximumHostVersion": "1.0.0",
  "targetFramework": "net10.0",
  "dependencies": {
    "runtime": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  },
  "approvedDependencies": {
    "runtime": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  }
}
```

Then run:

```bash
dotnet run --project src/Nao.DeveloperTools.Cli/Nao.DeveloperTools.Cli.fsproj -- \
  adapter diagnose ./adapter.json
```

Compatibility uses `AdapterHostCompatibility.current` and the same `AdapterCompatibility` validation as registration. Dependency names must map to exact lowercase SHA-256 identities in the approved set. Mismatches return `compatibility_mismatch` or `dependency_rejected` before registration or execution.

## Lint and evaluate a local dataset

The local dataset format intentionally provides a small deterministic preflight, not a replacement for governed `Nao.Eval` datasets and runners:

```json
{
  "schemaVersion": 1,
  "cases": [
    {
      "id": "greeting",
      "input": "Say hello.",
      "expected": "hello",
      "actual": "hello"
    }
  ]
}
```

Lint identities and required fields:

```bash
dotnet run --project src/Nao.DeveloperTools.Cli/Nao.DeveloperTools.Cli.fsproj -- \
  dataset lint ./dataset.json
```

Run deterministic exact-match evaluation:

```bash
dotnet run --project src/Nao.DeveloperTools.Cli/Nao.DeveloperTools.Cli.fsproj -- \
  dataset evaluate ./dataset.json
```

Use the full `Nao.Eval` runner for authorization-aware agent execution, recorded replay, governed traces, release gates, production-derived review, and statistical evaluation.

## Inspect trace and replay exports

Trace export:

```json
{
  "schemaVersion": 1,
  "traces": [
    { "id": "trace-1", "success": true, "durationMs": 12 }
  ]
}
```

Replay export:

```json
{
  "schemaVersion": 1,
  "fixtures": [
    { "id": "provider-1", "replayable": true }
  ]
}
```

Inspect them with:

```bash
dotnet run --project src/Nao.DeveloperTools.Cli/Nao.DeveloperTools.Cli.fsproj -- \
  trace inspect ./traces.json

dotnet run --project src/Nao.DeveloperTools.Cli/Nao.DeveloperTools.Cli.fsproj -- \
  replay inspect ./replay.json
```

Trace inspection reports record count, successful count, total duration, and malformed durations. Replay inspection reports non-replayable dependencies explicitly.

## Plan migrations

Migration manifests are schema-v1 JSON:

```json
{
  "schemaVersion": 1,
  "id": "knowledge-v1",
  "sourceRevision": "source-v1",
  "targetRevision": "target-v1",
  "verifiedBackupId": "backup-1",
  "verifiedAt": "2026-01-01T00:00:00Z",
  "activeWriters": 0,
  "availableBytes": 2048,
  "requiredBytes": 1024,
  "steps": ["copy", "verify"]
}
```

Local planning reuses `MigrationPreflight` and `MigrationPlan`, including backup, writer-fencing, capacity, unique-step, and immutable-digest validation:

```bash
dotnet run --project src/Nao.DeveloperTools.Cli/Nao.DeveloperTools.Cli.fsproj -- \
  migration plan ./migration.json
```

`migration dry-run` additionally submits the immutable plan identity to the authenticated administration preview contract. It never executes migration steps.

## Authenticated live diagnostics

The standalone executable intentionally exposes local commands only. It does not accept a tenant, user, role, token, or credential argument that could become a self-asserted principal.

Host applications enable these live commands by constructing `DeveloperCommandServices` with the same `RuntimeAdministrationClient` used by other administration clients:

- `knowledge status <resource-id>`;
- `index diagnostics <index-id>`;
- `migration dry-run <manifest>`.

The administration client carries a host-authenticated `SecurityPrincipal`, enforces tenant equality, authorizes the resource-specific role, and invokes `IRuntimeAdministrationTransport`. Missing control-plane composition returns `authenticated_control_plane_required`; authorization failures return `authorization_denied`. Commands never bypass the transport to read stores directly.

Knowledge status addresses the `ingestion:<resource-id>` administrative resource. Index diagnostics addresses `index:<index-id>`. The host may add safe diagnostic metadata such as ingestion phase, indexed revision, searchable-record count, schema identity, or health state to `AdministrativeResourceSnapshot.Metadata`; the command emits those values alongside revision, disablement, and update time. Metadata must not contain secrets, source payloads, or personal data.

This embedding boundary lets a host add OIDC, workload identity, mTLS, or another supported authentication mechanism without adding credential parsing or permissive identities to the reusable CLI.

## Failure diagnostics

Common local issue codes include:

- `configuration_invalid`;
- platform configuration validator codes such as `permissive_identity`;
- `adapter_diagnostic_invalid`, `compatibility_mismatch`, and `dependency_rejected`;
- `dataset_invalid`, `duplicate_case_id`, `missing_actual_output`, and `exact_match_failed`;
- `diagnostic_export_invalid`, `negative_trace_duration`, and `non_replayable_fixture`;
- `migration_manifest_invalid`;
- `extension_workflow_failed`;
- `authenticated_control_plane_required`;
- `authorization_denied`;
- `control_plane_failure`.

Compatibility and dependency failures remain governed by the common [`Nao.Extensions` contracts](extension-adapter-sdk.md).
