# Evaluation and Observability

Quality, safety, latency, reliability, and cost are runtime properties. Nao's evaluation and observability foundations are intended to become one feedback loop from development through production.

## Current evaluation model

`Nao.Eval` provides evaluation cases, datasets, functional `Evaluator` records, runners, and reports. Built-in modules construct and compose evaluators without implementation classes.

```fsharp
// The host supplies an authenticated authorization scope and scoped agent services.
let execution = EvalExecutionConfig.create authorization createAgentContext
let config = EvalRunnerConfig.create execution

let dataset =
  EvalDataset.create
    (AuthorizationScope.identity authorization)
    "math"
    [ EvalCase.create "1" "2+2" "4" ]

let evaluator = ExactMatch.evaluator

let! report =
    EvalRunner.runDatasetAsync
        config
        evaluator
        agent
        dataset
        cancellationToken
```

Built-in evaluation approaches include exact match, containment, regular expressions, composites, verification, and LLM judges. Verification uses immutable `Judge` records; `VerificationJudge.fromJudge` adapts one to an `Evaluator`, while `Nao.Agents.LlmJudge.create` constructs the execution-trace judge without a class or interface implementation.

Every evaluator receives a mandatory `EvaluationContext` containing the host-authenticated `AuthorizationScope`, the producing execution's correlation, and the caller cancellation token. Composite evaluators forward the same context to every child. Runners reject dataset or run ownership that differs from the authenticated scope before creating agent services or executing work, including empty, parallel, multi-evaluator, and comparison runs. The harness replaces any caller-supplied agent principal with the host's scope principal.

Identity is not evaluation input. LLM and verification adapters use correlation and cancellation without appending principal roles, claims, service identity, or authentication strength to prompts or traces. Custom evaluators receive the full context for authorization and must keep it separate from model-visible case data and output.

## Evaluator selection

Prefer deterministic evaluation whenever the property can be checked directly:

- Schema and protocol validity
- Required or forbidden tool calls
- Terminal status
- Citation existence and source range
- Permission and policy behavior
- Artifact hashes and structured fields
- Resource and cost bounds

Use LLM judges for genuinely semantic properties. A release-blocking judge should be calibrated against human labels and checked for model-family, order, verbosity, and self-preference bias.

`SafetyDatasets.adversarialMultilingualV1` provides the first repository-owned output-safety
corpus. Its cases include explicit safe or unsafe human labels, normalized language tags,
adversarial techniques, and tags for segmentation. The corpus spans benign controls, direct
requests, instruction overrides, role play, obfuscation, and cross-language transfer in more
than eight locales. `SafetyDataset` revisions have mandatory versions and deterministic SHA-256
content hashes; changing any governed case field creates a different corpus identity.

`SafetyDataset.evaluateAsync` runs every candidate output through complete constitution
evaluation using an authenticated scope. It retains per-rule constitution evidence with each
classification, so probabilistic evaluators can be calibrated without introducing a separate
safety execution path. The built-in text is synthetic and contains no production-derived data.

`SafetyMetrics.measure` compares those classifications with the corpus labels. It produces a
confusion matrix, accuracy, false-positive rate, and false-negative rate for the complete corpus
and separately by language, adversarial technique, and tag. Measurements retain the dataset
name, version, and content hash. Missing, duplicated, or unknown case results are rejected rather
than producing a success-shaped partial report. A rate is `None` when its ground-truth class is
absent, keeping “not measurable” distinct from a measured zero-error rate.

## Reproducibility

A reproducible evaluation records:

- Dataset revision and content hash
- Agent, prompt, response protocol, and tool versions
- Provider, model, and generation settings
- Harness, policy, constitution, and workspace versions
- Knowledge-index and embedding versions
- Evaluator and judge versions
- Random seeds, clocks, IDs, and scheduling controls where applicable
- All fixtures needed for replay

Production, test, and replay should use the same harness path. Recorded provider and tool fixtures should allow a failed case to run without live network dependencies.

## Release gates

Evaluation gates can cover:

- Functional success rate
- Quality and groundedness
- Safety and policy compliance
- Retrieval recall and citation correctness
- Latency and reliability
- Tokens and monetary cost
- Tool selection and side-effect behavior

Gates should support absolute thresholds, baseline deltas, minimum sample sizes, confidence intervals, segmentation, and explicit baseline promotion.

## Continuous evaluation

The target operational loop is:

```text
Production traces and feedback
  → privacy-aware sampling and redaction
  → reviewed evaluation cases
  → deterministic or recorded replay
  → regression analysis
  → release gate or rollback
  → permanent regression coverage
```

