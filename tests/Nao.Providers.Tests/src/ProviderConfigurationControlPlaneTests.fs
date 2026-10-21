namespace Nao.Providers.Tests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Providers

[<TestClass>]
type ProviderConfigurationControlPlaneTests() =
    let get =
        function
        | Ok value -> value
        | Error failure -> failwith $"Unexpected failure: {failure}."

    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate
                principal
                None
                (WorkspaceId.parse "workspace")
                (Some(SessionId.parse "session"))
        |> Option.get

    let policy allowed denied =
        ProviderModelAccessRule.create allowed denied
        |> fun rule ->
            ProviderModelPolicy.create (
                Map [ (AuthorizationScope.tenantId authorization, AuthorizationScope.workspaceId authorization), rule ]
            )

    let capabilities =
        ProviderCapability.all
        |> List.map (fun capability -> capability, CapabilitySupport.Supported)
        |> Map
        |> fun support -> ProviderCapabilities.create support (Some 32768) UsageReportingLevel.Full

    let observation endpoint version health =
        ProviderCapabilityObservation.create
            endpoint
            version
            DateTimeOffset.UtcNow
            CapabilityObservationSource.Configured
            capabilities
            (ProviderModelMetadata.create
                "model"
                ProviderQualityTier.Standard
                ProviderLatencyClass.Interactive
                (Set [ "eu-west" ])
                ProviderPrivacyClass.ZeroRetention
                (ProviderModelPrice.create "USD" 1m 2m)
                ProviderDeprecation.Active)
            health
            ProviderEndpointReadiness.Ready
            ProviderQuotaStatus.Available
            ProviderMaintenanceStatus.Active

    let configuration version modelPolicy =
        let endpoint =
            ProviderEndpointConfiguration.configured
                (observation version version ProviderEndpointHealth.Healthy)
                (fun _ -> Task.CompletedTask)
                (fun () -> DateTimeOffset.UtcNow)

        ProviderRuntimeConfiguration.create
            version
            (Ollama
                { OllamaConfig.Default with
                    Model = "model" })
            endpoint
            modelPolicy

    let endpoint version health complete =
        ProviderEndpoint.create
            (LlmProvider.create (fun () -> version) complete)
            (fun _ -> Task.FromResult(observation version version health))
            (fun _ -> Task.CompletedTask)
            (fun () -> DateTimeOffset.UtcNow)

    let requirements =
        ProviderRequirements.create (Set [ ProviderCapability.Chat ]) None None

    [<TestMethod>]
    member _.``Invalid candidate cannot replace the last known-good configuration``() : Task =
        task {
            let audits = ConcurrentQueue<ProviderConfigurationAudit>()
            let modelPolicy = policy (Some(Set [ "model" ])) Set.empty

            let activate config _ =
                let version = ProviderRuntimeConfiguration.version config

                let health =
                    if version = "v2" then
                        ProviderEndpointHealth.Unavailable
                    else
                        ProviderEndpointHealth.Healthy

                Task.FromResult(
                    endpoint version health (fun _ _ _ _ ->
                        CompletionResult.text version ProviderFinishReason.Stop ProviderUsage.None
                        |> Task.FromResult),
                    None
                )

            let! created =
                ProviderConfigurationControlPlane.createAsync
                    activate
                    (fun audit ->
                        audits.Enqueue audit
                        Task.CompletedTask)
                    (fun () -> DateTimeOffset.UtcNow)
                    (configuration "v1" modelPolicy)
                    CancellationToken.None

            let controlPlane = created |> get

            let! reload =
                ProviderConfigurationControlPlane.reloadAsync
                    (configuration "v2" modelPolicy)
                    CancellationToken.None
                    controlPlane

            Assert.AreEqual(
                Error(ProviderConfigurationFailure.ValidationFailed ProviderConfigurationRejection.EndpointUnhealthy),
                reload
            )

            Assert.AreEqual("v1", ProviderConfigurationControlPlane.currentVersion controlPlane)
            Assert.AreEqual(ProviderConfigurationAuditKind.Rejected, audits.ToArray() |> Array.last |> _.Kind)
            ProviderConfigurationControlPlane.dispose controlPlane
        }

    [<TestMethod>]
    member _.``Reload drains in-flight work audits affected execution and supports rollback``() : Task =
        task {
            let audits = ConcurrentQueue<ProviderConfigurationAudit>()

            let started =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let release =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let modelPolicy = policy (Some(Set [ "model" ])) Set.empty

            let activate config _ =
                let version = ProviderRuntimeConfiguration.version config

                let complete _ _ _ _ =
                    task {
                        if version = "v1" && not release.Task.IsCompleted then
                            started.TrySetResult() |> ignore
                            do! release.Task

                        return CompletionResult.text version ProviderFinishReason.Stop ProviderUsage.None
                    }

                Task.FromResult(endpoint version ProviderEndpointHealth.Healthy complete, None)

            let! created =
                ProviderConfigurationControlPlane.createAsync
                    activate
                    (fun audit ->
                        audits.Enqueue audit
                        Task.CompletedTask)
                    (fun () -> DateTimeOffset.UtcNow)
                    (configuration "v1" modelPolicy)
                    CancellationToken.None

            let controlPlane = created |> get
            let executionId = ExecutionId.generate ()

            let inFlight =
                ProviderConfigurationControlPlane.completeAsync
                    requirements
                    authorization
                    executionId
                    (CorrelationContext.root ())
                    []
                    CompletionOptions.Default
                    CancellationToken.None
                    controlPlane

            do! started.Task

            let! reload =
                ProviderConfigurationControlPlane.reloadAsync
                    (configuration "v2" modelPolicy)
                    CancellationToken.None
                    controlPlane

            Assert.AreEqual(Ok(), reload)

            let! current =
                ProviderConfigurationControlPlane.completeAsync
                    requirements
                    authorization
                    (ExecutionId.generate ())
                    (CorrelationContext.root ())
                    []
                    CompletionOptions.Default
                    CancellationToken.None
                    controlPlane

            Assert.AreEqual("v2", (current |> get).Content)

            let reloadAudit =
                audits
                |> Seq.find (fun audit -> audit.Kind = ProviderConfigurationAuditKind.Reloaded)

            Assert.AreEqual([ executionId ], reloadAudit.AffectedOperations |> List.map _.ExecutionId)
            release.TrySetResult() |> ignore
            let! original = inFlight
            Assert.AreEqual("v1", (original |> get).Content)

            let! rollback = ProviderConfigurationControlPlane.rollbackAsync CancellationToken.None controlPlane
            Assert.AreEqual(Ok(), rollback)
            Assert.AreEqual("v1", ProviderConfigurationControlPlane.currentVersion controlPlane)
            ProviderConfigurationControlPlane.dispose controlPlane
        }

    [<TestMethod>]
    member _.``Tenant workspace model policy fails closed before invocation``() : Task =
        task {
            let mutable invoked = false
            let modelPolicy = policy None (Set [ "model" ])

            let activate config _ =
                Task.FromResult(
                    endpoint
                        (ProviderRuntimeConfiguration.version config)
                        ProviderEndpointHealth.Healthy
                        (fun _ _ _ _ ->
                            invoked <- true

                            CompletionResult.text "unexpected" ProviderFinishReason.Stop ProviderUsage.None
                            |> Task.FromResult),
                    None
                )

            let! created =
                ProviderConfigurationControlPlane.createAsync
                    activate
                    (fun _ -> Task.CompletedTask)
                    (fun () -> DateTimeOffset.UtcNow)
                    (configuration "v1" modelPolicy)
                    CancellationToken.None

            let controlPlane = created |> get

            let! result =
                ProviderConfigurationControlPlane.completeAsync
                    requirements
                    authorization
                    (ExecutionId.generate ())
                    (CorrelationContext.root ())
                    []
                    CompletionOptions.Default
                    CancellationToken.None
                    controlPlane

            Assert.AreEqual(Error ManagedProviderInvocationFailure.ModelDenied, result)
            Assert.IsFalse invoked
            ProviderConfigurationControlPlane.dispose controlPlane
        }
