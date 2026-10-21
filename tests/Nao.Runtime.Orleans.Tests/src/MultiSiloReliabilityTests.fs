namespace Nao.Runtime.Orleans.Tests

open System
open System.Threading.Tasks
open System.Reflection
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.Serialization
open Orleans.TestingHost

type RuntimeSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(builder: ISiloBuilder) =
            let codegenAssembly = Assembly.Load "Nao.Runtime.Orleans.Codegen"

            builder.Services.Configure<GrainTypeOptions>(fun (options: GrainTypeOptions) ->
                options.Classes.Add typeof<ClusterHealthGrain> |> ignore
                options.Interfaces.Add typeof<IClusterHealthGrain> |> ignore)
            |> ignore

            builder.Services.Configure<ClusterMembershipOptions>(fun (options: ClusterMembershipOptions) ->
                options.ProbeTimeout <- TimeSpan.FromMilliseconds 500.0
                options.TableRefreshTimeout <- TimeSpan.FromSeconds 1.0
                options.DeathVoteExpirationTimeout <- TimeSpan.FromSeconds 5.0
                options.IAmAliveTablePublishTimeout <- TimeSpan.FromSeconds 1.0
                options.NumProbedSilos <- 1
                options.NumMissedProbesLimit <- 1
                options.NumVotesForDeathDeclaration <- 1)
            |> ignore

            builder.Services.AddSerializer(fun serializer ->
                serializer.AddAssembly(typeof<ClusterHealthGrain>.Assembly) |> ignore

                serializer.AddAssembly(typeof<MemoryEntry>.Assembly) |> ignore
                serializer.AddAssembly codegenAssembly |> ignore)
            |> ignore

type RuntimeClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_: IConfiguration, builder: IClientBuilder) =
            let codegenAssembly = Assembly.Load "Nao.Runtime.Orleans.Codegen"

            builder.Services.Configure<GrainTypeOptions>(fun (options: GrainTypeOptions) ->
                options.Interfaces.Add typeof<IClusterHealthGrain> |> ignore)
            |> ignore

            builder.Services.AddSerializer(fun serializer ->
                serializer.AddAssembly(typeof<IClusterHealthGrain>.Assembly) |> ignore

                serializer.AddAssembly(typeof<MemoryEntry>.Assembly) |> ignore
                serializer.AddAssembly codegenAssembly |> ignore)
            |> ignore

[<TestClass; TestCategory("Integration"); TestCategory("ClusterReliability")>]
type MultiSiloReliabilityTests() =
    let createCluster () =
        let builder = TestClusterBuilder(2s)
        builder.AddSiloBuilderConfigurator<RuntimeSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<RuntimeClientConfigurator>() |> ignore
        builder.Build()

    [<TestMethod; Timeout(90000)>]
    member _.TenantScopedGrainRemainsReachableAfterHostingSiloFailure() : Task =
        task {
            use cluster = createCluster ()
            do! cluster.DeployAsync()

            Assert.AreEqual(2, cluster.Silos.Count)

            let probe = cluster.GrainFactory.GetGrain<IClusterHealthGrain>("tenant-a")

            let otherTenantProbe =
                cluster.GrainFactory.GetGrain<IClusterHealthGrain>("tenant-b")

            let! first = probe.GetAsync()
            let! other = otherTenantProbe.GetAsync()
            Assert.AreEqual("tenant-a", first.TenantId)
            Assert.AreEqual("tenant-b", other.TenantId)

            let hostingSilo =
                cluster.Silos
                |> Seq.find (fun silo -> silo.SiloAddress.ToString() = first.SiloIdentity)

            do! cluster.KillSiloAsync hostingSilo
            do! cluster.StopClusterClientAsync()
            do! cluster.InitializeClientAsync()

            let recoveredProbe = cluster.GrainFactory.GetGrain<IClusterHealthGrain>("tenant-a")

            let recoveredOtherTenantProbe =
                cluster.GrainFactory.GetGrain<IClusterHealthGrain>("tenant-b")

            let! afterFailure = recoveredProbe.GetAsync().WaitAsync(TimeSpan.FromSeconds 30.0)

            let! otherAfterFailure = recoveredOtherTenantProbe.GetAsync().WaitAsync(TimeSpan.FromSeconds 30.0)
            Assert.AreEqual("tenant-a", afterFailure.TenantId)
            Assert.AreEqual("tenant-b", otherAfterFailure.TenantId)
            Assert.AreNotEqual(first.SiloIdentity, afterFailure.SiloIdentity)
        }

    [<TestMethod; Timeout(90000)>]
    member _.RollingSiloRestartPreservesTenantRoutingCompatibility() : Task =
        task {
            use cluster = createCluster ()
            do! cluster.DeployAsync()

            let probe = cluster.GrainFactory.GetGrain<IClusterHealthGrain>("tenant-a")
            let! before = probe.GetAsync()

            let hostingSilo =
                cluster.Silos
                |> Seq.find (fun silo -> silo.SiloAddress.ToString() = before.SiloIdentity)

            do! cluster.StopSiloAsync hostingSilo
            let! _ = cluster.RestartSiloAsync hostingSilo
            do! cluster.StopClusterClientAsync()
            do! cluster.InitializeClientAsync()

            let recovered = cluster.GrainFactory.GetGrain<IClusterHealthGrain>("tenant-a")

            let! after = recovered.GetAsync().WaitAsync(TimeSpan.FromSeconds 30.0)

            Assert.AreEqual("tenant-a", after.TenantId)
            Assert.IsFalse(String.IsNullOrWhiteSpace after.SiloIdentity)
        }
