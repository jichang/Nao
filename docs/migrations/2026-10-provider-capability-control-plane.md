# Provider capability control plane

Provider routing now uses explicit versioned capability observations rather than model-name
heuristics. This changes the version-1 provider control-plane contract in place because Nao is
unreleased.

Hosts that route production requests should replace direct aggregate-provider activation with
`ProviderFactory.activateEndpointAsync`. Supply a `ProviderEndpointConfiguration` that either
returns validated configured metadata or probes the endpoint. Every capability must be marked
supported, unsupported, or unknown; unknown capabilities fail closed.

Declare invocation constraints through `ProviderRequirements` and invoke through
`ProviderEndpoint.completeAsync` or `ProviderEndpoint.streamAsync`. Compatibility is evaluated
before the adapter receives request content. Temporary health, readiness, quota, and maintenance
failures remain distinct from unsupported capabilities.

Persist `ProviderCapabilityAudit` records in the host's governed audit sink. These records contain
endpoint and observation identities plus changed capability names, but no prompts, completions,
credentials, or provider response bodies.
