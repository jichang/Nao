namespace Nao.Persistence

open Nao.Agents

/// Consolidated constructors for the in-memory reference implementations that used to live in
/// Nao.Agents. Nao.Agents now exposes only interfaces/types; hosts and tests pick a concrete
/// store from here (or one of the ADO/file factories) without the agent layer depending on any
/// storage technology.
module InMemory =
    let store () : IMemoryStore = InMemoryStore() :> IMemoryStore
    let embeddingProvider () : IEmbeddingProvider = SimpleEmbeddingProvider() :> IEmbeddingProvider
    let semanticMemory (provider: IEmbeddingProvider) : ISemanticMemory = InMemorySemanticMemory(provider) :> ISemanticMemory
    let tracer () : ITracer = InMemoryTracer() :> ITracer
    let metrics () : IMetricsCollector = InMemoryMetricsCollector() :> IMetricsCollector
    let auditLog () : IAuditLog = InMemoryAuditLog() :> IAuditLog
    let traceStore () : ITraceStore = InMemoryTraceStore() :> ITraceStore
    let executionJournal () : IExecutionJournal = InMemoryExecutionJournal() :> IExecutionJournal
    let eventBus () : IEventBus = InMemoryEventBus() :> IEventBus
