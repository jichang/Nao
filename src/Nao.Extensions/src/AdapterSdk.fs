namespace Nao.Extensions

open System
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type AdapterKind =
    | Tool
    | Provider
    | Storage
    | Parser
    | Connector
    | Retriever
    | Telemetry
    | Identity
    | Reasoner

[<RequireQualifiedAccess>]
type AdapterFailureCode =
    | InvalidConfiguration
    | CompatibilityMismatch
    | DuplicateRegistration
    | NotRegistered
    | StartupFailed
    | HealthCheckFailed
    | ShutdownFailed
    | TrustRejected
    | DependencyRejected
    | IsolationRejected

type AdapterFailure =
    private
        { Code: AdapterFailureCode
          Message: string
          Retryable: bool }

[<RequireQualifiedAccess>]
module AdapterFailure =
    let create code message retryable =
        if String.IsNullOrWhiteSpace message || message <> message.Trim() then
            invalidArg (nameof message) "Adapter failure messages must be canonical and non-blank."

        { Code = code
          Message = message
          Retryable = retryable }

    let code value = value.Code
    let message value = value.Message
    let retryable value = value.Retryable

type AdapterCompatibility =
    private
        { SdkVersion: Version
          ProtocolVersion: int
          MinimumHostVersion: Version
          MaximumHostVersion: Version
          TargetFramework: string }

[<RequireQualifiedAccess>]
module AdapterCompatibility =
    let create sdkVersion protocolVersion minimumHostVersion maximumHostVersion targetFramework =
        if protocolVersion <= 0 then
            invalidArg (nameof protocolVersion) "Adapter protocol version must be positive."

        if minimumHostVersion > maximumHostVersion then
            invalidArg (nameof maximumHostVersion) "Maximum host version cannot precede minimum host version."

        if
            String.IsNullOrWhiteSpace targetFramework
            || targetFramework <> targetFramework.Trim()
        then
            invalidArg (nameof targetFramework) "Adapter target framework must be canonical and non-blank."

        { SdkVersion = sdkVersion
          ProtocolVersion = protocolVersion
          MinimumHostVersion = minimumHostVersion
          MaximumHostVersion = maximumHostVersion
          TargetFramework = targetFramework }

    let sdkVersion value = value.SdkVersion
    let protocolVersion value = value.ProtocolVersion
    let minimumHostVersion value = value.MinimumHostVersion
    let maximumHostVersion value = value.MaximumHostVersion
    let targetFramework value = value.TargetFramework

type AdapterHostCompatibility =
    private
        { SdkVersion: Version
          ProtocolVersion: int
          HostVersion: Version
          TargetFramework: string }

[<RequireQualifiedAccess>]
module AdapterHostCompatibility =
    let current =
        { SdkVersion = Version(1, 0, 0)
          ProtocolVersion = 1
          HostVersion = Version(1, 0, 0)
          TargetFramework = "net10.0" }

    let create sdkVersion protocolVersion hostVersion targetFramework =
        if protocolVersion <= 0 then
            invalidArg (nameof protocolVersion) "Host adapter protocol version must be positive."

        if
            String.IsNullOrWhiteSpace targetFramework
            || targetFramework <> targetFramework.Trim()
        then
            invalidArg (nameof targetFramework) "Host target framework must be canonical and non-blank."

        { SdkVersion = sdkVersion
          ProtocolVersion = protocolVersion
          HostVersion = hostVersion
          TargetFramework = targetFramework }

    let validate host adapter =
        let compatible =
            host.SdkVersion = AdapterCompatibility.sdkVersion adapter
            && host.ProtocolVersion = AdapterCompatibility.protocolVersion adapter
            && host.HostVersion >= AdapterCompatibility.minimumHostVersion adapter
            && host.HostVersion <= AdapterCompatibility.maximumHostVersion adapter
            && host.TargetFramework = AdapterCompatibility.targetFramework adapter

        if compatible then
            Ok()
        else
            Error(
                AdapterFailure.create
                    AdapterFailureCode.CompatibilityMismatch
                    "Adapter SDK, protocol, host-version range, or target framework is incompatible with this host."
                    false
            )

