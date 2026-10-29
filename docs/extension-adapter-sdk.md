# Extension and Adapter SDK

`Nao.Extensions` defines the common host lifecycle for Nao extensions without replacing the richer contracts owned by each capability package. Adapter authors implement the domain contract they need and expose its construction through a compiled registration.

The SDK covers these categories:

- tools;
- model and embedding providers;
- persistence and index storage;
- document and knowledge parsers;
- source connectors;
- retrievers;
- telemetry exporters and stores;
- identity integrations;
- formal reasoners.

## Contract layering

Category-specific contracts remain authoritative for actual work:

| Category | Example owning contracts | SDK responsibility |
|---|---|---|
| Tool | `Tool` and `ExecutableTool` in `Nao.Agents` | Registration, configuration, capabilities, startup, health, and shutdown |
| Provider | `LlmProvider`, `EmbeddingProvider`, and provider endpoints | Common lifecycle around provider-specific request and result semantics |
| Storage | Persistence stores and knowledge indexes | Lifecycle and compatibility, not a lowest-common-denominator storage API |
| Parser | `KnowledgeParser` and document format readers | Lifecycle around typed parsing contracts |
| Connector | Pull, push, snapshot, database, and object-storage source connectors | Lifecycle around source-specific ingestion |
| Retriever | Knowledge retrieval and comparison retrievers | Lifecycle around retrieval-specific ranking and evidence |
| Telemetry | Trace, metric, audit, journal, and exporter contracts | Lifecycle around telemetry-specific delivery behavior |
| Identity | Host authentication, authorization, and identity adapter contracts | Lifecycle only; the host still owns trust roots and transport authentication |
| Reasoner | `ReasoningEndpoint`, `OwlReasoningEndpoint`, and engine-specific endpoints | Lifecycle around explicit formal semantics and engine identities |

This separation avoids weakening domain invariants merely to fit a generic extension interface.

## Compiled registration

Trusted compiled registration is the primary integration path:

1. Create `AdapterCompatibility` and an `AdapterDescriptor`.
2. Provide configuration validation and `StartAsync` in a `CompiledAdapterRegistration`.
3. Register it in an `AdapterRegistry` during host composition.
4. Resolve or activate it by category and canonical adapter identity.
5. Keep the returned `AdapterRuntime` for health checks and graceful shutdown.

Registration validates compatibility before changing the registry. Duplicate registrations fail, and an incompatible adapter never becomes resolvable or routable.

The descriptor supplies:

- canonical identity and display metadata;
- package version and adapter category;
- SDK, protocol, host-version, and target-framework compatibility;
- declared capability names;
- a positive shutdown timeout.

The current host contract is SDK `1.0.0`, protocol v1, host `1.0.0`, and `net10.0`. Nao is unreleased, so this contract may be improved in place rather than carrying compatibility shims.

## Configuration and secrets

`AdapterConfiguration` separates ordinary values from `AdapterSecretReference` values. Configuration rejects common secret-bearing names such as tokens, passwords, credentials, private keys, and API keys. A secret reference names a host-owned provider and key; it does not contain secret material.

Adapters must validate all required values before startup and return `InvalidConfiguration` evidence. The host decides how authorized references are resolved and must not copy resolved secrets into descriptors, diagnostics, health details, or failure messages.

## Lifecycle and failures

Lifecycle operations are cancellation-aware:

- registration performs compatibility checks;
- activation validates configuration and starts the adapter;
- health returns `Ready`, `Degraded`, or `Unhealthy`;
- shutdown must complete within the descriptor timeout and be idempotent.

Expected failures use `AdapterFailure`, including a structured code, canonical message, and retryability. The registry normalizes adapter-thrown configuration, startup, health, and shutdown exceptions into the common failure contract. Caller cancellation is never converted into an adapter failure.

Health and failure details must be safe for operator diagnostics. They must not include credentials, source payloads, personal data, or other secret material.

## Public conformance suite

Third-party adapter packages can reference `Nao.Extensions` and call `AdapterConformance.runAsync`; no private Nao test assembly is required. Supply:

- the target host compatibility;
- an activation context;
- the compiled registration;
- one valid configuration;
- at least one invalid configuration;
- a cancellation token.

The report covers compatibility, valid configuration acceptance, invalid configuration rejection, activation, ready health, shutdown, and idempotent shutdown. Run the same suite for every category implemented by a multi-category package. Package-specific tests must additionally cover the domain contract, authorization, tenant isolation, resource limits, and real external integration where applicable.

## Dynamic adapters

Nao does not load arbitrary assemblies into the runtime process. The SDK exposes admission planning only; a host must execute an admitted dynamic adapter behind a process or container boundary.

A `DynamicAdapterManifest` binds:

- the ordinary adapter descriptor;
- the lowercase SHA-256 artifact identity;
- signer and signature algorithm;
- exact dependency name and SHA-256 pairs;
- process or container isolation.

`DynamicAdapterAdmission.validateAsync` requires host compatibility, a trusted signer, an allowed category, exact approved dependency identities, an allowed isolation boundary, and successful verification by a host-supplied `AdapterSignatureVerifier`. Admission returns a plan, not executable code. The host remains responsible for artifact acquisition, signature trust roots, sandbox construction, protocol mediation, secret delivery, resource limits, network policy, monitoring, and termination.

An implementation must not treat admission as permission to use reflection, `Assembly.Load`, an application load context, or another in-process plugin mechanism.

## Package design

`Nao.Extensions` is dependency-light and does not reference domain packages. An adapter package references `Nao.Extensions` plus the capability package whose contract it implements. Core capability packages do not reference adapters, and adapters do not depend on runtime hosts.

Run the focused public SDK evidence with:

```bash
dotnet test tests/Nao.Extensions.Tests/Nao.Extensions.Tests.fsproj --configuration Release
```

The repository tests run all nine categories through the same public conformance entry point and cover compatibility rejection, secret separation, duplicate and missing registration, lifecycle exception normalization, caller cancellation, and dynamic trust, dependency, signature, and isolation policy.
