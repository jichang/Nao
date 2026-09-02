# Evaluation and Observability

Quality, safety, latency, reliability, and cost are runtime properties. Nao's evaluation and observability foundations are intended to become one feedback loop from development through production.

## Current evaluation model

`Nao.Eval` provides evaluation cases, datasets, evaluators, runners, and reports.

```fsharp
let dataset =
    { Name = "math"
      Cases = [ EvalCase.create "1" "2+2" (Some "4") ] }

let! report =
    EvalRunner.runDatasetAsync
        evaluator
        agent
        dataset
        EvalRunnerConfig.Default
```

Built-in evaluation approaches include exact match, containment, regular expressions, composites, verification, and LLM judges.

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

Production-derived data requires consent, tenant isolation, redaction, retention, and review before becoming a dataset.

## Current observability model

Nao provides parent/child traces, metrics, token usage, execution journals, and resilience primitives.

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
