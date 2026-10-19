# Evaluation and Observability

Quality, safety, latency, reliability, and cost are runtime properties. Nao's evaluation and observability foundations are intended to become one feedback loop from development through production.

## Current evaluation model

`Nao.Eval` provides schema-versioned evaluation cases, datasets, functional `Evaluator` records,
runners, and reports. Cases contain ordered role-bearing turns, structured expected behavior,
governance metadata, and provenance. Datasets have positive revisions and canonical SHA-256
content identities. Built-in modules construct and compose evaluators without implementation
classes.

```fsharp
let identity name version configuration =
    EvalComponentIdentity.create name version configuration

let specification =
    EvalExecutionSpecification.create
        (Map [ "math-agent", identity "math-agent" "3" "agent configuration" ])
        (identity "math-prompt" "7" "prompt text")
        (identity "text" "1" "response protocol")
        []
        (Some(identity "openai-compatible" "1" "provider configuration"))
        (Some(identity "model-name" "2026-10" "model options"))
        (identity "etclovg" "1" "harness configuration")
        [ identity "production-policy" "4" "policy configuration" ]
        []

let execution =
    EvalExecutionConfig.create
        authorization
        createAgentContext
        harnessConfig
        sandboxConfig
        specification
        EvalExecutionMode.PinnedLive
        (EvalRuntime.system 20261015)

let config = EvalRunnerConfig.create execution

let dataset =
    EvalDataset.create
        (AuthorizationScope.identity authorization)
        "math"
        1
        [ EvalCase.repository "1" "2+2" "4" ]
        []

let evaluator = ExactMatch.evaluator

let! report =
    EvalRunner.runDatasetAsync
        config
        evaluator
        agent
        dataset
        cancellationToken
```

`EvalExecutionSpecification` pins every executable component that can influence a result: agent,
prompt, response protocol, tools, provider, model, harness, policies, and knowledge indexes.
Evaluators carry their own version and configuration hash. The runner resolves these identities
into `EvalExecutableConfiguration`, and every report records that configuration together with the
dataset ID, revision, and content hash. An agent without a pinned identity is rejected before
execution.

`EvalExpectedBehavior` supports reference answers, named properties, required tool calls,
forbidden actions, artifacts, citations, resource budgets, and allowed terminal states. Dataset
fixtures contain ordered, versioned setup and teardown operations whose inputs are represented by
content hashes. This makes the specification portable without embedding fixture secrets in the
dataset.

Case governance records an owner, labels, review status, optional expiry, and source. A
production-derived case can only be constructed with `EvalSanitizationReview`, which requires the
sanitizer, timestamp, applied redaction rules, and exact matching approval evidence. Repository
cases are explicitly marked `RepositoryAuthored`; callers must not use that provenance for
production-derived content.

`EvalDataset.revise` preserves dataset identity, increments the revision, and recomputes the
content hash across all governed case and fixture fields. `EvalDataset.restore` and the evaluation
archive verify that hash. Reports can only be archived after their exact dataset revision is
present, and mismatched revision or content identity is rejected.

## Harness parity and reproduction

Every evaluation mode invokes `EtclovgHarness.runAsync`; there is no evaluator-only execution
path. `EvalExecutionMode` declares one of three host-selected modes:

- `PinnedLive` uses the pinned provider/model configuration and rejects installed replay fixtures.
- `PinnedMock` uses pinned host mock components and rejects installed replay fixtures.
- `RecordedReplay` owns a versioned `EvalRecordedFixtureSet` and installs its exact provider and
  tool fixtures into the ordinary harness. Missing or mismatched dependency fixtures fail closed
  in the harness, so recorded mode cannot fall through to live network or tool access.

`EvalRuntime` records a random seed and supplies run, turn, execution, correlation, result, and
timestamp identity. Production runs use `EvalRuntime.system`; deterministic tests and local
reproduction use `EvalRuntime.deterministic`. Reports retain the seed, deterministic flag,
maximum parallelism, stop-on-first-failure setting, mode, and fixture schema/format versions.

`EvalRunComparison.compare` compares semantic case outcomes only after proving exact dataset and
executable-configuration identity. A changed deterministic run is a regression; a changed run
where either side is nondeterministic is reported separately as nondeterministic variance.

`EvalReproductionBundle` contains the complete dataset, exact executable configuration, failing
case identities, evaluator identity, seed, timestamp, scheduling, and versioned recorded
fixtures. Its JSON form validates dataset and fixture identities when loaded. Local replay also
requires the supplied agent, evaluator, and execution specification to exactly match the bundle
before running through the harness in recorded deterministic mode.

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

Every `Evaluator` declares `EvaluatorAuthority.Deterministic` or `Probabilistic` plus mandatory
release-blocking status and failure semantics. `EvaluationContext` includes the complete harness
`ExecutionResult`, while `EvalResult` records evaluator authority and explicitly marks
probabilistic rationale as untrusted evidence.

