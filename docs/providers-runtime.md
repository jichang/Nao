# Providers and Distributed Runtime

Nao separates model-provider integration from agent behavior and offers Orleans as an optional distributed session runtime.

## Provider abstraction

The immutable `LlmProvider` capability record provides common completion behavior with an optional streaming function. Current adapters cover OpenAI-compatible services, Anthropic, DeepSeek, Kimi, Ollama, vLLM, and llama.cpp.

Applications should not select a provider by hard-coded model-name heuristics. A production control plane needs explicit capability metadata for:

- Chat and streaming
- Tool calling and structured output
- Vision, audio, and embeddings
- Context limits
- Usage reporting
- Privacy and data residency
- Quality, latency, and cost tiers
- Health, quota, and maintenance state

## Normalized semantics

Provider adapters should normalize common behavior while preserving provider-specific metadata:

- Authentication and authorization errors
- Quota and rate limits
- Timeout and overload
- Invalid requests and content rejection
- Finish reasons
- Partial-stream termination
- Tool calls and structured output
- Full, partial, aggregate, or unavailable token usage

Shared conformance tests should apply to every adapter.

## Routing and resilience

The current repository provides retry, circuit-breaker, and fallback primitives. A full model control plane remains planned:

- Capability- and policy-based routing
- Endpoint pools
- Per-provider/model concurrency
- Rate-limit-aware queues
- Tenant quotas and fair scheduling
- Cost, latency, quality, privacy, and residency constraints
- Controlled fallback and endpoint draining
- Optional request hedging with cost controls

Fallback must preserve mandatory security and capability constraints.

## Configuration and secrets

Provider configuration should be validated and versioned. Secret references—not raw credentials—flow through configuration. Activation should support health validation, last-known-good rollback, controlled reload, audit, and model allow/deny policies.

## Orleans runtime

`Nao.Runtime.Orleans` currently supplies:

- `SessionGrain` for persisted session execution
- `SessionDirectoryGrain` for session discovery
- `WorkspaceRegistry` for compiled workspace definitions
- Conversation/session persistence integrations
- Workspace selection and switching

```fsharp
let registry =
    WorkspaceRegistry.fromWorkspaces [
        ("team-a",
         { WorkspaceDefinitions.Empty with
             Agents = [ teamAAgent ]
             Tools = teamATools })
        ("team-b",
         { WorkspaceDefinitions.Empty with
             Agents = [ teamBAgent ]
             Tools = teamBTools })
    ]
```

Agents and tools are compiled registrations. The runtime does not dynamically execute JSON definitions or load arbitrary untrusted assemblies.

## Sessions and state

Runtime-owned state keeps agents stateless per call:

1. Resolve the session and workspace.
2. Load persisted conversation, grants, and runtime state.
3. Construct the execution context.
4. Run the harness and selected agent.
5. Persist a committed outcome.

Production completion requires explicit semantics for idempotency, concurrent turns, checkpoints, retries, deletion, archival, migration, and restart recovery.

## Tenancy status

Session records contain grouping metadata, but the organizational `GroupDirectoryGrain` described by older documentation is not present in current source. Group membership, roles, quotas, lifecycle, and authorization therefore remain planned rather than advertised current capabilities.

Tenant, user, workspace, session, and execution identity must eventually be enforced at grain entry points, storage partitions, retrieval boundaries, telemetry, and administration.

## Workspace lifecycle

A production workspace lifecycle needs:

- Immutable version identity
- Validation before registration
- Agent, tool, provider, policy, and schema compatibility
- Staged rollout, canary, promotion, rollback, and retirement
- Session pinning or explicit migration
- Safe handling of active sessions using older versions

Any future extension discovery must include signature, trust, compatibility, dependency, and isolation policies. Arbitrary assembly loading into the runtime process is not a safe plugin model.

## Cluster operations

Multi-silo production support requires tests and guidance for:

- Placement and tenant affinity
- Silo failure and rolling restart
- Network partition and storage degradation
- Admission control and backpressure
- Grain activation and queue saturation
- Scaling signals
- Recovery objectives
- Upgrade compatibility

## Administration

Provider, workspace, policy, quota, session, and health administration belongs to an authenticated control plane. Mutations require authorization, optimistic concurrency, dry-run validation, impact previews, propagation across silos, and audit evidence.

## Roadmap

See [Providers and distributed runtime](roadmap/04-providers-runtime.md) for detailed model control-plane, tenancy, durability, workspace lifecycle, cluster, and administration tasks.
