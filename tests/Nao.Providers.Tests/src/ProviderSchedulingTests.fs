namespace Nao.Providers.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Providers

[<TestClass>]
type ProviderSchedulingTests() =
    let configuration endpointLimit tenantQueueCapacity =
        ProviderSchedulerConfiguration.create
            (Map [ "endpoint", endpointLimit ])
            (Map [ ("endpoint", "model"), endpointLimit ])
            (Map [ "tenant-a", 1; "tenant-b", 1 ])
            4
            tenantQueueCapacity

    let key tenant =
        ProviderSchedulingKey.create tenant "endpoint" "model"

    let acquire tenant scheduler =
        task {
            let! result = ProviderScheduler.acquireAsync (key tenant) CancellationToken.None scheduler

            match result with
            | Ok lease -> return lease
            | Error failure -> return failwith $"Unexpected admission failure: {failure}."
        }

    [<TestMethod>]
    member _.``Endpoint and tenant concurrency are enforced``() : Task =
        task {
            let scheduler = ProviderScheduler.create (configuration 2 2)
            let! first = acquire "tenant-a" scheduler
            let secondTask = acquire "tenant-a" scheduler
            let! otherTenant = acquire "tenant-b" scheduler
            Assert.IsFalse(secondTask.IsCompleted)
            (first :> IDisposable).Dispose()
            let! second = secondTask
            (second :> IDisposable).Dispose()
            (otherTenant :> IDisposable).Dispose()
        }

    [<TestMethod>]
    member _.``Rate-limited endpoints retain queued work until availability returns``() : Task =
        task {
            let scheduler = ProviderScheduler.create (configuration 1 2)
            ProviderScheduler.setRateLimit "endpoint" ProviderRateLimitState.Limited scheduler
            let waiting = acquire "tenant-a" scheduler
            Assert.IsFalse(waiting.IsCompleted)
            ProviderScheduler.setRateLimit "endpoint" ProviderRateLimitState.Available scheduler
            let! lease = waiting
            (lease :> IDisposable).Dispose()
        }

    [<TestMethod>]
    member _.``Per-tenant queue bounds stop a noisy tenant before global exhaustion``() : Task =
        task {
            let scheduler = ProviderScheduler.create (configuration 1 1)
            let! active = acquire "tenant-a" scheduler

            let waiting =
                ProviderScheduler.acquireAsync (key "tenant-a") CancellationToken.None scheduler

            let! rejected = ProviderScheduler.acquireAsync (key "tenant-a") CancellationToken.None scheduler

            Assert.AreEqual(Error ProviderAdmissionFailure.TenantQueueFull, rejected)
            let otherTenant = acquire "tenant-b" scheduler
            (active :> IDisposable).Dispose()
            let! otherLease = otherTenant
            Assert.IsFalse(waiting.IsCompleted)
            (otherLease :> IDisposable).Dispose()
            let! waitingResult = waiting

            match waitingResult with
            | Ok lease -> (lease :> IDisposable).Dispose()
            | Error failure -> Assert.Fail($"Unexpected admission failure: {failure}.")
        }

    [<TestMethod>]
    member _.``Draining endpoint completes active work but admits no queued work``() : Task =
        task {
            let scheduler = ProviderScheduler.create (configuration 1 2)
            let! active = acquire "tenant-a" scheduler
            ProviderScheduler.setEndpointState "endpoint" ProviderEndpointAdmissionState.Draining scheduler
            let waiting = acquire "tenant-b" scheduler
            (active :> IDisposable).Dispose()
            Assert.IsFalse(waiting.IsCompleted)
            ProviderScheduler.setEndpointState "endpoint" ProviderEndpointAdmissionState.Active scheduler
            let! admitted = waiting
            (admitted :> IDisposable).Dispose()
        }
