namespace Nao.Providers.Tests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Providers

[<TestClass>]
type ProviderResilienceTests() =
    let transientFailure =
        PlatformFailure.fromHttpStatus None 503 "Provider unavailable."

    let partialFailure =
        ProviderAttemptFailure.create transientFailure None ProviderResponseProgress.PartialResponse

    let noResponseFailure retryAfter =
        ProviderAttemptFailure.create transientFailure retryAfter ProviderResponseProgress.NoResponse

    let decision endpoints =
        { PolicyVersion = "policy-v1"
          SelectedEndpointId = endpoints |> List.tryHead
          SelectedModelId = Some "model"
          OrderedEligibleEndpointIds = endpoints
          Candidates =
            endpoints
            |> List.map (fun endpointId ->
                { EndpointId = endpointId
                  ModelId = "model"
                  Exclusions = [] }) }

    let domain endpoint region =
        ProviderFailureDomain.create endpoint region

    let plan endpoints attempts =
        let domains =
            endpoints
            |> List.map (fun endpoint -> endpoint, domain endpoint $"region-{endpoint}")
            |> Map

        ProviderFailoverPlan.create (decision endpoints) domains attempts, domains

    let policy =
        ProviderResiliencePolicy.create TimeSpan.Zero (TimeSpan.FromSeconds 1.0)

    [<TestMethod>]
    member _.``Endpoint circuit failure does not open an unrelated failure domain``() : Task =
        task {
            let mutable now = DateTimeOffset.Parse("2026-10-21T00:00:00Z")
            let clock () = now
            let failover, domains = plan [ "west"; "east" ] 1

            let circuits =
                ProviderCircuitRegistry.create (ProviderCircuitConfiguration.create 1 (TimeSpan.FromMinutes 1.0)) clock

            let budget = ProviderRetryBudget.create 2 (TimeSpan.FromMinutes 1.0) clock

            let! result =
                ProviderResilience.executeAsync
                    policy
                    budget
                    circuits
                    failover
                    (fun _ _ -> Task.CompletedTask)
                    (fun endpoint _ _ ->
                        if endpoint = "west" then
                            Task.FromResult(Error(noResponseFailure None))
                        else
                            Task.FromResult(Ok "east-result"))
                    CancellationToken.None

            Assert.AreEqual(Ok "east-result", result.Result)

            Assert.AreEqual(
                ProviderCircuitState.Open(now + TimeSpan.FromMinutes 1.0),
                ProviderCircuitRegistry.state domains.["west"] circuits
            )

            Assert.AreEqual(ProviderCircuitState.Closed, ProviderCircuitRegistry.state domains.["east"] circuits)
        }

    [<TestMethod>]
    member _.``Retry-after is honored inside the shared task budget``() : Task =
        task {
            let now = DateTimeOffset.Parse("2026-10-21T00:00:00Z")
            let failover, _ = plan [ "primary" ] 2

            let circuits =
                ProviderCircuitRegistry.create
                    (ProviderCircuitConfiguration.create 3 (TimeSpan.FromMinutes 1.0))
                    (fun () -> now)

            let budget = ProviderRetryBudget.create 2 (TimeSpan.FromMinutes 1.0) (fun () -> now)
            let delays = ConcurrentQueue<TimeSpan>()
            let mutable attempts = 0

            let! result =
                ProviderResilience.executeAsync
                    policy
                    budget
                    circuits
                    failover
                    (fun delay _ ->
                        delays.Enqueue delay
                        Task.CompletedTask)
                    (fun _ _ _ ->
                        attempts <- attempts + 1

                        if attempts = 1 then
                            Task.FromResult(Error(noResponseFailure (Some(TimeSpan.FromSeconds 12.0))))
                        else
                            Task.FromResult(Ok "ok"))
                    CancellationToken.None

            Assert.AreEqual(Ok "ok", result.Result)
            Assert.AreEqual(TimeSpan.FromSeconds 12.0, delays |> Seq.exactlyOne)
            Assert.AreEqual(2, ProviderRetryBudget.consumedAttempts budget)
        }

    [<TestMethod>]
    member _.``Partial response prevents retry and fallback``() : Task =
        task {
            let now = DateTimeOffset.Parse("2026-10-21T00:00:00Z")
            let failover, _ = plan [ "primary"; "fallback" ] 3

            let circuits =
                ProviderCircuitRegistry.create
                    (ProviderCircuitConfiguration.create 2 (TimeSpan.FromMinutes 1.0))
                    (fun () -> now)

            let budget = ProviderRetryBudget.create 6 (TimeSpan.FromMinutes 1.0) (fun () -> now)
            let invoked = ConcurrentQueue<string>()

            let! result =
                ProviderResilience.executeAsync
                    policy
                    budget
                    circuits
                    failover
                    (fun _ _ -> Task.CompletedTask)
                    (fun endpoint _ _ ->
                        invoked.Enqueue endpoint
                        Task.FromResult(Error partialFailure))
                    CancellationToken.None

            match result.Result with
            | Error(ProviderResilienceFailure.PartialResponse("primary", _)) -> ()
            | other -> Assert.Fail($"Unexpected resilience result: {other}.")

            CollectionAssert.AreEqual([| "primary" |], invoked.ToArray())
        }

    [<TestMethod>]
    member _.``Nested calls consume one shared retry budget``() =
        let now = DateTimeOffset.Parse("2026-10-21T00:00:00Z")
        let budget = ProviderRetryBudget.create 2 (TimeSpan.FromMinutes 1.0) (fun () -> now)
        Assert.IsTrue(ProviderRetryBudget.tryConsumeAttempt budget)
        Assert.IsTrue(ProviderRetryBudget.tryConsumeAttempt budget)
        Assert.IsFalse(ProviderRetryBudget.tryConsumeAttempt budget)

    [<TestMethod>]
    member _.``Cache keys require explicit eligible operation and owner scope``() =
        let completion =
            ProviderCacheKey.create
                "tenant-a"
                "endpoint"
                "model"
                ProviderCacheOperation.DeterministicCompletion
                "input-digest"
                "config-digest"

        let embedding =
            ProviderCacheKey.create
                "tenant-a"
                "endpoint"
                "model"
                ProviderCacheOperation.Embedding
                "input-digest"
                "config-digest"

        Assert.AreNotEqual(completion, embedding)
