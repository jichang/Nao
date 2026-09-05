# Immutable execution request

## Scope

- First incompatible version: next unreleased Nao package version.
- Affected packages and APIs: `Nao.Agents`, `EtclovgConfig`, and `EtclovgHarness.runAsync`.
- Affected files, streams, tables, or Orleans state: none.

## Breaking changes

- `EtclovgHarness.runAsync` now accepts an `ExecutionRequest` instead of a string input.
- `EtclovgConfig.Execution` and `EtclovgConfig.Scope` were removed.
- Execution input, authorization scope, agent and turn identity, conversation identity, sandbox budgets, pinned policy and dependency versions, and correlation now belong to the immutable request.
- The harness rejects requests whose `AgentId` differs from the supplied executable agent.

## Before upgrade

1. Identify every direct call to `EtclovgHarness.runAsync`.
2. Identify the trusted host source for `AuthorizationScope` and correlation data.
3. Inventory the effective sandbox, policy versions, and dependency versions at each call site.

## Migration

1. Construct `ExecutionRequest` from host-authenticated identity and the effective execution settings.
2. Move sandbox configuration from `EtclovgConfig.Execution` to `ExecutionRequest.Sandbox`.
3. Move event correlation and routing identity from `EtclovgConfig.Scope` to the request identity fields.
4. Pass the request as the final argument to `EtclovgHarness.runAsync`.

## Validation

- Run focused harness, end-to-end harness, and host integration tests.
- Verify mismatched request and executable agent identities fail before agent execution.
- Verify traces, metrics, journals, and audit entries retain the request execution ID.

## Rollback

Rollback requires restoring the prior callers and harness API together. No persisted data transformation or rollback is required.