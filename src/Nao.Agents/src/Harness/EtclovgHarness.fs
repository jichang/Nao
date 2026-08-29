namespace Nao.Agents

open System
open System.Diagnostics
open System.Threading.Tasks
open Nao.Agents

/// Pluggable observability + governance services that a host (Orleans silo, ASP.NET
/// app, test fixture, ...) injects into the harness. Each capability is optional so a
/// host can supply only what it needs; `None` means that capability is disabled.
/// This is the seam that lets callers choose in-memory (testing) vs persistent
/// (production) backends without the harness/runtime knowing the concrete type.
type IHarnessServices =
    abstract member Tracer: ITracer option
    abstract member Metrics: IMetricsCollector option
    abstract member ExecutionJournal: IExecutionJournal option
    abstract member TraceStore: ITraceStore option
    abstract member AuditLog: IAuditLog option

/// Helpers for constructing IHarnessServices values.
module HarnessServices =

    /// Services with nothing configured — every capability disabled.
    let none: IHarnessServices =
        { new IHarnessServices with
            member _.Tracer = None
            member _.Metrics = None
            member _.ExecutionJournal = None
            member _.TraceStore = None
            member _.AuditLog = None }

    /// Build services from explicit optional components.
    let create
        (tracer: ITracer option)
        (metrics: IMetricsCollector option)
        (executionJournal: IExecutionJournal option)
        (traceStore: ITraceStore option)
        (auditLog: IAuditLog option)
        : IHarnessServices =
        { new IHarnessServices with
            member _.Tracer = tracer
            member _.Metrics = metrics
            member _.ExecutionJournal = executionJournal
            member _.TraceStore = traceStore
            member _.AuditLog = auditLog }

/// Complete ETCLOVG harness configuration wiring all seven layers together
type EtclovgConfig =
    { /// E — Execution Environment: sandbox and resource limits
      Execution: SandboxConfig
      /// T — Tool Interface: protocol for tool discovery and invocation
      ToolProtocol: IToolProtocol option
      /// T — Execution journal for tracking tool calls (revert support)
      ExecutionJournal: IExecutionJournal option
      /// L — Lifecycle: hooks and pipeline stages
      Lifecycle: ILifecycleHook list
      /// O — Observability: tracer, metrics, and resilience
      Tracer: ITracer option
      Metrics: IMetricsCollector option
      Resilience: ResilienceConfig
      /// V — Verification: readiness checks, trace store, and judge
      ReadinessChecks: IReadinessCheck list
      TraceStore: ITraceStore option
      Judge: IJudge option
      /// G — Governance: permissions, constitution, audit, policies
      Constitution: Constitution option
      AuditLog: IAuditLog option
      PolicyEngine: PolicyEngine option
      /// Durable event bus turn-progress signals are published to
      Bus: IEventBus
      /// Scope identifying the turn whose progress signals are published (ActionId = turnId)
      Scope: EventScope }

    static member Default =
        { Execution = SandboxConfig.Default
          ToolProtocol = None
          ExecutionJournal = None
          Lifecycle = []
          Tracer = None
          Metrics = None
          Resilience = ResilienceConfig.NoResilience
          ReadinessChecks = []
          TraceStore = None
          Judge = None
          Constitution = None
          AuditLog = None
          PolicyEngine = None
          Bus = EventBus.none
          Scope = EventScope.Empty }

    static member WithObservability (tracer: ITracer) (metrics: IMetricsCollector) =
        { EtclovgConfig.Default with Tracer = Some tracer; Metrics = Some metrics }

    /// Overlay host-provided pluggable services onto this config. A service that is
    /// `Some` overrides the current value; `None` leaves the existing value intact.
    member this.WithServices(services: IHarnessServices) =
        { this with
            Tracer = services.Tracer |> Option.orElse this.Tracer
            Metrics = services.Metrics |> Option.orElse this.Metrics
            ExecutionJournal = services.ExecutionJournal |> Option.orElse this.ExecutionJournal
            TraceStore = services.TraceStore |> Option.orElse this.TraceStore
            AuditLog = services.AuditLog |> Option.orElse this.AuditLog }

/// Result of an ETCLOVG harness execution
type EtclovgResult =
    { /// The agent's final response (if successful)
      Response: string option
      /// Whether execution succeeded
      Success: bool
    /// Structured execution error
      HarnessError: HarnessError option
      /// Resource usage
      Usage: ResourceUsage
      /// Execution trace
      Trace: ExecutionTrace option
      /// Metrics collected during execution
      Metrics: ExecutionMetrics option
      /// Judgement result (if judge configured)
      Judgement: JudgementResult option
      /// Regression result (if baseline available)
      Regression: RegressionResult option
      /// Audit entries generated
      AuditEntries: int
      /// Policy violations
      PolicyViolations: PolicyViolation list
      /// Constitution violations
      ConstitutionViolations: ConstitutionViolation list }

