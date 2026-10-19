# Evaluator quality boundary

Nao is unreleased, so evaluator construction and context changed directly.

## Evaluator authority and reliability

`Evaluator.create` was removed. Implementations must use:

- `Evaluator.deterministic` for proof-like validation;
- `Evaluator.probabilistic` for model or statistical assessments.

Both constructors require release-blocking status and explicit failure semantics. Every
`EvaluationContext` now includes the complete `ExecutionResult`, enabling deterministic
validation of terminal state, traces, artifacts, policies, and resource usage. `EvalResult`
records evaluator authority and marks probabilistic rationale as untrusted evidence.

## Deterministic execution validation

`DeterministicExecution` validates structured JSON, a versioned caller-owned schema, expected
properties, tool counts/order/argument hashes, artifacts, citations, forbidden actions, budgets,
and terminal states. Missing evidence fails closed. Artifact metadata expectations fail until the
artifact contract supplies corresponding metadata evidence.

## High-risk judge gates

`HighRiskEvaluation` requires at least one deterministic validator and one probabilistic judge.
Any deterministic failure returns immediately without invoking judges. Judge aggregation supports
unanimous, majority, and minimum-average-score policies. Favorable probabilistic evidence cannot
override deterministic failure.

## Judge calibration

`JudgeCalibration` measures accuracy against human labels, Cohen's kappa, repeated-rating
stability, position bias, verbosity bias, candidate model-family pass rates, and self-preference
bias. `JudgeUpgradeQualification` compares a candidate report with the prior judge and rejects
insufficient samples, weak agreement/stability, excessive bias, or accuracy regression.

Judge rationale remains untrusted evidence. It is never treated as reference truth.