type AdapterDescriptor =
    private
        { Id: string
          DisplayName: string
          Description: string
          PackageVersion: Version
          Kind: AdapterKind
          Compatibility: AdapterCompatibility
          Capabilities: Set<string>
          ShutdownTimeout: TimeSpan }

[<RequireQualifiedAccess>]
module AdapterDescriptor =
    let private canonical argumentName value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg argumentName "Adapter descriptor values must be canonical and non-blank."

        value

    let create id displayName description packageVersion kind compatibility capabilities shutdownTimeout =
        canonical (nameof id) id |> ignore
        canonical (nameof displayName) displayName |> ignore
        canonical (nameof description) description |> ignore

        if shutdownTimeout <= TimeSpan.Zero then
            invalidArg (nameof shutdownTimeout) "Adapter shutdown timeout must be positive."

        let capabilities =
            capabilities |> Seq.map (canonical (nameof capabilities)) |> Set.ofSeq

        { Id = id
          DisplayName = displayName
          Description = description
          PackageVersion = packageVersion
          Kind = kind
          Compatibility = compatibility
          Capabilities = capabilities
          ShutdownTimeout = shutdownTimeout }

    let id value = value.Id
    let displayName value = value.DisplayName
    let description value = value.Description
    let packageVersion value = value.PackageVersion
    let kind value = value.Kind
    let compatibility value = value.Compatibility
    let capabilities value = value.Capabilities
    let shutdownTimeout value = value.ShutdownTimeout

type AdapterSecretReference =
    private
        { Name: string
          Provider: string
          Key: string }

[<RequireQualifiedAccess>]
module AdapterSecretReference =
    let create name provider key =
        for argumentName, value in [ nameof name, name; nameof provider, provider; nameof key, key ] do
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg argumentName "Adapter secret references must be canonical and non-blank."

        { Name = name
          Provider = provider
          Key = key }

    let name value = value.Name
    let provider value = value.Provider
    let key value = value.Key

type AdapterConfiguration =
    private
        { Values: Map<string, string>
          SecretReferences: AdapterSecretReference list }

[<RequireQualifiedAccess>]
module AdapterConfiguration =
    let private sensitiveName =
        Regex(
            "(^|[_.-])(api[_-]?key|credential|password|private[_-]?key|secret|token)($|[_.-])",
            RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant
        )

    let empty =
        { Values = Map.empty
          SecretReferences = [] }

    let create values secretReferences =
        values
        |> Map.iter (fun name value ->
            if String.IsNullOrWhiteSpace name || name <> name.Trim() then
                invalidArg (nameof values) "Adapter configuration names must be canonical and non-blank."

            if isNull value then
                nullArg (nameof values)

            if sensitiveName.IsMatch name then
                invalidArg
                    (nameof values)
                    "Secret material cannot enter adapter configuration; use a named secret reference.")

        let names = secretReferences |> List.map AdapterSecretReference.name

        if names.Length <> (names |> List.distinct |> List.length) then
            invalidArg (nameof secretReferences) "Adapter secret reference names must be unique."

        { Values = values
          SecretReferences = secretReferences }

    let values value = value.Values
    let secretReferences value = value.SecretReferences

[<RequireQualifiedAccess>]
type AdapterHealth =
    | Ready
    | Degraded of reasons: string list
    | Unhealthy of reasons: string list

type AdapterRuntime =
    { HealthAsync: CancellationToken -> Task<Result<AdapterHealth, AdapterFailure>>
      StopAsync: CancellationToken -> Task<Result<unit, AdapterFailure>> }

type AdapterActivationContext =
    private
        { InstanceId: string
          Host: AdapterHostCompatibility
          AvailableServices: Set<string> }

