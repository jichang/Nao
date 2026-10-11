# Evaluation and Observability

Quality, safety, latency, reliability, and cost are runtime properties. Nao's evaluation and observability foundations are intended to become one feedback loop from development through production.

## Current evaluation model

`Nao.Eval` provides evaluation cases, datasets, functional `Evaluator` records, runners, and reports. Built-in modules construct and compose evaluators without implementation classes.

```fsharp
// Evaluations use the same explicit harness and sandbox policy as production execution.
let execution =
    EvalExecutionConfig.create authorization createAgentContext harnessConfig sandboxConfig
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

`KnowledgeEvaluationDataset` is the versioned source of truth for knowledge quality evaluation.
Each case records a query, relevant document and chunk identities, expected facts bound to
supporting relevant chunks, prohibited document or chunk results with reasons, and required source,
format, language, tenant-policy, and query-type dimensions. Schema version, positive dataset
revision, and every governed field produce a deterministic SHA-256 content hash. DTO-based JSON
fixtures retain and verify that hash when loaded.

`KnowledgeHybridRetrievalEvaluator` compares lexical, vector, and hybrid ranked retrieval against
the exact same `KnowledgeEvaluationDataset`. Each strategy executes exactly once per case at one
cutoff. Duplicate, null, or over-cutoff rankings fail closed. Evidence retains per-case rankings
and macro-averaged recall@k, precision@k, mean reciprocal rank, and nDCG. Retrieval implementations
return explicit non-negative USD cost, while the evaluator independently measures invocation
latency. Per-strategy evidence aggregates mean and nearest-rank p95 latency plus total cost.
Every strategy also exposes the same metrics independently for each observed source, format,
language, tenant policy, and query type.

The hybrid quality gate compares each hybrid metric with the better single-strategy value.
Acceptance requires either a material improvement in at least one metric without a regression
beyond tolerance, or parity across every metric with an explicit justification. Parity cannot
silently pass without that rationale.

`KnowledgeGroundedGenerationEvaluator` measures generation behavior against the same dataset.
Context relevance compares assembled chunk identities with case relevance judgments. Answer
faithfulness reuses the grounded pipeline's semantic claim-support verdicts instead of invoking a
second judge. Citation correctness requires each citation occurrence both to be selected by that
support evaluator as entailing its claim and to reference a chunk labeled relevant for the case.

Abstention expectations come from expected-fact judgments: a case with expected facts requires a
published answer, while a case without expected facts requires abstention. Accepted and warned
artifacts count as answers; repair-required and rejected outcomes are unresolved rather than
successful abstentions. Per-case evidence retains support-evaluator component, model,
configuration, usage, latency, and cost. Missing, duplicate, or extra case results fail closed,
and a metric without a denominator remains unavailable rather than becoming a synthetic score.
Grounded-generation reports expose the same five segment dimensions with per-slice quality,
abstention, usage, duration, and cost evidence.

Cases may belong to multiple source or format slices. Segment aggregates overlap by design and are
not additive; global metrics are always computed directly from the complete case set.

`KnowledgeQualitySnapshot` projects retrieval and grounded-generation reports into one release-gate
metric model. Metric keys identify either the global report or an exact source, format, language,
tenant-policy, or query-type slice. This allows a segment regression to block release even when
the aggregate remains acceptable.

`KnowledgeQualityGate` enforces minimum case count, absolute thresholds, and maximum regression
from a promoted baseline. Quality ratios are higher-is-better; latency and cost are
lower-is-better. Missing metrics fail rather than defaulting, and baseline comparison requires the
exact dataset content hash and evaluator component family.

Passing evidence can be promoted through `KnowledgeQualityBaselinePromotion`. Version-1 baseline
artifacts retain the complete snapshot, threshold-configuration hash, authenticated owner,
mandatory justification, promotion time, and previous baseline identity. A new dataset content
hash requires a separately reviewed initial baseline rather than comparison with non-equivalent
cases.

`KnowledgeAdversarialDatasets.adversarialV1` is the repository-owned executable RAG threat
fixture. Its synthetic corpus carries source, document, chunk, exact text, trust, freshness,
access, and typed risk evidence. Five cases cover poisoning, retrieved prompt injection, stale
guidance, unresolved authoritative conflict, and an unauthorized cross-boundary result. Each case
also exposes an ordinary `KnowledgeEvaluationCase`, allowing retrieval and grounded-generation
metrics to run without a separate adversarial execution path.

The adversarial dataset hash governs both corpus content and scenario judgments. References to
unknown corpus entries, duplicate chunks, documents assigned to multiple sources, and risk labels
without corresponding relevant or prohibited evidence are rejected during construction.

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

Source-backed embedding batches participate in the same governed evidence path as agent and tool
work. Runtime budgets track embedding calls separately from chat LLM calls while sharing total
token and cost limits. Metrics include profile ID, stable batch ID, input count, provider tokens,
latency, cost, and attempt count. Distributed spans use `embedding.generate`, and evaluation
execution traces include text-free `EmbeddingCall` steps. These records contain no chunk text,
vector values, provider response bodies, principal evidence, or credentials.

Production, test, and replay should use the same harness path. Recorded provider and tool fixtures should allow a failed case to run without live network dependencies.

## Release gates

Evaluation gates can cover:

- Functional success rate
- Quality and groundedness
- Safety and policy compliance
- Retrieval recall and citation correctness
- Mean/p95 latency and reliability
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

Production retrieval failures use an explicit candidate workflow. Capture requires authenticated
ownership, a consent-record hash, retention deadline, and a versioned query redactor. The
candidate stores only the redacted query, removed-value count, typed failure and correlation
evidence, observed chunk identities, dimensions, and redactor provenance. Raw queries and removed
values are not retained by the candidate.

Candidates do not become cases automatically. Review and promotion are separate administration
resources with independent permission decisions. Authorized approval requires complete relevance,
expected-fact, and prohibited-result judgments and constructs the ordinary
`KnowledgeEvaluationCase` contract. Rejected or expired candidates cannot be promoted. Promotion
creates a new dataset revision and records the candidate identities, authenticated promoter,
permission evidence, and promotion time.

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