/// The ETCLOVG Harness — integrates all seven layers into a unified execution pipeline
module EtclovgHarness =

    let private failResult (harnessError: HarnessError) (usage: ResourceUsage) (trace: ExecutionTrace) (policyViolations: PolicyViolation list) (constitutionViolations: ConstitutionViolation list) (metrics: IMetricsCollector option) (auditEntries: int) : EtclovgResult =
          { Response = None
            Success = false
            HarnessError = Some harnessError
            Usage = usage
            Trace = Some trace
            Metrics = metrics |> Option.map (fun m -> m.GetMetrics())
            Judgement = None
            Regression = None
            AuditEntries = auditEntries
            PolicyViolations = policyViolations
            ConstitutionViolations = constitutionViolations }

    /// Run an agent through the full ETCLOVG harness
    let runAsync (config: EtclovgConfig) (agentContext: AgentContext) (agent: IAgent) (input: string) : Task<EtclovgResult> =
        task {
            let execCtx = ExecutionContext.Create config.Execution
            let mutable trace = Verification.startTrace agent.Id input
            let mutable policyViolations = []
            let mutable constitutionViolations = []

            // === G: Policy engine pre-check ===
            let policyBlocked =
                match config.PolicyEngine with
                | Some engine ->
                    let ctx = PolicyContext.FromExecutionContext agent.Id "execute" (Some input) execCtx
                    let result = engine.Evaluate(ctx)
                    policyViolations <- result.Violations
                    if not result.Proceed then
                        Some (result.Violations |> List.map (fun v -> v.Message))
                    else None
                | None -> None

            match policyBlocked with
            | Some violations ->
                return failResult (HarnessError.PolicyBlocked violations) execCtx.Usage trace policyViolations [] None 0
            | None ->

            // === V: Verification — Readiness checks ===
            let! readiness =
                if config.ReadinessChecks.Length > 0 then
                    Verification.checkReadiness config.ReadinessChecks agent.Id input
                else
                    Task.FromResult ReadinessResult.Ready

            match readiness with
            | ReadinessResult.NotReady reasons ->
                return failResult (HarnessError.NotReady reasons) execCtx.Usage trace policyViolations [] None 0
            | ReadinessResult.Ready ->

            // === L: Lifecycle — Initialize ===
            let lifecycle = AgentLifecycle.create () |> AgentLifecycle.withHooks config.Lifecycle
            let! initResult = AgentLifecycle.initializeAsync agent.Id lifecycle

            match initResult with
            | Error msg ->
                return failResult (HarnessError.InitializationFailed msg) execCtx.Usage trace policyViolations [] None 0
            | Ok initializedLc ->

            // === L: Lifecycle — Start ===
            let! _startedLc = AgentLifecycle.startAsync agent.Id input initializedLc

            // === O: Observability — Start trace span ===
            let rootSpan =
                config.Tracer
                |> Option.map (fun t ->
                    let s = t.StartTrace(sprintf "harness:%s" agent.Name)
                    t.SetAttributes s (Map.ofList ["agent.name", agent.Name; "input", input; "execution.id", string execCtx.ExecutionId])
                    s)

            // === T: Tool Protocol — Record available tools in span ===
            match config.ToolProtocol, rootSpan, config.Tracer with
            | Some protocol, Some span, Some tracer ->
                let! tools = protocol.ListTools()
                let toolNames = tools |> List.map (fun t -> t.Name) |> String.concat ","
                tracer.SetAttributes span (Map.ofList ["tools.available", toolNames; "tools.count", string tools.Length])
            | _ -> ()

            // === E: Execution — Run agent within sandbox ===
            let sw = Stopwatch.StartNew()
            let execSpan =
                match rootSpan, config.Tracer with
                | Some parent, Some tracer ->
                    let s = tracer.StartSpan parent "agent.execute"
                    tracer.SetAttributes s (Map.ofList ["sandbox.isolation", string config.Execution.Isolation])
                    Some s
                | _ -> None
            // O: hand the orchestrator a tracing context so every tool it invokes is recorded
            // as a child span (tool name, parameters, round) under agent.execute.
            match box agent, execSpan, config.Tracer with
            | (:? OrchestratorBase as orch), Some parent, Some tracer -> orch.TraceContext <- Some (tracer, parent)
            | _ -> ()
            let previousMetrics = RuntimeMetrics.get ()
            RuntimeMetrics.set config.Metrics
            let env = ExecutionEnvironment.local ()
            let! execResult =
                task {
                    try
                        return! env.ExecuteAsync execCtx agentContext agent input
                    finally
                        match box agent with
                        | :? OrchestratorBase as orch -> orch.TraceContext <- None
                        | _ -> ()
                        RuntimeMetrics.set previousMetrics
                }
            sw.Stop()

            // === O: End execution span ===
            match execSpan, config.Tracer with
            | Some s, Some tracer ->
                match execResult with
                | Ok _ -> tracer.EndSpan s SpanStatus.Ok
                | Error e -> tracer.EndSpan s (SpanStatus.Error (sprintf "%A" e))
            | _ -> ()

            match execResult with
            | Error limitExceeded ->
                let! _ = AgentLifecycle.failAsync agent.Id (exn (sprintf "Limit exceeded: %A" limitExceeded)) _startedLc
                trace <- trace |> Verification.fail (sprintf "Limit exceeded: %A" limitExceeded)
                match config.TraceStore with
                | Some store -> do! store.SaveAsync trace
                | None -> ()
                // End root span on failure
                match rootSpan, config.Tracer with
                | Some s, Some tracer -> tracer.EndSpan s (SpanStatus.Error (sprintf "Limit exceeded: %A" limitExceeded))
                | _ -> ()
                return failResult (HarnessError.ResourceLimitExceeded limitExceeded) execCtx.Usage trace policyViolations [] config.Metrics 0

            | Ok response ->
                // === G: Constitution — Check output ===
                let constitutionBlocked =
                    match config.Constitution with
                    | Some constitution ->
                        let checkResult = Constitution.check constitution response
                        constitutionViolations <- checkResult.Violations
                        Constitution.hasHardViolations checkResult
                    | None -> false

                if constitutionBlocked then
                    // Audit the violation
                    match config.AuditLog with
                    | Some audit ->
                        let violationNames = constitutionViolations |> List.map (fun v -> v.RuleId)
                        do! audit.RecordAsync (AuditLog.constitutionCheck agent.Id violationNames (Some execCtx.ExecutionId))
                    | None -> ()
                    let! _ = AgentLifecycle.failAsync agent.Id (exn "Constitution violation") _startedLc
                    // End root span on constitution violation
                    match rootSpan, config.Tracer with
                    | Some s, Some tracer -> tracer.EndSpan s (SpanStatus.Error "Constitution violation")
                    | _ -> ()
                    let violationIds = constitutionViolations |> List.map (fun v -> v.RuleId)
                    return
                        ({ Response = None
                           Success = false
                           HarnessError = Some (HarnessError.ConstitutionViolation violationIds)
                           Usage = execCtx.Usage
                           Trace = Some trace
                           Metrics = config.Metrics |> Option.map (fun metrics -> metrics.GetMetrics())
                           Judgement = None
                           Regression = None
                           AuditEntries = 1
                           PolicyViolations = policyViolations
                           ConstitutionViolations = constitutionViolations }: EtclovgResult)
                else

                // === L: Lifecycle — Complete ===
                let! _ = AgentLifecycle.completeAsync agent.Id response _startedLc

                // === V: Complete trace and store ===
                trace <- trace |> Verification.addStep (TraceAction.LlmCall "unknown") input response sw.ElapsedMilliseconds
                trace <- trace |> Verification.complete response

                // === V: Judge the execution ===
                let! judgement =
                    match config.Judge with
                    | Some judge ->
                        task {
                            let! j = judge.JudgeAsync trace
                            return Some j
                        }
                    | None -> Task.FromResult None

                // === V: Regression detection ===
                let! regression =
                    match config.TraceStore with
                    | Some store ->
                        task {
                            let! baseline = store.GetBaselineAsync agent.Id input
                            match baseline with
                            | Some b -> return Some (Regression.detect b trace)
                            | None -> return None
                        }
                    | None -> Task.FromResult None

                // === V: Save trace ===
                match config.TraceStore with
                | Some store -> do! store.SaveAsync trace
                | None -> ()

                // === G: Audit ===
                match config.AuditLog with
                | Some audit ->
                    do! audit.RecordAsync (AuditLog.llmCall agent.Id "unknown" (Some execCtx.ExecutionId))
                | None -> ()

                // === O: End root span ===
                match rootSpan, config.Tracer with
                | Some s, Some tracer ->
                    tracer.AddEvent s "harness.complete" (Map.ofList ["response.length", string response.Length])
                    tracer.EndSpan s SpanStatus.Ok
                | _ -> ()

                config.Bus.PublishAsync(NaoEvent.TurnProgress(config.Scope, ProgressSignal.AnswerProduced response)) |> ignore

                return
                    { Response = Some response
                      Success = true
                      HarnessError = None
                      Usage = execCtx.Usage
                      Trace = Some trace
                      Metrics = config.Metrics |> Option.map (fun m -> m.GetMetrics())
                      Judgement = judgement
                      Regression = regression
                      AuditEntries = 1
                      PolicyViolations = policyViolations
                      ConstitutionViolations = constitutionViolations }
        }
