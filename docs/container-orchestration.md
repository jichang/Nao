# Container images and Kubernetes orchestration

Nao publishes two one-shot worker images and a Helm chart for a host application that integrates the Nao runtime libraries. Nao does not ship a generic public API server: identity, transports, trust roots, secret resolution, and product endpoints remain host-owned.

## Published worker images

| Image | Dockerfile | Purpose |
|---|---|---|
| `ghcr.io/jichang/nao-agents-worker` | [`agents-worker.Dockerfile`](https://github.com/jichang/Nao/blob/main/deploy/images/agents-worker.Dockerfile) | Executes registered process-isolated agent handlers over worker protocol v1 |
| `ghcr.io/jichang/nao-documents-worker` | [`documents-worker.Dockerfile`](https://github.com/jichang/Nao/blob/main/deploy/images/documents-worker.Dockerfile) | Parses governed document content over worker protocol v1 |

Both images:

- use digest-pinned .NET build and runtime bases;
- run as numeric user and group `65532:65532`;
- contain only published runtime output in the final stage;
- receive `SIGTERM` as cooperative cancellation through `WorkerExecutionRequest.CancellationToken`;
- carry OCI source and revision labels; and
- are published for AMD64 and ARM64 with GitHub build provenance and an SBOM.

The workers are one-request processes launched by the governed execution environment. They are not Kubernetes services and do not expose health endpoints. The host owns worker admission, deadlines, hard resource controls, image verification, cancellation, and cleanup.

Clean image builds restore the repository-local tools and run `dotnet paket restore` before the
SDK restore and publish. Paket generates the ignored `.paket/Paket.Restore.targets` import;
installing the tool or running only `dotnet restore` does not prepare that import for publishing.

Release tags and manual invocations of [`container-images.yml`](https://github.com/jichang/Nao/blob/main/.github/workflows/container-images.yml) publish the images. Deploy by immutable digest, never by the mutable release tag:

```text
ghcr.io/jichang/nao-agents-worker@sha256:<64 lowercase hexadecimal characters>
```

## Host image contract

The chart requires a host-runtime image supplied by the application. That image must:

1. load `NAO_PLATFORM_CONFIGURATION` with `PlatformConfigurationJson.parse`;
2. call `PlatformStartup.validate` before binding its public listener;
3. load the digest-pinned worker image map from `NAO_WORKER_IMAGE_CONFIGURATION`;
4. expose `/health/startup`, `/health/ready`, and `/health/live`;
5. make readiness unhealthy before shutdown stops admission;
6. propagate shutdown cancellation through active Nao operations and await their durable terminal records;
7. use external durable state for clustered production; and
8. support numeric non-root execution with a read-only root filesystem.

The runtime libraries provide durable execution records and recovery. A host that exits without draining or uses ephemeral state does not satisfy the clustered-production profile.

## Install the clustered profile

The chart is versioned under [`deploy/helm/nao`](https://github.com/jichang/Nao/tree/main/deploy/helm/nao). Obtain immutable digests for the host and both worker images, then create a values file:

```yaml
runtime:
  image:
    repository: ghcr.io/example/nao-host
    digest: sha256:<host digest>

workerImages:
  agents:
    repository: ghcr.io/jichang/nao-agents-worker
    digest: sha256:<agents worker digest>
  documents:
    repository: ghcr.io/jichang/nao-documents-worker
    digest: sha256:<documents worker digest>

serviceAccount:
  annotations:
    example.cloud/workload-identity: nao-runtime

networkPolicy:
  egress:
    # DNS rule from values.yaml must remain. Add explicit database, telemetry,
    # provider-proxy, and identity endpoints using selectors or CIDRs.
```

Validate before installation:

```bash
helm lint deploy/helm/nao -f production-values.yaml
helm template nao deploy/helm/nao -f production-values.yaml > rendered.yaml
python3 scripts/validate-deployment-assets.py --rendered rendered.yaml
helm upgrade --install nao deploy/helm/nao \
  --namespace nao \
  --create-namespace \
  -f production-values.yaml \
  --atomic \
  --wait
```

Secret values must not appear in Helm values, ConfigMaps, image layers, or rendered manifests. Supply only named secret references in `platformConfiguration` and use the host's workload identity to resolve them from the selected secret provider.

## Reliability controls

The default clustered deployment provides:

- three runtime replicas;
- startup, readiness, and liveness probes;
- rolling updates with zero unavailable replicas and one surge replica;
- a 60-second termination grace period;
- a disruption budget allowing one unavailable replica;
- required zone spreading and preferred node spreading;
- explicit CPU and memory requests and limits;
- default-deny ingress and egress network policy;
- disabled automatic service-account-token mounting;
- optional, separately identified migration and administration workloads; and
- CPU autoscaling with delayed scale-down.

`runtime.autoscaling.maxReplicas` cannot exceed either `capacity.providerQuotaSafeReplicas` or `capacity.storageSafeReplicas`; Helm rendering fails when either capacity boundary would be violated. Operators must derive both limits from measured provider quotas and backing-store connection or throughput capacity. CPU is only the default scaling signal. A host may replace it with queue or saturation metrics after proving that the same two capacity ceilings remain enforced.

Migration jobs are disabled by default and run as a pre-install/pre-upgrade hook when explicitly configured. Administration is disabled by default and the chart does not publish an administration Service. Application owners must add authenticated transport and an explicit ingress policy before enabling remote administration.

## Network and identity boundaries

The chart creates a default-deny policy for every Nao pod and a separate runtime allow policy. The default egress list permits DNS only. Add the smallest selectors or CIDRs required for database, telemetry, identity, provider proxy, and other dependencies. Kubernetes `NetworkPolicy` cannot securely express DNS-name allowlists; use a governed egress proxy for provider destinations.

Workload identity is configured through service-account annotations. Automatic Kubernetes API credentials remain disabled. If a selected cluster-membership provider requires Kubernetes API access, add a narrowly scoped projected token and RBAC policy explicitly; the default configuration uses ADO.NET membership instead.

## Unsupported deployment claims

- Multi-region active/active is unsupported.
- Cross-region automatic failover is unsupported.
- The chart does not provision PostgreSQL, an identity provider, a secret manager, telemetry storage, or an egress proxy.
- The chart does not turn a host application into a compliant Nao host; the host contract above remains mandatory.
- Kubernetes network policy enforcement depends on the selected CNI.

The supported production boundary is one region with durable external state. Disaster recovery, backup, and active/passive regional procedures belong to OPS-03.
