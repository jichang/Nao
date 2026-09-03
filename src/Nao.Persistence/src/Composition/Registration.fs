namespace Nao.Persistence

open Nao.Agents

/// Selects which durable backend the persistence factories should produce. The host
/// turns this single knob to choose between the two storage categories: file system
/// or database.
[<RequireQualifiedAccess>]
type PersistenceMode =
    /// FileSystem-backed implementations rooted at the given directory.
    | File of baseDir: string
    /// Provider-agnostic ADO.NET implementations using the supplied connection factory.
    | Database of factory: DbConnectionFactory

/// Builds concrete persistence components for a chosen `PersistenceMode`. The rest of
/// the system depends only on functional capability records; this module is the one place that maps a
/// mode to an implementation, so swapping file for database is a single edit.
module Persistence =

    /// Tracer for the given mode.
    let tracer (mode: PersistenceMode) : Tracer =
        match mode with
        | PersistenceMode.File dir -> Tracers.file dir
        | PersistenceMode.Database factory -> Tracers.ado factory

    /// Metrics collector for the given mode.
    let metrics (mode: PersistenceMode) : MetricsCollector =
        match mode with
        | PersistenceMode.File dir -> MetricsCollectors.file dir
        | PersistenceMode.Database factory -> MetricsCollectors.ado factory

    /// Execution journal for the given mode.
    let executionJournal (mode: PersistenceMode) : ExecutionJournal =
        match mode with
        | PersistenceMode.File dir -> ExecutionJournals.file dir
        | PersistenceMode.Database factory -> ExecutionJournals.ado factory

    /// Trace store (regression baselines) for the given mode.
    let traceStore (mode: PersistenceMode) : TraceStore =
        match mode with
        | PersistenceMode.File dir -> TraceStores.file dir
        | PersistenceMode.Database factory -> TraceStores.ado factory

    /// Audit log for the given mode.
    let auditLog (mode: PersistenceMode) : AuditLog =
        match mode with
        | PersistenceMode.File dir -> AuditLogs.file dir
        | PersistenceMode.Database factory -> AuditLogs.ado factory

    /// Build a full `HarnessServices` bundle for the mode — the value a host injects
    /// into the Orleans silo / ASP.NET container so the `SessionGrain` (and any harness)
    /// gets observability + governance wired to the chosen backend.
    let harnessServices (mode: PersistenceMode) : HarnessServices =
        HarnessServices.create
            (Some(tracer mode))
            (Some(metrics mode))
            (Some(executionJournal mode))
            (Some(traceStore mode))
            (Some(auditLog mode))
