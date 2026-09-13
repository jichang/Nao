# Getting Started

This guide builds the current Nao solution and introduces the smallest typed agent. For platform direction and capability status, begin with [Nao AI Platform](platform.md).

## Prerequisites

- .NET 10.0 or later
- Paket, installed through the repository's local .NET tool manifest

## Restore, build, and test

```bash
dotnet tool restore
dotnet paket install
dotnet build Nao.slnx
dotnet test Nao.slnx
```

Some test projects and integration scenarios may require services or may not yet be members of the default solution. The [foundations roadmap](roadmap/00-foundations.md) tracks consolidation of the complete supported test surface.

## Define an agent

Agents publish an explicit name, description, execution guidance, and transport contract. Nao does not infer transport schemas or force one serialization format.

```fsharp
open System.ComponentModel
open Nao.Agents

type EligibilityInput =
    { [<Description("Applicant age in years.")>]
      Age: int
      [<Description("Whether the applicant accepted the terms.")>]
      AcceptedTerms: bool }

type EligibilityOutput =
    { Eligible: bool }

let eligibilityAgent =
    Agent.createContextual
        "eligibility-agent"
        "Eligibility"
        "Checks whether an applicant is eligible."
        10
        [ "Check eligibility and return a typed decision." ]
        { Input = AgentParameter.Structured "object with integer age and boolean acceptedTerms"
          Output = AgentParameter.Structured "object with boolean eligible" }
        (fun _context encodedInput -> task {
            let input = decodeInput encodedInput
            return encodeOutput { Eligible = input.Age >= 18 && input.AcceptedTerms }
        })
```

Applications own encoding and decoding, allowing JSON, protocol-specific formats, or domain codecs to be used deliberately.

## Run an agent

```fsharp
let! result = Agent.runAsync agentContext encodedInput eligibilityAgent
```

The result is `Result<string, PlatformFailure>`. `AgentContext.unrestrictedForTests ()` creates a fresh correlated context for isolated tests. Production hosts must execute through the harness, which installs the governed runtime dispatcher.

## Use the ETCLOVG harness

The harness provides the common execution path for governance, readiness, lifecycle, observability, execution, output validation, verification, and audit.

```fsharp
let readinessCheck =
    ReadinessCheck.create "dependencies" (fun _agentId _input ->
        Task.FromResult ReadinessResult.Ready)

let config =
    { EtclovgConfig.Default with
        Tracer = Some(Tracer.inMemory ())
        Metrics = Some(InMemory.metrics ())
        Constitution =
            Some(
                Constitution.empty "safety"
                |> Constitution.addRule Constitution.noPrivateDataRule)
        ReadinessChecks = [ readinessCheck ]
        TraceStore = Some traceStore
        AuditLog = Some(AuditLog.inMemory ()) }

let request =
    ExecutionRequest.create
        authorizationScope
        turnId
        conversationId
        eligibilityAgent.Metadata.Id
        encodedInput
        (SandboxConfig.Restricted(
            ResourceLimits.Constrained 60 50 100000))
        policyVersions
        dependencyVersions
        correlation

let! result =
    EtclovgHarness.runAsync config agentContext eligibilityAgent request cancellationToken

match result.Status, result.Outputs.Response with
| ExecutionTerminalStatus.Succeeded, Some response -> printfn "%s" response
| status, _ -> printfn "Execution ended with %A" status
```

The request is the authoritative execution envelope. Its authorization scope, identity,
budgets, pinned policy and dependency versions, and correlation data are immutable for the
run. The harness rejects a request whose agent identity differs from the executable agent.
The result groups response and published artifacts under `Outputs`, trace and metrics under
`Evidence`, governance outcomes under `PolicyDecisions`, and exposes one terminal `Status`.

`ReadinessCheck` and `Judge` are immutable capability records. Their modules provide
`create` and invocation functions, so harness extensions require no interface implementation,
object expression, or cast. The harness starts all configured readiness checks together and
retains their configured order when aggregating not-ready reasons.

