namespace Nao.Agents.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type OperationalDiagnosticsTests() =
    [<TestMethod>]
    member _.``Health checks preserve independent probe and component identity``() : Task =
        task {
            let expected =
                OperationalHealth.create
                    OperationalProbe.Dependency
                    OperationalComponent.KnowledgeIndex
                    "primary-index"
                    OperationalHealthState.Degraded
                    OperationalHealthReason.CapacityExhausted
                    DateTimeOffset.UtcNow
                    (TimeSpan.FromMilliseconds 12.0)

            let check =
                OperationalHealthCheck.create
                    OperationalProbe.Dependency
                    OperationalComponent.KnowledgeIndex
                    "primary-index"
                    (fun _ -> Task.FromResult expected)

            let! actual = OperationalHealthCheck.checkAsync CancellationToken.None check
            Assert.AreEqual(expected, actual)
            Assert.AreEqual(OperationalProbe.Dependency, OperationalHealthCheck.probe check)
            Assert.AreEqual(OperationalComponent.KnowledgeIndex, OperationalHealthCheck.componentKind check)
        }

    [<TestMethod>]
    member _.``Health states require bounded reasons``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            OperationalHealth.create
                OperationalProbe.Readiness
                OperationalComponent.Provider
                "provider"
                OperationalHealthState.Unhealthy
                OperationalHealthReason.None
                DateTimeOffset.UtcNow
                TimeSpan.Zero
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Health plans require host probes and independently named dependencies``() : Task =
        task {
            let check probe componentKind name =
                OperationalHealthCheck.create probe componentKind name (fun _ ->
                    OperationalHealth.create
                        probe
                        componentKind
                        name
                        OperationalHealthState.Healthy
                        OperationalHealthReason.None
                        DateTimeOffset.UtcNow
                        TimeSpan.Zero
                    |> Task.FromResult)

            let plan =
                OperationalHealthPlan.create
                    (check OperationalProbe.Liveness OperationalComponent.Host "host")
                    (check OperationalProbe.Startup OperationalComponent.Host "host")
                    (check OperationalProbe.Readiness OperationalComponent.Host "host")
                    [ check OperationalProbe.Dependency OperationalComponent.Provider "provider"
                      check OperationalProbe.Dependency OperationalComponent.Storage "storage"
                      check OperationalProbe.Dependency OperationalComponent.Queue "queue"
                      check OperationalProbe.Dependency OperationalComponent.KnowledgeIndex "index"
                      check OperationalProbe.Dependency OperationalComponent.Reasoner "reasoner" ]

            let! observations = OperationalHealthPlan.checkAsync CancellationToken.None plan
            Assert.AreEqual(8, observations.Length)
        }

    [<TestMethod>]
    member _.``Saturation reports overcommit and rejected work without content``() =
        let signal =
            SaturationSignal.create SaturationComponent.ExecutionWorkers "workers" 12L 10L 3L DateTimeOffset.UtcNow

        Assert.AreEqual(1.2, SaturationSignal.utilization signal, 0.0001)
        Assert.AreEqual(3L, SaturationSignal.rejected signal)
        Assert.IsFalse((sprintf "%A" signal).Contains("Input"))
        Assert.IsFalse((sprintf "%A" signal).Contains("Output"))

        let metrics = OperationalTelemetry.saturation (CorrelationContext.root ()) signal
        Assert.AreEqual(2, metrics.Length)

    [<TestMethod>]
    member _.``Health observations project to canonical bounded metrics``() =
        let health =
            OperationalHealth.create
                OperationalProbe.Dependency
                OperationalComponent.Provider
                "primary-provider"
                OperationalHealthState.Unhealthy
                OperationalHealthReason.DependencyUnavailable
                DateTimeOffset.UtcNow
                (TimeSpan.FromMilliseconds 7.0)

        let metrics = OperationalTelemetry.health (CorrelationContext.root ()) health

        let points =
            metrics
            |> List.choose (function
                | { Payload = MetricPayload.Custom point } -> Some point
                | _ -> None)

        Assert.AreEqual(4, points.Length)

        let unhealthy =
            points
            |> List.find (fun point ->
                point.Name = "nao.health.status"
                && point.Labels.[TelemetryAttribute.HealthState] = "unhealthy")

        Assert.AreEqual(1.0, unhealthy.Value)

        let duration =
            points |> List.find (fun point -> point.Name = "nao.health.check.duration")

        Assert.AreEqual(7.0, duration.Value)

    [<TestMethod>]
    member _.``Diagnostic snapshots reject ambiguous duplicate observations``() =
        let health =
            OperationalHealth.create
                OperationalProbe.Liveness
                OperationalComponent.Host
                "host"
                OperationalHealthState.Healthy
                OperationalHealthReason.None
                DateTimeOffset.UtcNow
                TimeSpan.Zero

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            DiagnosticSnapshot.create DateTimeOffset.UtcNow [ health; health ] [] |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Operational dashboard instruments use bounded labels``() =
        let expected =
            [ "nao.health.status"
              "nao.saturation.utilization"
              "nao.provider.cost"
              "nao.provider.retry.count"
              "nao.circuit.state"
              "nao.policy.block.count"
              "nao.retrieval.quality"
              "nao.evaluation.drift" ]

        for name in expected do
            Assert.IsTrue(TelemetryInstrument.tryFind(name).IsSome, $"Missing canonical instrument {name}.")
