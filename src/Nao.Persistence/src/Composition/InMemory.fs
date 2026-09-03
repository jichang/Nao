namespace Nao.Persistence

open Nao.Agents

/// Consolidated constructors for the in-memory reference implementations that used to live in
/// Nao.Agents. Nao.Agents now exposes only functional capability records; hosts and tests pick a concrete
/// store from here (or one of the ADO/file factories) without the agent layer depending on any
/// storage technology.
module InMemory =
    let store () : MemoryStore = InMemoryStore.create ()
    let embeddingProvider () : EmbeddingProvider = SimpleEmbeddingProvider.create ()
    let semanticMemory (provider: EmbeddingProvider) : SemanticMemory = InMemorySemanticMemory.create provider
    let tracer () : Tracer = InMemoryTracer.create ()
    let metrics () : MetricsCollector = InMemoryMetricsCollector.create ()
    let auditLog () : AuditLog = InMemoryAuditLog.create ()
    let traceStore () : TraceStore = InMemoryTraceStore.create ()
    let executionJournal () : ExecutionJournal = InMemoryExecutionJournal.create ()
    let eventBus () : EventBus = InMemoryEventBus.create ()
