namespace Nao.Runtime.Orleans.Grains

open System
open System.Threading.Tasks
open Nao.Agents
open Orleans

[<GenerateSerializer>]
type RuntimeSiloHealthSnapshot() =
    [<Id(0u)>]
    member val TenantId: string = "" with get, set

    [<Id(1u)>]
    member val SiloIdentity: string = "" with get, set

    [<Id(2u)>]
    member val ObservedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

type IClusterHealthGrain =
    inherit IGrainWithStringKey

    abstract member GetAsync: unit -> Task<RuntimeSiloHealthSnapshot>

/// Tenant-keyed liveness probe used to verify cluster routing and silo failover. It owns no
/// protected tenant state; callers receive only the key they supplied and the serving silo identity.
type ClusterHealthGrain() as this =
    inherit Grain()

    interface IClusterHealthGrain with
        member _.GetAsync() =
            let tenantId = this.GetPrimaryKeyString() |> TenantId.parse |> TenantId.value

            Task.FromResult(
                RuntimeSiloHealthSnapshot(
                    TenantId = tenantId,
                    SiloIdentity = this.RuntimeIdentity,
                    ObservedAt = DateTimeOffset.UtcNow
                )
            )
