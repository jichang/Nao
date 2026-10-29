# Integration selection and support

Nao selects integrations through the schema-v1 [`integration-policy.json`](https://github.com/jichang/Nao/blob/main/integration-policy.json). Selection requires a named user and roadmap need, an owned maintenance path, acceptable licensing, a supported deployment or isolation boundary, and domain-specific conformance evidence. Popularity or protocol compatibility alone does not make an adapter supported.

## Support tiers

| Tier | Guarantee |
|---|---|
| Production | Release-blocking Nao package with domain, security, lifecycle, operational, and ownership evidence |
| Optional | Supported Nao package for explicitly validated use cases; the host owns the external engine or service boundary |
| Host boundary | Supported Nao contract and implementation guidance; the application owns vendor choice, credentials, availability, and operations |

All selected integrations are owned by `@jichang`. Package and policy ownership is enforced through `CODEOWNERS`; the machine-readable policy rejects missing projects, owners, tests, documentation, operational guidance, or use-case declarations.

## Selected integrations

| Integration | Capability | Tier | Delivery | Validated use |
|---|---|---|---|---|
| PostgreSQL pgvector | Vector retrieval | Production | `Nao.Knowledge.Postgres` | Authorized semantic and hybrid grounded retrieval |
| PostgreSQL full-text search | Lexical retrieval | Production | `Nao.Knowledge.Postgres` | Authorized lexical and hybrid grounded retrieval |
| PostgreSQL graph memory | Graph backend | Production | `Nao.Persistence.Postgres` | Durable tenant graph memory and graph-enhanced evidence |
| OpenTelemetry | Telemetry export | Production | `Nao.Telemetry.OpenTelemetry` | OTLP traces, metrics, logs, and Prometheus projection |
| OIDC principal adapter | Enterprise identity | Production | `Nao.Identity.Oidc` | Host-validated claims, delegation, and explicit impersonation |
| Host secret provider | Enterprise secrets | Host boundary | `Nao.Agents` guidance | Authorized vault resolution and isolated-worker delivery |
| Souffle Datalog | Formal reasoning | Optional | `Nao.Reasoning.Datalog` | Typed stratified rules and proof-backed derivation |
| Z3 constraints | Formal reasoning | Optional | `Nao.Reasoning.Constraints` | Bounded scheduling, satisfiability, and optimization |

PostgreSQL graph memory is selected because BRG-02 has a validated graph-enhanced retrieval use case and independently evaluates graph contribution. A separate graph database is not justified while PostgreSQL meets isolation, mutation, recovery, and deletion requirements.

## Enterprise identity boundary

`Nao.Identity.Oidc` maps exactly one identity already authenticated by host middleware. It does not parse tokens or validate signatures, issuers, audiences, expiry, or revocation. Configure tenant, subject, service, delegation, impersonation, group, role, allowlisted claim, and authentication-strength mappings. Missing, duplicate, ambiguous, malformed, or internally inconsistent authority fails closed.

The host must:

1. validate the token and transport before mapping;
2. constrain accepted issuers, audiences, algorithms, lifetimes, and authentication contexts;
3. map only claims with a defined authorization purpose;
4. retain authentication and revocation evidence in host audit records;
5. pass the resulting `SecurityPrincipal`, never raw claims, into Nao runtime boundaries.

## Enterprise secret-provider boundary

Nao deliberately does not select a vault vendor before a deployment has chosen workload identity, tenancy, rotation, and availability requirements. A production host adapts its enterprise vault through `SecretProvider.create`.

The resolver receives the authenticated `AuthorizationScope`, opaque `SecretReference`, and cancellation token. It returns `Available` with an exact `SecretVersion` and reference-bound `SecretValue`, `Revoked`, or `Missing`. The implementation must:

1. authorize the caller and reference at the vault boundary;
2. use workload identity rather than static bootstrap credentials where the platform supports it;
3. preserve cancellation and bound network deadlines;
4. return material for exactly the requested reference;
5. expose vault and secret versions as resolution evidence without exposing values;
6. implement rotation and revocation through the vault's compare-and-swap or equivalent concurrency control;
7. avoid caching beyond the vault and application threat model;
8. never log, serialize, diagnose, or place resolved values in configuration or deployment manifests.

`SecretRedaction.runAsync` provides defense-in-depth for values resolved during an execution, but it does not replace correct host logging and vault policy. Production recovery restores vault versions through the vault's own protected procedure; secret material is never part of a Nao backup.

## Formal-reasoning admission

Formal engines remain optional and cannot be selected as general-purpose answer generators. Souffle is admitted only for typed, range-restricted, stratified rule programs. Z3 is admitted only for typed bounded constraints and explicit optimization. Both execute through host-owned isolated runners, validate engine identity and response integrity, preserve assumptions and provenance, enforce budgets, and keep unknown, timeout, unsatisfiable, and failure outcomes distinct.

New formal adapters require:

- a concrete domain use case that existing selected engines cannot satisfy;
- a typed input language with no arbitrary code or raw engine program escape;
- semantic and adversarial conformance datasets;
- deterministic engine/version evidence and resource bounds;
- isolation, authorization, exfiltration, and failure tests;
- proof, binding, model, or equivalent result evidence;
- an explicit human-review policy for high-impact conclusions.

## Validation

```bash
python3 scripts/validate-integrations.py
python3 -m unittest scripts/test_validate_integrations.py
```

PostgreSQL external-service tests require `NAO_TEST_POSTGRES`; they are the release qualification for real vector, lexical, graph, recovery, deletion, and benchmark behavior. CI and protected publication run them against the same digest-pinned pgvector/PostgreSQL image. A local run without the environment reports inconclusive rather than fabricating evidence.