Worker-resolvable agents can run through host-configured process or Docker-compatible
container environments. Process workers inherit no host environment variables and receive
only the explicit `SandboxConfig.EnvironmentVariables` map. A configured fully qualified
process working directory is applied before launch, but it does not confine filesystem access
by itself. On Linux, hosts can select `ProcessIdentityIsolation.LinuxUserNamespace` with a
fully qualified `unshare` path and target UID/GID. The worker sees only that mapped identity;
the mapping still has the caller's host filesystem DAC permissions, so use Landlock separately
for path confinement. Hosts can select `ProcessFileSystemIsolation.LinuxLandlock` to enforce
canonical existing `SandboxConfig.AllowedPaths` before handler execution; trusted
worker/runtime paths retain read-only baseline access. Disabled mode and non-Linux platforms
do not enforce these controls.

Container runs require SHA-256-pinned image references, publish structured image and
host-supplied supply-chain metadata, enforce numeric UID/GID 65532, read-only root, all
capabilities dropped, and no privilege escalation. Construction probes the runtime daemon and
requires Linux containers plus memory and process-count limit support; `tryContainerWorker`
returns explicit unsupported reasons instead of an execution-time protocol failure. Runs use
`--rm`, a predeclared unique name,
and unconditional forced container and anonymous-volume cleanup; removal also detaches the
runtime-managed network attachment. Declared environment variable names are forwarded without
values in argv, working directories must be absolute container-internal paths, and default
network policy selects `--network=none`. Host paths and devices are exposed only through
explicit validated lists; bounded writable storage uses hardened UID-owned `tmpfs`
declarations. Configured memory, CPU-time, and process-count limits become hard Docker controls,
while the shared worker boundary enforces duration and output limits.

Destination allowlists carry a host-owned `ContainerEgressBroker`. After image verification, the
broker receives the validated absolute HTTP(S) destinations and returns a dedicated runtime
network attachment plus enforcement claims. Nao rejects built-in or option-like network names,
publishes broker and policy identity in `container.network`, and releases the attachment after
success, failure, cancellation, or pre-launch publication failure. Hosts own the proxy, routing,
DNS, certificate, and policy implementation behind that attachment. The secured container CI
probe uses isolated worker and upstream networks with a fixed-forward broker to prove that its
declared endpoint is reachable while a live undeclared sibling and public destinations are not.

Every container configuration supplies a `ContainerImageVerifier`. Before artifact publication
or runtime launch, it must accept the exact digest-pinned reference and return non-empty verified
claims. The `container.image` artifact records those claims with verifier and policy identity.
Hosts own scanner or attestation tooling, trust roots, policy content, and key lifecycle.

Protocol v1 transports remaining logical allowances, host-authorized secret handles, and the
scope-bound security principal. Worker handlers access identity through `request.Principal`,
separately from model input. Protected execution rejects missing identity; deploy matching v1
host and worker binaries/images together. Worker
handlers consume `WorkerExecutionBudget` and `WorkerSecrets`; structured overages cross the
protocol, reported usage merges into the host context, and undeclared secrets fail closed
without provider access. Process memory and CPU checks remain reactive rather than hard
operating-system quotas. Optional `ProcessOutputCapture` independently bounds both streams,
applies its required host redactor, and publishes text artifacts through the governed context.
Complete secret lifecycle and production operation of host egress-broker implementations remain
host responsibilities, so these environments are not by themselves general untrusted-code
security boundaries.

## Register a workspace

Hosts register compiled agents and tools explicitly:

```fsharp
let registry =
    WorkspaceRegistry.fromWorkspaces [
        ("default",
         { WorkspaceDefinitions.Empty with
             Agents = [ agent ]
             Tools = tools })
    ]
```

The runtime intentionally does not load arbitrary agent assemblies or JSON-defined executable code.

## Git hooks

Enable the repository pre-commit hook:

```bash
git config core.hooksPath .githooks
```

## Next steps

- [Architecture and ETCLOVG](architecture.md)
- [Agents and orchestration](agents-orchestration.md)
- [Tools, security, and governance](tools-governance.md)
- [Development and contributing](development.md)
