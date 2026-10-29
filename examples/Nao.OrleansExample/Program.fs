namespace Nao.OrleansExample

open System
open System.Reflection
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Nao.Agents
open Nao.Runtime.Orleans.Grains
open Orleans.Configuration
open Orleans.Hosting
open Orleans.Serialization
open Orleans.TestingHost

type ExampleSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(builder: ISiloBuilder) =
            let codegenAssembly = Assembly.Load "Nao.Runtime.Orleans.Codegen"

            builder.Services.Configure<GrainTypeOptions>(fun (options: GrainTypeOptions) ->
                options.Classes.Add typeof<ClusterHealthGrain> |> ignore
                options.Interfaces.Add typeof<IClusterHealthGrain> |> ignore)
            |> ignore

            builder.Services.AddSerializer(fun serializer ->
                serializer.AddAssembly(typeof<ClusterHealthGrain>.Assembly) |> ignore
                serializer.AddAssembly(typeof<MemoryEntry>.Assembly) |> ignore
                serializer.AddAssembly codegenAssembly |> ignore)
            |> ignore

type ExampleClientConfigurator() =
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

module Program =
    [<EntryPoint>]
    let main _ =
        task {
            let builder = TestClusterBuilder(2s)
            builder.AddSiloBuilderConfigurator<ExampleSiloConfigurator>() |> ignore
            builder.AddClientBuilderConfigurator<ExampleClientConfigurator>() |> ignore

            use cluster = builder.Build()
            do! cluster.DeployAsync()

            let tenantA = cluster.GrainFactory.GetGrain<IClusterHealthGrain>("example-tenant-a")

            let tenantB = cluster.GrainFactory.GetGrain<IClusterHealthGrain>("example-tenant-b")

            let! first = tenantA.GetAsync()
            let! second = tenantB.GetAsync()

            if first.TenantId <> "example-tenant-a" || second.TenantId <> "example-tenant-b" then
                invalidOp "Orleans tenant-keyed grain routing failed."

            if cluster.Silos.Count <> 2 then
                invalidOp "The local reference cluster did not start two silos."

            printfn "orleans-cluster: two silos and tenant-keyed grains succeeded"
        }
        |> _.GetAwaiter().GetResult()

        0
