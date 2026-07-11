namespace Nao.Persistence

open Nao.Agents

/// Mutating events for tool-discovery persistence. Only usage statistics are
/// durable; the in-process tool registry (populated via Register) is runtime
/// configuration and is not persisted.
[<RequireQualifiedAccess>]
type ToolDiscoveryEvent =
    | Invocation of toolName: string * success: bool * latencyMs: int64 * cost: float

/// Event-sourced tool discovery. Wraps InMemoryToolDiscovery, persisting recorded
/// invocations so usage-based ranking survives restarts. Tool registration is a
/// passthrough (re-register tools at startup as part of application wiring).
type PersistentToolDiscovery(store: IEventStore, config: ToolDiscoveryConfig, ?embeddingProvider: IEmbeddingProvider) =
    let inner =
        match embeddingProvider with
        | Some p -> InMemoryToolDiscovery(config, p)
        | None -> InMemoryToolDiscovery(config)

    let asInterface = inner :> IToolDiscovery

    do
        for line in store.LoadAll() do
            match FSharpJson.deserialize<ToolDiscoveryEvent> line with
            | ToolDiscoveryEvent.Invocation(name, success, latency, cost) ->
                (asInterface.RecordInvocationAsync name success latency cost).GetAwaiter().GetResult()

    /// Register a tool from a given source (runtime configuration, not persisted).
    member _.Register (schema: ToolSchema) (source: DiscoverySource) = inner.Register schema source

    /// Register multiple tools (runtime configuration, not persisted).
    member _.RegisterMany (schemas: ToolSchema list) (source: DiscoverySource) = inner.RegisterMany schemas source

    interface IToolDiscovery with
        member _.DiscoverAsync() = asInterface.DiscoverAsync()

        member _.RankForTaskAsync (taskDescription: string) (maxTools: int) =
            asInterface.RankForTaskAsync taskDescription maxTools

        member _.CheckAvailabilityAsync(toolName: string) = asInterface.CheckAvailabilityAsync toolName

        member _.GetStatsAsync(toolName: string) = asInterface.GetStatsAsync toolName

        member _.RecordInvocationAsync (toolName: string) (success: bool) (latencyMs: int64) (cost: float) =
            task {
                do! asInterface.RecordInvocationAsync toolName success latencyMs cost
                store.Append(FSharpJson.serialize (ToolDiscoveryEvent.Invocation(toolName, success, latencyMs, cost)))
            }

        member _.PruneForContextAsync (taskDescription: string) (availableTokenBudget: int) =
            asInterface.PruneForContextAsync taskDescription availableTokenBudget

/// Factory helpers for tool discovery persistence.
module ToolDiscoveries =
    /// ADO.NET-backed tool discovery over any provider supplied via the connection factory.
    let ado
        (factory: IDbConnectionFactory)
        (config: ToolDiscoveryConfig)
        (embeddingProvider: IEmbeddingProvider option)
        : PersistentToolDiscovery =
        PersistentToolDiscovery(EventStore.db factory "tool-discovery", config, ?embeddingProvider = embeddingProvider)

    /// FileSystem-backed tool discovery rooted at the given directory.
    let file
        (baseDir: string)
        (config: ToolDiscoveryConfig)
        (embeddingProvider: IEmbeddingProvider option)
        : PersistentToolDiscovery =
        PersistentToolDiscovery(
            EventStore.file (System.IO.Path.Combine(baseDir, "tool-discovery.jsonl")),
            config,
            ?embeddingProvider = embeddingProvider
        )