`DeterministicExecution` validates the structured response and caller-owned schema together with
expected properties, tool order/count/argument hashes, artifacts, citations, forbidden actions,
resource budgets, and terminal states. Missing evidence fails closed. Prefer this evaluator before
any semantic judge when a property is mechanically observable.

`HighRiskEvaluation` runs deterministic validators first. Any deterministic failure blocks the
result without invoking judges. Only after deterministic success does it aggregate multiple
probabilistic judges using unanimous, majority, or minimum-average-score policy. Thus a favorable
judge can never override invalid protocol, policy, citation, artifact, tool, or resource evidence.

`JudgeCalibration` consumes human-labeled repeated observations and measures accuracy, Cohen's
kappa, stability, position bias, verbosity bias, candidate model-family pass rates, and
self-preference bias. `JudgeUpgradeQualification` requires a candidate judge to meet configured
sample, accuracy, agreement, stability, bias, and regression thresholds against the prior judge
before promotion.

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

`KnowledgeGroundedReleaseQualification` fixes the initial grounded release profile to the exact
five-case adversarial dataset and grounded-generation evaluator. It requires context relevance of
at least 0.90, answer faithfulness and citation correctness of at least 0.95, perfect abstention
accuracy, no unresolved responses, and one support-evaluator descriptor across every case.
Evidence also binds the versioned generation-adapter component and configuration hash. The
deterministic reference adapter proves the thresholds are executable; it does not qualify a
production adapter.

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

`EvalReleaseGate` is the generic release boundary for quality, safety, success, policy
compliance, retrieval, latency, tokens, and cost. Metric keys are either overall or one exact
scenario, provider, model, language, source, or tenant-policy segment. Each threshold owns its
absolute bound, tolerated baseline delta, minimum sample size, and confidence level. Proportions
use Wilson intervals; sampled continuous values require dispersion evidence and use a normal
interval. Missing metrics or insufficient evidence fail closed.

Release snapshots bind the exact dataset content and executable configuration. Correctness
regressions and infrastructure failures remain separate evidence and both block promotion.
`EvalReleaseProjection` derives quality, functional success, latency, scenario success, and typed
failure evidence directly from the canonical `EvalReport`; callers add domain metrics such as
safety, policy compliance, retrieval, tokens, and cost without replacing canonical metrics.
`EvalBaselineRegistry` separates proposal, independent review, and promotion; retains immutable
baseline versions and audit events; supports explicit rollback to any retained baseline; and
permanently unions failed case identities observed by proposals so replacing a baseline cannot
erase regression history. `EvalReleaseArtifact` emits reviewable JSON gate evidence.

CI runs the `EvalFast` profile for pull requests and pushes and the `EvalFull` profile on the
scheduled workflow. Both publish TRX gate reports even when a gate fails.

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

`EvalProductionSamplingPolicy` governs deterministic trace sampling by policy identity, private
salt, sample rate, consent-record hash, retention duration, allowed production signals, and a
versioned redactor. Captured observations contain only redacted input, output, and optional
feedback plus removed-value counts and applied-rule evidence. They bind the originating turn and
execution to the complete `EvalExecutableConfiguration`. Raw feedback comments do not enter the
evaluation candidate.

Explicit negative `Feedback` is joined to its exact `TurnRecord`; mismatched turns fail.
Failures, denials, retries, abandonments, low-confidence results, and negative feedback share one
typed observation model. Clustering groups only observations with the same signal, typed segment
map, and executable configuration, preventing unlike deployments or tenant-policy slices from
being merged.

Selected observations enter `EvalProductionCandidate` under their original ownership boundary.
Proposal requires the owning authenticated scope. Review must be independent while remaining in
the same tenant, group, workspace, and session boundary. Successful review constructs the normal
version-1 `EvalCase` with matching approval and sanitization evidence, consent lineage, sampling
policy, observation identity, and execution identity.

`EvalDriftDetection` evaluates model, prompt, retrieval, cost, latency, and traffic-distribution
observations. Every alert retains the changed dimension, exact affected segment, baseline and
current executable configurations, magnitude, warning or critical severity, and the
rule-configured notify, release-block, or rollback action. Scalar observations enforce direction
and minimum samples; traffic drift uses total variation distance over normalized distributions.

`EvalExperiment` provides deterministic shadow, canary, and A/B assignment. Assignment hashes a
private salt, scoped ownership identity, and execution identity into an opaque cohort identifier;
evidence contains no prompt, response, feedback, claims, or credentials. Shadow traffic keeps one
primary and one or more zero-allocation shadow configurations, while canary and A/B allocations
must total 10,000 basis points.

Production incidents cannot close on an explanation alone. `EvalProductionIncident.close`
requires an approved production-derived regression case, preserving the incident as permanent,
executable regression coverage.

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