[<RequireQualifiedAccess>]
module AdapterActivationContext =
    let create instanceId host availableServices =
        if String.IsNullOrWhiteSpace instanceId || instanceId <> instanceId.Trim() then
            invalidArg (nameof instanceId) "Adapter instance identity must be canonical and non-blank."

        let services =
            availableServices
            |> Seq.map (fun service ->
                if String.IsNullOrWhiteSpace service || service <> service.Trim() then
                    invalidArg (nameof availableServices) "Available service names must be canonical and non-blank."

                service)
            |> Set.ofSeq

        { InstanceId = instanceId
          Host = host
          AvailableServices = services }

    let instanceId value = value.InstanceId
    let host value = value.Host
    let availableServices value = value.AvailableServices

type CompiledAdapterRegistration =
    { Descriptor: AdapterDescriptor
      ValidateConfiguration: AdapterConfiguration -> Result<unit, AdapterFailure list>
      StartAsync: AdapterActivationContext -> AdapterConfiguration -> CancellationToken -> Task<AdapterRuntime> }

type AdapterRegistry(host: AdapterHostCompatibility) =
    let sync = obj ()

    let mutable registrations =
        Map.empty<AdapterKind * string, CompiledAdapterRegistration>

    member _.Register(registration) =
        match AdapterHostCompatibility.validate host (AdapterDescriptor.compatibility registration.Descriptor) with
        | Error failure -> Error failure
        | Ok() ->
            let key =
                AdapterDescriptor.kind registration.Descriptor, AdapterDescriptor.id registration.Descriptor

            lock sync (fun () ->
                if Map.containsKey key registrations then
                    Error(
                        AdapterFailure.create
                            AdapterFailureCode.DuplicateRegistration
                            $"Adapter '{snd key}' is already registered for category '{fst key}'."
                            false
                    )
                else
                    registrations <- Map.add key registration registrations
                    Ok())

    member _.Descriptors =
        lock sync (fun () ->
            registrations
            |> Map.values
            |> Seq.map _.Descriptor
            |> Seq.sortBy (fun descriptor -> AdapterDescriptor.kind descriptor, AdapterDescriptor.id descriptor)
            |> Seq.toList)

    member _.Resolve(kind, id) =
        lock sync (fun () -> Map.tryFind (kind, id) registrations)

    member this.ActivateAsync(kind, id, context, configuration, cancellationToken) =
        task {
            match this.Resolve(kind, id) with
            | None ->
                return
                    Error(
                        AdapterFailure.create
                            AdapterFailureCode.NotRegistered
                            $"Adapter '{id}' is not registered for category '{kind}'."
                            false
                    )
            | Some registration ->
                let validation =
                    try
                        registration.ValidateConfiguration configuration
                    with exceptionValue ->
                        Error
                            [ AdapterFailure.create
                                  AdapterFailureCode.InvalidConfiguration
                                  $"Adapter '{id}' configuration validation failed: {exceptionValue.Message}"
                                  false ]

                match validation with
                | Error failures ->
                    return
                        Error(
                            failures
                            |> List.tryHead
                            |> Option.defaultValue (
                                AdapterFailure.create
                                    AdapterFailureCode.InvalidConfiguration
                                    "Adapter configuration validation failed without evidence."
                                    false
                            )
                        )
                | Ok() ->
                    try
                        let! runtime = registration.StartAsync context configuration cancellationToken

                        let healthAsync runtimeCancellationToken =
                            task {
                                try
                                    return! runtime.HealthAsync runtimeCancellationToken
                                with
                                | :? OperationCanceledException when runtimeCancellationToken.IsCancellationRequested ->
                                    return raise (OperationCanceledException(runtimeCancellationToken))
                                | exceptionValue ->
                                    return
                                        Error(
                                            AdapterFailure.create
                                                AdapterFailureCode.HealthCheckFailed
                                                $"Adapter '{id}' health check failed: {exceptionValue.Message}"
                                                true
                                        )
                            }

                        let stopAsync runtimeCancellationToken =
                            task {
                                try
                                    return! runtime.StopAsync runtimeCancellationToken
                                with
                                | :? OperationCanceledException when runtimeCancellationToken.IsCancellationRequested ->
                                    return raise (OperationCanceledException(runtimeCancellationToken))
                                | exceptionValue ->
                                    return
                                        Error(
                                            AdapterFailure.create
                                                AdapterFailureCode.ShutdownFailed
                                                $"Adapter '{id}' shutdown failed: {exceptionValue.Message}"
                                                false
                                        )
                            }

                        return
                            Ok
                                { HealthAsync = healthAsync
                                  StopAsync = stopAsync }
                    with
                    | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                        return! Task.FromCanceled<Result<AdapterRuntime, AdapterFailure>>(cancellationToken)
                    | exceptionValue ->
                        return
                            Error(
                                AdapterFailure.create
                                    AdapterFailureCode.StartupFailed
                                    $"Adapter '{id}' failed to start: {exceptionValue.Message}"
                                    false
                            )
        }

