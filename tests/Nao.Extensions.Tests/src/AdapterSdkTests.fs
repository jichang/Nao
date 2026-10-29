namespace Nao.Extensions.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Extensions

[<TestClass; TestCategory("Compatibility")>]
type AdapterSdkTests() =
    let errorValue =
        function
        | Error failure -> failure
        | Ok _ -> invalidOp "Expected an error result."

    let compatibility =
        AdapterCompatibility.create (Version(1, 0, 0)) 1 (Version(1, 0, 0)) (Version(1, 0, 0)) "net10.0"

    let configuration valid =
        AdapterConfiguration.create (Map [ "valid", string valid ]) []

    let descriptor kind id =
        AdapterDescriptor.create
            id
            id
            $"Conformance fixture for {kind}."
            (Version(1, 0, 0))
            kind
            compatibility
            [ "fixture" ]
            (TimeSpan.FromSeconds 1.0)

    let registration kind =
        let mutable stopped = false

        { Descriptor = descriptor kind $"fixture-{kind.ToString().ToLowerInvariant()}"
          ValidateConfiguration =
            fun value ->
                if AdapterConfiguration.values value |> Map.tryFind "valid" = Some "True" then
                    Ok()
                else
                    Error
                        [ AdapterFailure.create
                              AdapterFailureCode.InvalidConfiguration
                              "Fixture configuration is invalid."
                              false ]
          StartAsync =
            fun _ _ _ ->
                stopped <- false

                Task.FromResult
                    { HealthAsync = fun _ -> Task.FromResult(Ok AdapterHealth.Ready)
                      StopAsync =
                        fun _ ->
                            stopped <- true
                            Task.FromResult(Ok()) } }

    let context =
        AdapterActivationContext.create "fixture-instance" AdapterHostCompatibility.current [ "logging" ]

    [<TestMethod>]
    member _.EveryAdapterCategoryPassesThePublicConformanceSuite() : Task =
        task {
            let kinds =
                [ AdapterKind.Tool
                  AdapterKind.Provider
                  AdapterKind.Storage
                  AdapterKind.Parser
                  AdapterKind.Connector
                  AdapterKind.Retriever
                  AdapterKind.Telemetry
                  AdapterKind.Identity
                  AdapterKind.Reasoner ]

            for kind in kinds do
                let! report =
                    AdapterConformance.runAsync
                        AdapterHostCompatibility.current
                        context
                        (registration kind)
                        (configuration true)
                        [ configuration false ]
                        CancellationToken.None

                Assert.IsTrue(
                    AdapterConformanceReport.passed report,
                    $"Conformance failed for adapter category {kind}."
                )

                Assert.AreEqual(7, report.Checks.Length)
        }

    [<TestMethod>]
    member _.CompatibilityMismatchFailsBeforeRegistrationOrRouting() =
        let incompatible =
            AdapterCompatibility.create (Version(2, 0, 0)) 1 (Version(1, 0, 0)) (Version(2, 0, 0)) "net10.0"

        let value =
            { registration AdapterKind.Provider with
                Descriptor =
                    AdapterDescriptor.create
                        "incompatible"
                        "Incompatible"
                        "Requires another SDK."
                        (Version(1, 0, 0))
                        AdapterKind.Provider
                        incompatible
                        []
                        (TimeSpan.FromSeconds 1.0) }

        let registry = AdapterRegistry AdapterHostCompatibility.current
        let failure = registry.Register value |> errorValue

        Assert.AreEqual(AdapterFailureCode.CompatibilityMismatch, AdapterFailure.code failure)
        Assert.IsTrue(registry.Resolve(AdapterKind.Provider, "incompatible") |> Option.isNone)

    [<TestMethod>]
    member _.ConfigurationSeparatesSecretReferencesFromValues() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            AdapterConfiguration.create (Map [ "api_key", "secret-value" ]) [] |> ignore)
        |> ignore

        let reference =
            AdapterSecretReference.create "provider-key" "vault" "providers/example"

        let value =
            AdapterConfiguration.create (Map [ "endpoint", "https://example.invalid" ]) [ reference ]

        Assert.AreEqual(1, AdapterConfiguration.secretReferences value |> List.length)

    [<TestMethod>]
    member _.StartupFailureUsesTheCommonFailureContract() : Task =
        task {
            let broken =
                { registration AdapterKind.Storage with
                    StartAsync =
                        fun _ _ _ -> Task.FromException<AdapterRuntime>(InvalidOperationException "storage unavailable") }

            let registry = AdapterRegistry AdapterHostCompatibility.current

            registry.Register broken
            |> Result.defaultWith (AdapterFailure.message >> invalidOp)

            let! result =
                registry.ActivateAsync(
                    AdapterKind.Storage,
                    AdapterDescriptor.id broken.Descriptor,
                    context,
                    configuration true,
                    CancellationToken.None
                )

            let failure = result |> errorValue
            Assert.AreEqual(AdapterFailureCode.StartupFailed, AdapterFailure.code failure)
            StringAssert.Contains(AdapterFailure.message failure, "storage unavailable")
        }

    [<TestMethod>]
    member _.RegistryReportsDuplicateAndMissingRegistrations() : Task =
        task {
            let value = registration AdapterKind.Tool
            let registry = AdapterRegistry AdapterHostCompatibility.current
            Assert.IsTrue(registry.Register value |> Result.isOk)

            let duplicate = registry.Register value |> errorValue
            Assert.AreEqual(AdapterFailureCode.DuplicateRegistration, AdapterFailure.code duplicate)

            let! missing =
                registry.ActivateAsync(AdapterKind.Tool, "missing", context, configuration true, CancellationToken.None)

            Assert.AreEqual(AdapterFailureCode.NotRegistered, missing |> errorValue |> AdapterFailure.code)
        }

    [<TestMethod>]
    member _.LifecycleExceptionsUseCommonFailuresAndCancellationPropagates() : Task =
        task {
            let broken =
                { registration AdapterKind.Telemetry with
                    StartAsync =
                        fun _ _ _ ->
                            Task.FromResult
                                { HealthAsync =
                                    fun _ ->
                                        Task.FromException<Result<AdapterHealth, AdapterFailure>>(
                                            InvalidOperationException "health unavailable"
                                        )
                                  StopAsync =
                                    fun _ ->
                                        Task.FromException<Result<unit, AdapterFailure>>(
                                            InvalidOperationException "shutdown unavailable"
                                        ) } }

            let registry = AdapterRegistry AdapterHostCompatibility.current
            Assert.IsTrue(registry.Register broken |> Result.isOk)

            let! activated =
                registry.ActivateAsync(
                    AdapterKind.Telemetry,
                    AdapterDescriptor.id broken.Descriptor,
                    context,
                    configuration true,
                    CancellationToken.None
                )

            let runtime = activated |> Result.defaultWith (AdapterFailure.message >> invalidOp)
            let! health = runtime.HealthAsync CancellationToken.None
            let! stopped = runtime.StopAsync CancellationToken.None
            Assert.AreEqual(AdapterFailureCode.HealthCheckFailed, health |> errorValue |> AdapterFailure.code)
            Assert.AreEqual(AdapterFailureCode.ShutdownFailed, stopped |> errorValue |> AdapterFailure.code)

            let cancelled =
                { registration AdapterKind.Identity with
                    StartAsync = fun _ _ cancellationToken -> Task.FromCanceled<AdapterRuntime>(cancellationToken) }

            let cancelledRegistry = AdapterRegistry AdapterHostCompatibility.current
            Assert.IsTrue(cancelledRegistry.Register cancelled |> Result.isOk)
            use source = new CancellationTokenSource()
            source.Cancel()

            let activation () =
                cancelledRegistry.ActivateAsync(
                    AdapterKind.Identity,
                    AdapterDescriptor.id cancelled.Descriptor,
                    context,
                    configuration true,
                    source.Token
                )
                :> Task

            let! _ = Assert.ThrowsAsync<OperationCanceledException>(activation)
            ()
        }

    [<TestMethod>]
    member _.DynamicAdaptersRequireTrustDependenciesSignatureAndIsolation() : Task =
        task {
            let manifest =
                DynamicAdapterManifest.create
                    (descriptor AdapterKind.Parser "dynamic-parser")
                    (String('a', 64))
                    "trusted-signer"
                    "sigstore"
                    (Map [ "runtime", String('b', 64) ])
                    DynamicAdapterIsolation.Process

            let policy =
                DynamicAdapterTrustPolicy.create
                    (Set.singleton "trusted-signer")
                    (Set.singleton AdapterKind.Parser)
                    (Map [ "runtime", String('b', 64) ])
                    (Set.ofList [ DynamicAdapterIsolation.Process; DynamicAdapterIsolation.Container ])

            let verifier = { VerifyAsync = fun _ _ -> Task.FromResult true }

            let! admitted =
                DynamicAdapterAdmission.validateAsync
                    AdapterHostCompatibility.current
                    policy
                    verifier
                    manifest
                    CancellationToken.None

            let plan = admitted |> Result.defaultWith (AdapterFailure.message >> invalidOp)
            Assert.AreEqual(DynamicAdapterIsolation.Process, DynamicAdapterPlan.isolation plan)

            let untrustedPolicy =
                DynamicAdapterTrustPolicy.create
                    (Set.singleton "other-signer")
                    (Set.singleton AdapterKind.Parser)
                    (Map [ "runtime", String('b', 64) ])
                    (Set.singleton DynamicAdapterIsolation.Process)

            let! rejected =
                DynamicAdapterAdmission.validateAsync
                    AdapterHostCompatibility.current
                    untrustedPolicy
                    verifier
                    manifest
                    CancellationToken.None

            Assert.AreEqual(AdapterFailureCode.TrustRejected, rejected |> errorValue |> AdapterFailure.code)

            let mismatchedManifest =
                DynamicAdapterManifest.create
                    (descriptor AdapterKind.Parser "mismatched-parser")
                    (String('c', 64))
                    "trusted-signer"
                    "sigstore"
                    (Map [ "runtime", String('d', 64) ])
                    DynamicAdapterIsolation.Container

            let! dependencyRejected =
                DynamicAdapterAdmission.validateAsync
                    AdapterHostCompatibility.current
                    policy
                    verifier
                    mismatchedManifest
                    CancellationToken.None

            Assert.AreEqual(
                AdapterFailureCode.DependencyRejected,
                dependencyRejected |> errorValue |> AdapterFailure.code
            )

            let failedVerifier = { VerifyAsync = fun _ _ -> Task.FromResult false }

            let! signatureRejected =
                DynamicAdapterAdmission.validateAsync
                    AdapterHostCompatibility.current
                    policy
                    failedVerifier
                    manifest
                    CancellationToken.None

            Assert.AreEqual(AdapterFailureCode.TrustRejected, signatureRejected |> errorValue |> AdapterFailure.code)
        }