Production-derived data requires consent, tenant isolation, redaction, retention, and review before becoming a dataset. `TurnStore` and `FeedbackStore` require the complete authenticated scope for access, deletion, and strict retention cutoff. Their durable payloads contain no authorization identity or authentication evidence, and deleting either source does not erase already-derived datasets governed by their own policy.

`EvalDataset`, `EvalRun`, `EvalResult`, and `EvalReport` carry stable identity and an `AuthorizationIdentity` owner: tenant, user, selected group, workspace, and optional session. This is persistable ownership metadata, not an authorization capability; it contains no principal claims, roles, service identity, or authentication-strength evidence. Ownership uses exact lineage matching: a session-owned dataset is not implicitly shared with another session or a workspace-wide scope.

Every operation on `EvalArchives.inMemory` and `EvalArchives.file` requires an authenticated `AuthorizationScope`, not an owner string. Reads and counted owner/strict timestamp-cutoff deletions derive their owner from that scope; writes reject mismatched dataset/report ownership and reports reject child results from another owner, dataset, or run. Tenant, user, group, workspace, and session substitutions cannot access another scope's data even when dataset, run, and execution IDs match.

The JSONL archive persists ownership metadata and deletion tombstones without serializing authentication evidence. Each archive instance serializes mutation and persistence before updating its live state, so failed writes or deletions do not change visible data. Reopening the archive replays the committed events, including deletions. The file adapter is a single-writer store; hosts must not open independent concurrent writers to the same path. Tombstones do not physically erase earlier payloads. This unreleased contract replaces the owner-string format directly; no legacy reader or compatibility API is provided. ADO.NET evaluation persistence remains planned and should use a dedicated evaluation persistence package rather than expand the core evaluation dependency surface.

## Current observability model

Nao provides parent/child traces, metrics, token usage, execution journals, and resilience primitives.

`TraceStore` supports counted deletion for one `AgentId` and strict retention purge by `StartedAt`. In-memory traces are removed directly; file and ADO.NET stores persist tombstones so deleted traces remain absent after replay. These tombstones are logical deletion, not physical erasure of the underlying append-only payloads.

`ExecutionJournal` requires stable record, session-workflow, and turn identity and supports counted owner/cutoff deletion. Retention must preserve records while compensation is still possible; coordinated session destruction deletes the session journal before clearing runtime identity.

```fsharp
let tracer = Tracer.inMemory ()
let root = tracer.StartTrace "user-request"
let child = tracer.StartSpan root "tool.invoke"
tracer.EndSpan child SpanStatus.Ok
```

Metrics capture actual LLM calls, latency, and provider-reported usage. Nao preserves aggregate token usage when that is all a provider reports and does not invent an input/output split.

```fsharp
let metrics = InMemory.metrics ()
let pricing =
    { InputCostPer1K = inputPrice
      OutputCostPer1K = outputPrice }

let cost = metrics.EstimateCost pricing
let summary = metrics.GetMetrics()
```

Pricing remains host-owned because model prices vary by deployment and change independently of the framework.

## Resilience telemetry

Retries, circuit breakers, fallback, provider routing, and queueing must be visible. Operational signals should distinguish:

- Provider authentication or quota failures
- Rate limiting and overload
- Policy denial
- Resource exhaustion
- Storage degradation
- Tool failure
- Model protocol failure
- User cancellation and deadline expiry

A fallback should never silently weaken privacy, residency, capability, or authorization requirements.

## Target telemetry model

Every operation should carry bounded-cardinality identifiers for:

- Tenant and subject
- Workspace and session
- Turn and execution
- Agent and tool
- Provider and model
- Knowledge index and reasoner where applicable
- Trace, parent, causation, and attempt

One user turn should be traceable across Orleans, the harness, orchestration, providers, tools, retrieval, persistence, policy, audit, and evaluation.

## External telemetry

Standard OpenTelemetry trace and metric export, structured-log correlation, OTLP transport, Prometheus exposure, dashboards, and operational alerts remain roadmap work.

Exporters require bounded queues, batching, retry/drop policy, graceful shutdown, and protection against telemetry failures blocking execution.

## Privacy

Operational metadata and captured content need separate controls. Telemetry must:

- Redact secrets and protected data before export
- Avoid user content in metric labels
- Apply tenant retention and regional policy
- Keep sampling decisions trace-consistent
- Restrict audit mutation
- Record redaction without retaining removed values

## Roadmap

See [Evaluation and observability](roadmap/03-evaluation-observability.md) for versioned datasets, replay bundles, evaluator calibration, CI gates, drift detection, OpenTelemetry, privacy, health, dashboards, and diagnostics.