[<RequireQualifiedAccess>]
type AdapterConformanceCheck =
    | Compatibility
    | ValidConfiguration
    | InvalidConfiguration
    | Activation
    | Health
    | Shutdown
    | IdempotentShutdown

type AdapterConformanceCheckResult =
    { Check: AdapterConformanceCheck
      Passed: bool
      Failure: AdapterFailure option }

type AdapterConformanceReport =
    { AdapterId: string
      Kind: AdapterKind
      Checks: AdapterConformanceCheckResult list }

[<RequireQualifiedAccess>]
module AdapterConformanceReport =
    let passed value = value.Checks |> List.forall _.Passed

[<RequireQualifiedAccess>]
module AdapterConformance =
    let private passed check =
        { Check = check
          Passed = true
          Failure = None }

    let private failed check failure =
        { Check = check
          Passed = false
          Failure = Some failure }

    let runAsync host context registration validConfiguration invalidConfigurations cancellationToken =
        task {
            let checks = ResizeArray<AdapterConformanceCheckResult>()
            let registry = AdapterRegistry host

            match registry.Register registration with
            | Error failure -> checks.Add(failed AdapterConformanceCheck.Compatibility failure)
            | Ok() -> checks.Add(passed AdapterConformanceCheck.Compatibility)

            match registration.ValidateConfiguration validConfiguration with
            | Ok() -> checks.Add(passed AdapterConformanceCheck.ValidConfiguration)
            | Error failures ->
                checks.Add(
                    failed
                        AdapterConformanceCheck.ValidConfiguration
                        (failures
                         |> List.tryHead
                         |> Option.defaultValue (
                             AdapterFailure.create
                                 AdapterFailureCode.InvalidConfiguration
                                 "Valid adapter configuration was rejected without evidence."
                                 false
                         ))
                )

            let invalidRejected =
                not (List.isEmpty invalidConfigurations)
                && invalidConfigurations
                   |> List.forall (registration.ValidateConfiguration >> Result.isError)

            if invalidRejected then
                checks.Add(passed AdapterConformanceCheck.InvalidConfiguration)
            else
                checks.Add(
                    failed
                        AdapterConformanceCheck.InvalidConfiguration
                        (AdapterFailure.create
                            AdapterFailureCode.InvalidConfiguration
                            "Conformance requires at least one rejected invalid configuration."
                            false)
                )

            let! activation =
                registry.ActivateAsync(
                    AdapterDescriptor.kind registration.Descriptor,
                    AdapterDescriptor.id registration.Descriptor,
                    context,
                    validConfiguration,
                    cancellationToken
                )

            match activation with
            | Error failure -> checks.Add(failed AdapterConformanceCheck.Activation failure)
            | Ok runtime ->
                checks.Add(passed AdapterConformanceCheck.Activation)

                try
                    let! healthResult = runtime.HealthAsync cancellationToken

                    match healthResult with
                    | Error failure -> checks.Add(failed AdapterConformanceCheck.Health failure)
                    | Ok AdapterHealth.Ready -> checks.Add(passed AdapterConformanceCheck.Health)
                    | Ok(AdapterHealth.Degraded reasons)
                    | Ok(AdapterHealth.Unhealthy reasons) ->
                        let detail = String.concat "; " reasons

                        checks.Add(
                            failed
                                AdapterConformanceCheck.Health
                                (AdapterFailure.create
                                    AdapterFailureCode.HealthCheckFailed
                                    $"Adapter conformance health was not ready: {detail}"
                                    true)
                        )
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    raise (OperationCanceledException(cancellationToken))
                | exceptionValue ->
                    checks.Add(
                        failed
                            AdapterConformanceCheck.Health
                            (AdapterFailure.create
                                AdapterFailureCode.HealthCheckFailed
                                $"Adapter health check failed: {exceptionValue.Message}"
                                true)
                    )

                let stop check =
                    task {
                        try
                            use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken

                            timeout.CancelAfter(AdapterDescriptor.shutdownTimeout registration.Descriptor)
                            let! stopped = runtime.StopAsync timeout.Token

                            match stopped with
                            | Ok() -> checks.Add(passed check)
                            | Error failure -> checks.Add(failed check failure)
                        with
                        | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                            raise (OperationCanceledException(cancellationToken))
                        | exceptionValue ->
                            checks.Add(
                                failed
                                    check
                                    (AdapterFailure.create
                                        AdapterFailureCode.ShutdownFailed
                                        $"Adapter shutdown failed: {exceptionValue.Message}"
                                        false)
                            )
                    }

                do! stop AdapterConformanceCheck.Shutdown
                do! stop AdapterConformanceCheck.IdempotentShutdown

            return
                { AdapterId = AdapterDescriptor.id registration.Descriptor
                  Kind = AdapterDescriptor.kind registration.Descriptor
                  Checks = checks |> Seq.toList }
        }

