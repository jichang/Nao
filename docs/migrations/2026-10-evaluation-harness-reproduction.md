# Evaluation harness reproduction

Evaluation execution now requires an explicit mode and runtime. Nao is unreleased, so the runner
contract changed directly.

## Required execution configuration

`EvalExecutionConfig.create` now additionally requires:

- `EvalExecutionMode.PinnedLive`, `PinnedMock`, or `RecordedReplay`;
- an `EvalRuntime` carrying the random seed and ID/clock behavior.

Replay fixtures must not be installed directly on `EtclovgConfig`. Recorded mode owns the
versioned fixture set and installs it only for the harness invocation. Live and mock modes reject
replay fixtures.

`EvalRun.create` now requires `EvalReproducibility` and `EvalRuntime`, ensuring run identity and
timestamps use the same declared runtime as case execution.

## Reproduction bundles

Create `EvalReproductionBundle` from the exact dataset, failed report, agent/evaluator identities,
and recorded fixtures. Serialize the bundle as JSON for CI artifacts. Local reproduction loads
the bundle, validates its dataset and fixtures, verifies the locally supplied executable
configuration, and executes through `EtclovgHarness.runAsync` in deterministic recorded mode.

There is no legacy execution-mode default or fallback to live dependencies.
