# Supported deployment profiles

Nao configuration schema v1 defines four explicit deployment profiles. Hosts must parse the versioned configuration with `PlatformConfigurationJson.parse` and call `PlatformStartup.validate` before opening listeners, accepting work, or advertising health.

## Profiles

| Profile | Intended use | Reliability boundary | Security boundary |
|---|---|---|---|
| Local development | One developer on a trusted workstation | Ephemeral state and in-process execution are allowed; no availability or recovery guarantee | Permissive development identity is allowed, but administration remains disabled and tools remain restricted |
| Single-node production | One production runtime with durable backing services | Persistent authoritative state; host-owned backup and restart; no node-failure availability | Authenticated identity and administration, restricted tools, and out-of-process workers |
| Clustered production | Orleans cluster with durable backing services | Persistent state and node-failure tolerance within one supported region; cluster membership is mandatory | Workload identity, authenticated administration, restricted tools, and container-isolated workers |
| Isolated worker | Dedicated execution worker behind an authenticated control plane | Persistent coordination belongs to the calling runtime; the worker does not expose administration | Workload identity, disabled administration, restricted tools, and container isolation |

Multi-region active/active deployment is not supported. Profile selection does not replace host enforcement of network policy, resource limits, storage durability, backup, or workload identity.

## Versioned examples

- [`local-development.json`](https://github.com/jichang/Nao/blob/main/deploy/profiles/v1/local-development.json)
- [`single-node-production.json`](https://github.com/jichang/Nao/blob/main/deploy/profiles/v1/single-node-production.json)
- [`clustered-production.json`](https://github.com/jichang/Nao/blob/main/deploy/profiles/v1/clustered-production.json)
- [`isolated-worker.json`](https://github.com/jichang/Nao/blob/main/deploy/profiles/v1/isolated-worker.json)

The examples are parsed and validated by the repository test suite. Copy the selected file, change `instanceId`, and resolve every named secret through the declared provider. Secret values never belong in the platform configuration.

## Startup sequence

1. Load exactly one schema-v1 JSON document.
2. Resolve the profile-specific secret references without inserting secret values into the configuration object.
3. Call `PlatformConfigurationJson.parse`.
4. Call `PlatformStartup.validate`.
5. Record `EffectivePlatformConfiguration.values` and `EffectivePlatformConfiguration.secretReferences` in startup diagnostics.
6. Only after validation succeeds, initialize durable stores, bind listeners, and advertise readiness.

Unknown fields, unsupported versions, unsafe profile combinations, duplicate secret names, and malformed values fail startup. Effective diagnostics contain secret provider names and `<redacted>`, never secret keys or values.

## Production prohibitions

All production profiles reject:

- permissive development identity;
- anonymous administration;
- unrestricted tools;
- in-process untrusted execution;
- ephemeral authoritative state.

Cluster membership is valid only for clustered production. Isolated workers cannot expose administrative endpoints.
