# Configuration and Administration

Nao separates deployment configuration from live operational configuration. This keeps settings
that define security and durability boundaries immutable while allowing validated operational
changes without restarting the host.

## Configuration boundaries

`PlatformConfiguration` is the schema-v1 startup contract. Identity mode, administrative exposure,
worker isolation, authoritative storage, cluster membership, and deployment profile are immutable
for the lifetime of a host process. A host must parse and validate this configuration before
opening listeners. Production profiles reject anonymous administration and other development-only
settings.

`ReloadablePlatformSettings` contains the operational references that may change safely:

- provider routing revision;
- policy revision;
- knowledge revision;
- evaluation revision;
- concurrency, queue, and provider-timeout limits.

These are revision identities, not embedded policy documents, credentials, or mutable provider
objects. Secrets remain host-resolved references and never enter either configuration contract.

## Last-known-good activation

`ReloadableConfigurationRuntime` owns one immutable current snapshot and retained snapshots for
rollback. Changes follow this sequence:

1. Construct typed candidate settings. Invalid limits or non-canonical revisions fail immediately.
2. Preview the candidate and inspect its affected resource categories.
3. Activate with the current expected version, authenticated actor, and non-blank reason.
4. Publish the new snapshot only after all validation and concurrency checks pass.

A stale expected version, invalid candidate, or no-op candidate cannot replace the current
snapshot. Activation is serialized and versions increase monotonically. Rollback selects a
retained snapshot's settings and activates them as a new version; it never rewinds version
history. Mutation evidence identifies the actor, reason, prior version, new version, affected
resource categories, and activation time.

The runtime retains history in process. A production host that must preserve reload history across
restart must persist candidate revisions and mutation evidence through its administrative
control-plane storage; the startup configuration remains the recovery authority until the host
replays an approved operational revision.

## Authenticated administration

`RuntimeAdministrationGrain` is the tenant-keyed mutation boundary for providers, workspaces,
policies, quotas, knowledge, evaluations, operations, sessions, health, and emergency resources.
Hosts expose it through an authenticated transport; Nao does not expose an anonymous endpoint.

`RuntimeAdministrationClient` is the reference client. It requires a `SecurityPrincipal`, a
matching tenant identity, and an `IRuntimeAdministrationTransport`. The transport is responsible
for binding the authenticated host request to the supplied principal before invoking Orleans.
The client rejects cross-tenant principals, impersonation, weak authentication, and missing roles
before transport invocation. Administrative callers require multi-factor or phishing-resistant
authentication and either `nao.control.admin` or the resource-specific
`nao.control.<kind>.manage` role.

Application-specific UI remains outside reusable Nao packages. A host may build web, CLI, or
automation surfaces over the same transport and client contracts, but must not bypass their
principal and tenant checks.

## Preview, apply, and rollback

Every change carries:

- a caller-stable operation ID;
- resource kind and identity;
- action and canonical reason;
- expected resource revision;
- canonical non-secret parameters.

Use `PreviewAsync` to run the same validation and adapter impact analysis without mutation. Apply
the unchanged request through `ApplyAsync`. A stale resource revision fails instead of overwriting
another operator's change, and reusing an operation ID with different content is rejected.

Before external effects, the grain durably records the pending operation and impact. Host adapters
must use the operation ID idempotently. The grain publishes the new revision only after adapter
application, propagation acknowledgement, and idempotent audit recording succeed. Audit evidence
records actor, reason, prior and new revisions, exact affected resource identities, parameter
names, warnings, and impact summary without recording parameter values.

`RollbackAsync` is a normal optimistic mutation targeting a retained resource revision. Models,
tools, agents, connectors, and tenants instead reserve `disable` and `enable` for emergency
control and additionally require `nao.control.emergency`.

## Host responsibilities

Production hosts must provide:

- transport authentication and principal construction;
- TLS and endpoint authorization;
- idempotent resource adapters and propagation observers;
- durable pending-operation, resource-state, and audit storage;
- enforcement of model and connector disablement at their I/O boundaries;
- an approved operational-revision store if reload state must survive restart;
- an application-specific administration UI when one is needed.

Do not expose an administration transport until startup configuration has passed production-safety
validation.
