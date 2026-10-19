# Telemetry privacy and retention

Nao telemetry export now requires authenticated tenant ownership and an explicit privacy policy.
This is a direct version-1 contract replacement because the project is unreleased.

`EventScope.Create` now requires a canonical tenant ID. Hosts must derive it from the active
`AuthorizationScope`; request content, session keys, and caller-supplied routing fields are not
valid sources.

`TelemetryExportConfiguration.create` now receives regional `TelemetryDestination` values and a
`TelemetryPrivacyConfiguration`. Production destinations require HTTPS OTLP endpoints and a
backend-managed at-rest encryption key identity. Development HTTP endpoints must use
`LocalDevelopment` explicitly.

Define an exact `TelemetryTenantPolicy` for every exporting tenant. Missing policies, unknown
regions, expired events, and sampling exclusions do not reach a backend. Content capture defaults
to disabled in the local helper and is configured separately from operational metadata. Production
hosts must use a private sampling salt and configure each regional backend to enforce the tenant's
physical retention period across primary storage, replicas, and backups; maximum export age only
prevents stale signals from entering that backend.

`AuditLog.DeleteExpiredAsync` now requires `AuditRetentionAuthorization`. The authenticated
principal must hold `nao.audit.retention.manage` and use multi-factor or phishing-resistant
authentication. Exact-scope `DeleteScopeAsync` remains the subject-erasure operation.
