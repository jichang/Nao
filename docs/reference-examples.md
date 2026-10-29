# Reference Examples

The repository ships executable, offline reference paths under `examples/`. They compile against
the same projects as the test suite and run in CI after the release build.

## Run all examples

```bash
dotnet run \
  --project examples/Nao.Examples/Nao.Examples.fsproj \
  --configuration Release \
  -- all

dotnet run \
  --project examples/Nao.OrleansExample/Nao.OrleansExample.fsproj \
  --configuration Release
```

The first command accepts an individual example name instead of `all`.

| Command | Demonstrates |
|---|---|
| `local-agent` | Authenticated, budgeted execution through the ETCLOVG harness |
| `governed-tool` | Explicit operation-scoped approval followed by redacted audit evidence |
| `grounded-rag` | Owner-bound ingestion, lexical retrieval, a grounded claim, and a chunk citation |
| `orleans` | Principal-carrying administration preview and optimistic mutation through the host transport seam |
| `replay-evaluation` | Deterministic recorded replay and an exact-match release gate |
| `formal-reasoning` | Validated Datalog facts/rules and a cited deterministic binding |

`Nao.OrleansExample` separately starts a real two-silo local Orleans test cluster and resolves
tenant-keyed grains through the generated runtime contracts.

## Shared host boundary

`Nao.Examples` constructs a multi-factor `SecurityPrincipal`, derives one tenant/workspace/session
authorization scope, and creates `AgentContext` values with `HarnessRequired`. It does not use the
test-only unrestricted context fixture. Agent execution uses a restricted sandbox with bounded
calls, tokens, output, no filesystem, and no network.

This identity is a deterministic local fixture, not authentication. Production hosts must derive
the principal from validated OIDC or workload identity and must never trust tenant, user, role, or
authentication-strength values supplied by an application payload.

## Local agent

The local greeter runs through `EtclovgHarness.runAsync` rather than invoking its implementation
directly. The example proves request/agent identity matching and a successful terminal response.

Production differences:

- use durable execution journals, audit, traces, metrics, and terminal-outcome storage;
- configure readiness, policy, constitution, and required execution isolation;
- resolve secret references outside the execution request;
- use process or container workers for untrusted code;
- retain bounded resource limits rather than `Unlimited`.

## Governed tool

The governed-tool path constructs a typed permission request, forces confirmation, accepts only an
operation-scoped grant, and records a redacted tool invocation in the audit log.

The local broker automatically approves the one expected calculator request. **That callback is a
demonstration fixture and is unsafe for production.** A production broker must authenticate the
approver, display exact action/resource/reason/expiry, enforce allowed grant scopes, support denial
and timeout, and write durable approval evidence before tool execution. Replace the in-memory audit
store with owner-partitioned durable storage.

## Grounded RAG

The grounded RAG path:

1. creates an owner-bound source, document, version, content unit, chunk, and index entry;
2. atomically saves the version bundle to the catalog;
3. normalizes and plans a lexical knowledge query;
4. executes retrieval through the `KnowledgeLexicalIndex` boundary;
5. constructs a claim whose citation marker is bound to the retrieved chunk identity.

The in-memory catalog and single-document lexical index are deterministic local substitutes. They
do not prove production ranking, persistence, incremental source checkpoints, parser isolation,
ACL behavior under concurrency, deletion propagation, or citation verification. Production must
use qualified storage/index adapters, source connectors, ingestion workers, retrieval admission,
context assembly, grounded-output verification, and the current deletion ledger.

## Orleans

The `orleans` command demonstrates the application-facing administration boundary. The client
requires a strong principal and matching tenant, previews the change, then applies the same
optimistic revision. The local transport records no network traffic and exists only so the example
runs without external infrastructure. A production transport must authenticate the request, bind
the supplied principal to the Orleans call context, obtain the tenant-keyed
`IRuntimeAdministrationGrain`, and preserve cancellation and transport errors.

`Nao.OrleansExample` uses `Orleans.TestingHost` to start two development silos and prove
tenant-keyed grain routing. TestingHost clustering and in-memory state are **not production
deployment choices**. Production must use a supported profile, at least three silos for clustered
production, configured ADO.NET membership/storage, workload identity, TLS, bounded admission,
health probes, rolling-drain procedures, and external durable state. Neither example opens an
administration endpoint, so copying it cannot create anonymous administration.

## Replay and evaluation gate

The replay example pins agent, prompt, protocol, provider, model, harness, and evaluator identities.
It runs a deterministic dataset through the ordinary harness in `RecordedReplay` mode and fails
when any exact-match case fails.

The sample has no provider or tool calls, so its fixture set is intentionally empty. Real recorded
runs must include exact provider/tool fixtures and fail closed when one is missing. Production
release gates also need governed datasets, archived reports, minimum sample sizes, segmented
thresholds, baseline promotion, and protected CI approval.

## Formal reasoning

The reasoning example validates a Datalog schema, facts, rules, query, citations, and resource
limits. A deterministic runner returns one binding whose derivation references both the rule and
premise fact.

The runner is a protocol fixture; it does not execute Souffle. **Never treat a caller-supplied row
as a proven conclusion.** Production must execute the compiled program in the isolated, pinned
reasoner process, validate engine identity and resource evidence, disable undeclared effects, and
adapt only verified rows into common reasoning results.

## CI guarantee

`scripts/validate-test-surface.py` requires both example projects to remain in `Nao.slnx`. The
build-and-test workflow executes every command plus the two-silo cluster. A source change that
breaks an example therefore fails before publication.