[<RequireQualifiedAccess>]
type DynamicAdapterIsolation =
    | Process
    | Container

type DynamicAdapterManifest =
    private
        { Descriptor: AdapterDescriptor
          ArtifactSha256: string
          Signer: string
          SignatureAlgorithm: string
          Dependencies: Map<string, string>
          Isolation: DynamicAdapterIsolation }

[<RequireQualifiedAccess>]
module DynamicAdapterManifest =
    let private sha256 (value: string) =
        Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let create descriptor artifactSha256 signer signatureAlgorithm dependencies isolation =
        if not (sha256 artifactSha256) then
            invalidArg (nameof artifactSha256) "Dynamic adapter artifact identity must be lowercase SHA-256."

        for argumentName, value in [ nameof signer, signer; nameof signatureAlgorithm, signatureAlgorithm ] do
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg argumentName "Dynamic adapter signature values must be canonical and non-blank."

        dependencies
        |> Map.iter (fun name digest ->
            if String.IsNullOrWhiteSpace name || name <> name.Trim() || not (sha256 digest) then
                invalidArg
                    (nameof dependencies)
                    "Dynamic adapter dependencies require canonical names and lowercase SHA-256 identities.")

        { Descriptor = descriptor
          ArtifactSha256 = artifactSha256
          Signer = signer
          SignatureAlgorithm = signatureAlgorithm
          Dependencies = dependencies
          Isolation = isolation }

    let descriptor value = value.Descriptor
    let artifactSha256 value = value.ArtifactSha256
    let signer value = value.Signer
    let signatureAlgorithm value = value.SignatureAlgorithm
    let dependencies value = value.Dependencies
    let isolation value = value.Isolation

type DynamicAdapterTrustPolicy =
    private
        { TrustedSigners: Set<string>
          AllowedKinds: Set<AdapterKind>
          ApprovedDependencies: Map<string, string>
          AllowedIsolation: Set<DynamicAdapterIsolation> }

