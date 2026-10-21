namespace Nao.Providers.Tests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Providers

[<TestClass>]
type ProviderCapabilityTests() =
    let price = ProviderModelPrice.create "USD" 1m 2m

    let model =
        ProviderModelMetadata.create
            "model"
            ProviderQualityTier.Standard
            ProviderLatencyClass.Interactive
            (Set [ "eu-west" ])
            ProviderPrivacyClass.ZeroRetention
            price
            ProviderDeprecation.Active

    let capabilities overrides context usage =
        ProviderCapability.all
        |> List.map (fun capability -> capability, CapabilitySupport.Unknown)
        |> Map
        |> fun values -> overrides |> Map.fold (fun state key value -> Map.add key value state) values
        |> fun values -> ProviderCapabilities.create values context usage

    let observation version capabilities health readiness quota maintenance =
        ProviderCapabilityObservation.create
            "endpoint"
            version
            DateTimeOffset.UtcNow
            CapabilityObservationSource.Configured
            capabilities
            model
            health
            readiness
            quota
            maintenance

    let provider invoked =
        LlmProvider.create (fun () -> "test") (fun _ _ _ _ ->
            invoked ()

            CompletionResult.text "ok" ProviderFinishReason.Stop ProviderUsage.None
            |> Task.FromResult)

    [<TestMethod>]
    member _.``Unknown capability fails before provider invocation``() : Task =
        task {
            let mutable invoked = false

            let current =
                observation
                    "v1"
                    ProviderCapabilities.unknown
                    ProviderEndpointHealth.Healthy
                    ProviderEndpointReadiness.Ready
                    ProviderQuotaStatus.Available
                    ProviderMaintenanceStatus.Active

            let endpoint =
                ProviderEndpoint.create
                    (provider (fun () -> invoked <- true))
                    (fun _ -> Task.FromResult current)
                    (fun _ -> Task.CompletedTask)
                    (fun () -> DateTimeOffset.UtcNow)

            let requirements =
                ProviderRequirements.create (Set [ ProviderCapability.ToolCalling ]) None None

            let! result =
                ProviderEndpoint.completeAsync
                    requirements
                    (CorrelationContext.root ())
                    []
                    CompletionOptions.Default
                    CancellationToken.None
                    endpoint

            match result with
            | Error(ProviderInvocationFailure.Incompatible [ ProviderCompatibilityFailure.UnknownCapability ProviderCapability.ToolCalling ]) ->
                ()
            | other -> Assert.Fail($"Unexpected compatibility result {other}.")

            Assert.IsFalse invoked
            ProviderEndpoint.dispose endpoint
        }

    [<TestMethod>]
    member _.``Temporary endpoint failure is distinct from unsupported capability``() : Task =
        task {
            let configured =
                capabilities
                    (Map
                        [ ProviderCapability.Chat, CapabilitySupport.Supported
                          ProviderCapability.ToolCalling, CapabilitySupport.Unsupported
                          ProviderCapability.UsageReporting, CapabilitySupport.Unsupported ])
                    (Some 8192)
                    UsageReportingLevel.None

            let current =
                observation
                    "v1"
                    configured
                    ProviderEndpointHealth.Unavailable
                    ProviderEndpointReadiness.NotReady
                    ProviderQuotaStatus.Available
                    ProviderMaintenanceStatus.Active

            let endpoint =
                ProviderEndpoint.create
                    (provider ignore)
                    (fun _ -> Task.FromResult current)
                    (fun _ -> Task.CompletedTask)
                    (fun () -> DateTimeOffset.UtcNow)

            let requirements =
                ProviderRequirements.create (Set [ ProviderCapability.ToolCalling ]) None None

            let! result = ProviderEndpoint.evaluateAsync requirements CancellationToken.None endpoint

            match result with
            | Error(ProviderInvocationFailure.Incompatible failures) ->
                CollectionAssert.Contains(
                    failures |> List.toArray,
                    ProviderCompatibilityFailure.UnsupportedCapability ProviderCapability.ToolCalling
                )

                CollectionAssert.Contains(
                    failures |> List.toArray,
                    ProviderCompatibilityFailure.EndpointUnhealthy ProviderEndpointHealth.Unavailable
                )
            | Ok _ -> Assert.Fail("Expected an incompatible endpoint.")

            ProviderEndpoint.dispose endpoint
        }

    [<TestMethod>]
    member _.``Capability activation and version changes are audited without request content``() : Task =
        task {
            let audits = ConcurrentQueue<ProviderCapabilityAudit>()

            let first =
                observation
                    "v1"
                    ProviderCapabilities.unknown
                    ProviderEndpointHealth.Healthy
                    ProviderEndpointReadiness.Ready
                    ProviderQuotaStatus.Unknown
                    ProviderMaintenanceStatus.Active

            let secondCapabilities =
                capabilities
                    (Map
                        [ ProviderCapability.Chat, CapabilitySupport.Supported
                          ProviderCapability.UsageReporting, CapabilitySupport.Unsupported ])
                    (Some 4096)
                    UsageReportingLevel.None

            let second =
                observation
                    "v2"
                    secondCapabilities
                    ProviderEndpointHealth.Healthy
                    ProviderEndpointReadiness.Ready
                    ProviderQuotaStatus.Available
                    ProviderMaintenanceStatus.Active

            let mutable current = first

            let endpoint =
                ProviderEndpoint.create
                    (provider ignore)
                    (fun _ -> Task.FromResult current)
                    (fun audit ->
                        audits.Enqueue audit
                        Task.CompletedTask)
                    (fun () -> DateTimeOffset.UtcNow)

            let! _ = ProviderEndpoint.refreshAsync CancellationToken.None endpoint
            current <- second
            let! _ = ProviderEndpoint.refreshAsync CancellationToken.None endpoint
            let! _ = ProviderEndpoint.refreshAsync CancellationToken.None endpoint
            let values = audits.ToArray()

            Assert.AreEqual(2, values.Length)
            Assert.AreEqual(ProviderCapabilityAuditKind.Activated, values.[0].Kind)
            Assert.AreEqual(ProviderCapabilityAuditKind.Changed, values.[1].Kind)
            Assert.AreEqual(Some "v1", values.[1].PreviousVersion)
            CollectionAssert.Contains(values.[1].ChangedCapabilities |> List.toArray, ProviderCapability.Chat)
            Assert.IsFalse((sprintf "%A" values).Contains("prompt"))
            ProviderEndpoint.dispose endpoint
        }

    [<TestMethod>]
    member _.``Factory activation uses caller-owned capability observation without model heuristics``() : Task =
        task {
            let configured =
                capabilities
                    (Map
                        [ ProviderCapability.Chat, CapabilitySupport.Supported
                          ProviderCapability.Streaming, CapabilitySupport.Supported
                          ProviderCapability.UsageReporting, CapabilitySupport.Unsupported ])
                    (Some 32768)
                    UsageReportingLevel.None

            let current =
                observation
                    "configuration-v1"
                    configured
                    ProviderEndpointHealth.Healthy
                    ProviderEndpointReadiness.Ready
                    ProviderQuotaStatus.Available
                    ProviderMaintenanceStatus.Active

            let audits = ConcurrentQueue<ProviderCapabilityAudit>()

            let endpointConfiguration =
                ProviderEndpointConfiguration.configured
                    current
                    (fun audit ->
                        audits.Enqueue audit
                        Task.CompletedTask)
                    (fun () -> DateTimeOffset.UtcNow)

            let authorization =
                SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
                |> fun principal -> AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
                |> Option.get

            let! endpoint, credential =
                ProviderFactory.activateEndpointAsync
                    None
                    authorization
                    (Ollama OllamaConfig.Default)
                    endpointConfiguration
                    CancellationToken.None

            let requirements =
                ProviderRequirements.create
                    (Set [ ProviderCapability.Chat; ProviderCapability.Streaming ])
                    (Some 8192)
                    None

            let! result = ProviderEndpoint.evaluateAsync requirements CancellationToken.None endpoint
            Assert.IsTrue(Result.isOk result)
            Assert.AreEqual(None, credential)
            Assert.AreEqual(1, audits.Count)
            ProviderEndpoint.dispose endpoint
        }
