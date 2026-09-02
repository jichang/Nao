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

## Define a typed agent

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

type EligibilityAgent(
    decodeInput: string -> EligibilityInput,
    encodeOutput: EligibilityOutput -> string) =
    inherit TypedContextualAgent<EligibilityInput, EligibilityOutput>(
        "eligibility-agent",
        "eligibility",
        "Checks whether an applicant is eligible.",
        10,
        [ "Check eligibility and return a typed decision." ],
        { Input = AgentParameter.Structured "object with integer age and boolean acceptedTerms"
          Output = AgentParameter.Structured "object with boolean eligible" })

    override _.RunAsync(_context, encodedInput) =
        task {
            let input = decodeInput encodedInput
            return
                encodeOutput
                    { Eligible = input.Age >= 18 && input.AcceptedTerms }
        }
```

Applications own encoding and decoding, allowing JSON, protocol-specific formats, or domain codecs to be used deliberately.

## Run an agent

```fsharp
let agent = EligibilityAgent(decodeInput, encodeOutput) :> IAgent
let! result = agent.RunAsync(AgentContext.allowAll, encodedInput)
```

`AgentContext.allowAll` is suitable for isolated tests and library examples. Production hosts should construct a context connected to their permission and identity policy.

## Use the ETCLOVG harness

The harness provides the common execution path for governance, readiness, lifecycle, observability, execution, output validation, verification, and audit.

```fsharp
let config =
    { EtclovgConfig.Default with
        Execution =
            SandboxConfig.Restricted(
                ResourceLimits.Constrained 60 50 100000)
        Tracer = Some(Tracer.inMemory ())
        Metrics = Some(InMemory.metrics ())
        Constitution =
            Some(
                Constitution.empty "safety"
                |> Constitution.addRule Constitution.noPrivateDataRule)
        ReadinessChecks = [ readinessCheck ]
        TraceStore = Some traceStore
        AuditLog = Some(AuditLog.inMemory ()) }

let! result =
    EtclovgHarness.runAsync config agent encodedInput
```

Current sandbox records and checks resource limits, but real process and container isolation remain roadmap work. Do not treat the default local execution environment as an untrusted-code security boundary.

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