[<RequireQualifiedAccess>]
module DynamicAdapterTrustPolicy =
    let create trustedSigners allowedKinds approvedDependencies allowedIsolation =
        if Set.isEmpty trustedSigners then
            invalidArg (nameof trustedSigners) "Dynamic adapter policy requires at least one trusted signer."

        if Set.isEmpty allowedKinds then
            invalidArg (nameof allowedKinds) "Dynamic adapter policy requires at least one allowed category."

        if Set.isEmpty allowedIsolation then
            invalidArg (nameof allowedIsolation) "Dynamic adapter policy requires process or container isolation."

        { TrustedSigners = trustedSigners
          AllowedKinds = allowedKinds
          ApprovedDependencies = approvedDependencies
          AllowedIsolation = allowedIsolation }

type AdapterSignatureVerifier =
    { VerifyAsync: DynamicAdapterManifest -> CancellationToken -> Task<bool> }

type DynamicAdapterPlan =
    private
        { Manifest: DynamicAdapterManifest
          ArtifactSha256: string
          Isolation: DynamicAdapterIsolation }

[<RequireQualifiedAccess>]
module DynamicAdapterPlan =
    let manifest value = value.Manifest
    let artifactSha256 value = value.ArtifactSha256
    let isolation value = value.Isolation

[<RequireQualifiedAccess>]
module DynamicAdapterAdmission =
    let validateAsync host policy verifier manifest cancellationToken =
        task {
            match
                AdapterHostCompatibility.validate
                    host
                    (manifest |> DynamicAdapterManifest.descriptor |> AdapterDescriptor.compatibility)
            with
            | Error failure -> return Error failure
            | Ok() ->
                let descriptor = DynamicAdapterManifest.descriptor manifest

                if not (policy.TrustedSigners.Contains(DynamicAdapterManifest.signer manifest)) then
                    return
                        Error(
                            AdapterFailure.create
                                AdapterFailureCode.TrustRejected
                                "Dynamic adapter signer is not trusted."
                                false
                        )
                elif not (policy.AllowedKinds.Contains(AdapterDescriptor.kind descriptor)) then
                    return
                        Error(
                            AdapterFailure.create
                                AdapterFailureCode.TrustRejected
                                "Dynamic adapter category is not allowed by policy."
                                false
                        )
                elif not (policy.AllowedIsolation.Contains(DynamicAdapterManifest.isolation manifest)) then
                    return
                        Error(
                            AdapterFailure.create
                                AdapterFailureCode.IsolationRejected
                                "Dynamic adapters require an explicitly allowed process or container boundary."
                                false
                        )
                elif
                    DynamicAdapterManifest.dependencies manifest
                    |> Map.exists (fun name digest -> policy.ApprovedDependencies |> Map.tryFind name <> Some digest)
                then
                    return
                        Error(
                            AdapterFailure.create
                                AdapterFailureCode.DependencyRejected
                                "Dynamic adapter dependency identity is not approved."
                                false
                        )
                else
                    try
                        let! verified = verifier.VerifyAsync manifest cancellationToken

                        if not verified then
                            return
                                Error(
                                    AdapterFailure.create
                                        AdapterFailureCode.TrustRejected
                                        "Dynamic adapter signature verification failed."
                                        false
                                )
                        else
                            return
                                Ok
                                    { Manifest = manifest
                                      ArtifactSha256 = DynamicAdapterManifest.artifactSha256 manifest
                                      Isolation = DynamicAdapterManifest.isolation manifest }
                    with
                    | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                        return raise (OperationCanceledException(cancellationToken))
                    | exceptionValue ->
                        return
                            Error(
                                AdapterFailure.create
                                    AdapterFailureCode.TrustRejected
                                    $"Dynamic adapter signature verification failed: {exceptionValue.Message}"
                                    true
                            )
        }
