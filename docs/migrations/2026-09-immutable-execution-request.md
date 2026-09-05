# Immutable execution contracts

## Scope

- First incompatible version: next unreleased Nao package version.
- Affected packages and APIs: `Nao.Agents`, `EtclovgConfig`, `EtclovgHarness.runAsync`, and its result consumers.
- Affected files, streams, tables, or Orleans state: none.

## Breaking changes

- `EtclovgHarness.runAsync` now accepts an `ExecutionRequest` instead of a string input.
- `EtclovgConfig.Execution` and `EtclovgConfig.Scope` were removed.
- Execution input, authorization scope, agent and turn identity, conversation identity, sandbox budgets, pinned policy and dependency versions, and correlation now belong to the immutable request.
- The harness rejects requests whose `AgentId` differs from the supplied executable agent.
- `EtclovgResult` was replaced by `ExecutionResult`.
- `Success`, `Response`, and `HarnessError` were replaced by the single `Status` outcome and grouped `Outputs`.
- Trace, metrics, judgement, regression, and audit evidence now belong to `Evidence`.
- Policy and constitution outcomes now belong to `PolicyDecisions`.
- `AgentContextData`, `GetData`, and `PublishData` were replaced by the core `Artifact`, `GetArtifacts`, and `PublishArtifact`.
- `AgentArtifact` and `ExecutionArtifact` were unified as the single core `Artifact` type.
- Successful `AgentContext.PublishArtifact` calls are returned as identified execution artifacts.
- Artifact identity is assigned by `Artifact.create` before publication and remains unchanged in events, transcripts, and execution results.
- Turn records, conversation messages, Orleans message state, and version-1 conversation files now use `Artifacts` instead of `Data`.

## Before upgrade

1. Identify every direct call to `EtclovgHarness.runAsync`.
2. Identify every consumer of `EtclovgResult` and its flat fields.
3. Identify the trusted host source for `AuthorizationScope` and correlation data.
4. Inventory the effective sandbox, policy versions, and dependency versions at each call site.

## Migration

1. Construct `ExecutionRequest` from host-authenticated identity and the effective execution settings.
2. Move sandbox configuration from `EtclovgConfig.Execution` to `ExecutionRequest.Sandbox`.
3. Move event correlation and routing identity from `EtclovgConfig.Scope` to the request identity fields.
4. Pass the request as the final argument to `EtclovgHarness.runAsync`.
5. Pattern-match `ExecutionResult.Status` and read response or artifacts from `Outputs`.
6. Read observability and verification data from `Evidence`, and governance outcomes from `PolicyDecisions`.
7. Rename agent artifact callbacks and transcript fields; migrate or remove development conversation files that contain the old `Data` property.

## Validation

- Run focused harness, end-to-end harness, and host integration tests.
- Verify mismatched request and executable agent identities fail before agent execution.
- Verify traces, metrics, journals, and audit entries retain the request execution ID.
- Verify each published artifact appears once in `Outputs.Artifacts` after its host callback succeeds, with the producer-assigned ID unchanged.
- Verify every expected terminal status maps to a structured platform failure at host boundaries.

## Rollback

Rollback requires restoring the prior callers and harness API together. No persisted data transformation or rollback is required.