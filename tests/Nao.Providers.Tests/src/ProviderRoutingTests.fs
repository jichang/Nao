namespace Nao.Providers.Tests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Providers

[<TestClass>]
type ProviderRoutingTests() =
    let capabilities =
        ProviderCapability.all
        |> List.map (fun capability -> capability, CapabilitySupport.Supported)
        |> Map
        |> fun support -> ProviderCapabilities.create support (Some 32768) UsageReportingLevel.Full

    let observation endpoint model quality latency regions privacy inputPrice outputPrice health =
        ProviderCapabilityObservation.create
            endpoint
            "v1"
            DateTimeOffset.UtcNow
            CapabilityObservationSource.Configured
            capabilities
            (ProviderModelMetadata.create
                model
                quality
                latency
                regions
                privacy
                (ProviderModelPrice.create "USD" inputPrice outputPrice)
                ProviderDeprecation.Active)
            health
            ProviderEndpointReadiness.Ready
            ProviderQuotaStatus.Available
            ProviderMaintenanceStatus.Active

    let request strategy =
        ProviderRoutingRequest.create
            "policy-v1"
            "execution-1"
            (ProviderRequirements.create (Set [ ProviderCapability.Chat ]) (Some 8192) (Some UsageReportingLevel.Full))
            (Set [ ProviderQualityTier.Standard; ProviderQualityTier.Premium ])
            (Set [ ProviderLatencyClass.Interactive ])
            (Set [ ProviderPrivacyClass.LocalOnly; ProviderPrivacyClass.ZeroRetention ])
            (Set [ "eu-west" ])
            (Some(ProviderCostLimit.create "USD" 10m 20m))
            strategy

    let eligible endpoint model =
        observation
            endpoint
            model
            ProviderQualityTier.Standard
            ProviderLatencyClass.Interactive
            (Set [ "eu-west" ])
            ProviderPrivacyClass.ZeroRetention
            2m
            4m
            ProviderEndpointHealth.Healthy

    [<TestMethod>]
    member _.``Static routing follows policy order and records every candidate``() =
        let decision =
            ProviderRouter.route
                (request (ProviderRoutingStrategy.StaticPriority [ "secondary"; "primary" ]))
                [ eligible "primary" "model-a"; eligible "secondary" "model-b" ]

        Assert.AreEqual(Some "secondary", decision.SelectedEndpointId)
        CollectionAssert.AreEqual([| "secondary"; "primary" |], decision.OrderedEligibleEndpointIds |> List.toArray)
        Assert.AreEqual(2, decision.Candidates.Length)
        Assert.AreEqual("policy-v1", decision.PolicyVersion)

    [<TestMethod>]
    member _.``Weighted routing is deterministic for a pinned policy and candidate snapshot``() =
        let routingRequest =
            request (
                ProviderRoutingStrategy.DeterministicWeighted(Map [ "primary", 3m; "secondary", 1m; "tertiary", 2m ])
            )

        let candidates =
            [ eligible "primary" "model-a"
              eligible "secondary" "model-b"
              eligible "tertiary" "model-c" ]

        let first = ProviderRouter.route routingRequest candidates

        for _ in 1..20 do
            Assert.AreEqual(first, ProviderRouter.route routingRequest (List.rev candidates))

    [<TestMethod>]
    member _.``Fallback candidates cannot weaken mandatory constraints``() =
        let incompatible =
            observation
                "cheap"
                "model-cheap"
                ProviderQualityTier.Economy
                ProviderLatencyClass.Batch
                (Set [ "us-east" ])
                ProviderPrivacyClass.ProviderRetained
                1m
                1m
                ProviderEndpointHealth.Healthy

        let unavailable =
            observation
                "down"
                "model-down"
                ProviderQualityTier.Standard
                ProviderLatencyClass.Interactive
                (Set [ "eu-west" ])
                ProviderPrivacyClass.ZeroRetention
                2m
                4m
                ProviderEndpointHealth.Unavailable

        let decision =
            ProviderRouter.route
                (request (ProviderRoutingStrategy.StaticPriority [ "cheap"; "down"; "safe" ]))
                [ incompatible; unavailable; eligible "safe" "model-safe" ]

        Assert.AreEqual(Some "safe", decision.SelectedEndpointId)
        Assert.AreEqual([ "safe" ], decision.OrderedEligibleEndpointIds)

        let excluded =
            decision.Candidates
            |> List.filter (fun candidate -> candidate.EndpointId <> "safe")

        Assert.IsTrue(excluded |> List.forall (fun candidate -> not candidate.Exclusions.IsEmpty))

    [<TestMethod>]
    member _.``Endpoints omitted by policy are excluded rather than appended as fallback``() =
        let decision =
            ProviderRouter.route
                (request (ProviderRoutingStrategy.StaticPriority [ "primary" ]))
                [ eligible "primary" "model-a"; eligible "unconfigured" "model-b" ]

        Assert.AreEqual([ "primary" ], decision.OrderedEligibleEndpointIds)

        let unconfigured =
            decision.Candidates
            |> List.find (fun candidate -> candidate.EndpointId = "unconfigured")

        CollectionAssert.Contains(
            unconfigured.Exclusions |> List.toArray,
            ProviderRoutingExclusion.NotConfiguredByPolicy
        )

    [<TestMethod>]
    member _.``Routing decision is recorded before it is returned``() : Task =
        task {
            let audits = ConcurrentQueue<ProviderRoutingAudit>()
            let timestamp = DateTimeOffset.Parse("2026-10-21T00:00:00Z")

            let! decision =
                ProviderRouter.routeAndRecordAsync
                    (fun () -> timestamp)
                    (fun audit ->
                        audits.Enqueue audit
                        Task.CompletedTask)
                    (request (ProviderRoutingStrategy.StaticPriority [ "primary" ]))
                    [ eligible "primary" "model-a" ]

            let audit = audits |> Seq.exactlyOne
            Assert.AreEqual(timestamp, audit.Timestamp)
            Assert.AreEqual(decision, audit.Decision)
        }

    [<TestMethod>]
    member _.``Hedging never exceeds request or incremental cost budgets``() =
        let decision =
            ProviderRouter.route
                (request (ProviderRoutingStrategy.StaticPriority [ "primary"; "secondary"; "tertiary" ]))
                [ eligible "primary" "model-a"
                  eligible "secondary" "model-b"
                  eligible "tertiary" "model-c" ]

        let plan =
            ProviderHedgePlan.create
                (ProviderHedgingPolicy.Bounded(2, TimeSpan.FromMilliseconds 100.0, 3m))
                (Map [ "secondary", 2m; "tertiary", 2m ])
                decision

        Assert.AreEqual(Some "primary", plan.PrimaryEndpointId)
        Assert.AreEqual([ "secondary" ], plan.HedgeEndpointIds)
        Assert.AreEqual(3m, plan.MaximumIncrementalCost)
