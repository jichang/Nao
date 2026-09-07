namespace Nao.Agents

open System
open System.Diagnostics
open System.Threading.Tasks
open Nao.Agents

/// Pluggable observability and governance capabilities injected into the harness.
type HarnessServices =
    { Tracer: Tracer option
      Metrics: MetricsCollector option
      ExecutionJournal: ExecutionJournal option
      TraceStore: TraceStore option
      AuditLog: AuditLog option }

/// Helpers for constructing harness-service records.
module HarnessServices =

    /// Services with nothing configured — every capability disabled.
    let none: HarnessServices =
        { Tracer = None
          Metrics = None
          ExecutionJournal = None
          TraceStore = None
          AuditLog = None }

    /// Build services from explicit optional components.
    let create
        (tracer: Tracer option)
        (metrics: MetricsCollector option)
        (executionJournal: ExecutionJournal option)
        (traceStore: TraceStore option)
        (auditLog: AuditLog option)
        : HarnessServices =
        { Tracer = tracer
          Metrics = metrics
          ExecutionJournal = executionJournal
          TraceStore = traceStore
          AuditLog = auditLog }

/// Complete ETCLOVG harness configuration wiring all seven layers together
type EtclovgConfig =
    { ToolProtocol: ToolProtocol option
      ProcessEnvironment: ExecutionEnvironment option
      ExecutionJournal: ExecutionJournal option
      ReplayFixtures: ReplayFixtures option
      Lifecycle: LifecycleHook list
      Tracer: Tracer option
      Metrics: MetricsCollector option
      Resilience: ResilienceConfig
      ReadinessChecks: ReadinessCheck list
      TraceStore: TraceStore option
      Judge: Judge option
      Constitution: Constitution option
      AuditLog: AuditLog option
      PolicyEngine: PolicyEngine option
      Bus: EventBus }

    static member Default =
        { ToolProtocol = None
          ProcessEnvironment = None
          ExecutionJournal = None
          ReplayFixtures = None
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
          Bus = EventBus.none }

    static member WithObservability (tracer: Tracer) (metrics: MetricsCollector) =
        { EtclovgConfig.Default with
            Tracer = Some tracer
            Metrics = Some metrics }

    /// Overlay host-provided pluggable services onto this config. A service that is
    /// `Some` overrides the current value; `None` leaves the existing value intact.
    member this.WithServices(services: HarnessServices) =
        { this with
            Tracer = services.Tracer |> Option.orElse this.Tracer
            Metrics = services.Metrics |> Option.orElse this.Metrics
            ExecutionJournal = services.ExecutionJournal |> Option.orElse this.ExecutionJournal
            TraceStore = services.TraceStore |> Option.orElse this.TraceStore
            AuditLog = services.AuditLog |> Option.orElse this.AuditLog }

/// The ETCLOVG Harness — integrates all seven layers into a unified execution pipeline
module EtclovgHarness =

    let private terminalStatus =
        function
        | HarnessError.PermissionDenied -> ExecutionTerminalStatus.Denied HarnessError.PermissionDenied
        | HarnessError.PolicyBlocked _ as error -> ExecutionTerminalStatus.Denied error
        | HarnessError.ConstitutionViolation _ as error -> ExecutionTerminalStatus.Denied error
        | HarnessError.ResourceLimitExceeded limit -> ExecutionTerminalStatus.LimitExceeded limit
        | error -> ExecutionTerminalStatus.Failed error

    let private harnessError (failure: PlatformFailure) =
        match failure.Category with
        | PlatformErrorCategory.PermissionDenied -> HarnessError.PermissionDenied
        | _ -> HarnessError.ExecutionFailed failure.Message

    let private eventScope (request: ExecutionRequest) =
        let userId = request.Authorization |> AuthorizationScope.userId |> UserId.value

        let sessionId =
            request.Authorization
            |> AuthorizationScope.sessionId
            |> Option.map SessionId.value
            |> Option.defaultValue ""

        let sessionKey =
            if String.IsNullOrEmpty sessionId then
                ""
            else
                sprintf "%s/%s" userId sessionId

        EventScope.Create(
            userId,
            sessionId,
            request.ConversationId,
            (request.Authorization |> AuthorizationScope.workspaceId |> WorkspaceId.value),
            (request.TurnId |> TurnId.value),
            sessionKey,
            request.Correlation
        )

    let private checkpointPhase =
        function
        | ExecutionTerminalStatus.Succeeded -> HarnessCheckpointPhase.Succeeded
        | ExecutionTerminalStatus.Failed _ -> HarnessCheckpointPhase.Failed
        | ExecutionTerminalStatus.Denied _ -> HarnessCheckpointPhase.Denied
        | ExecutionTerminalStatus.Cancelled -> HarnessCheckpointPhase.Cancelled
        | ExecutionTerminalStatus.TimedOut -> HarnessCheckpointPhase.TimedOut
        | ExecutionTerminalStatus.LimitExceeded _ -> HarnessCheckpointPhase.LimitExceeded
        | ExecutionTerminalStatus.Indeterminate _ -> HarnessCheckpointPhase.Indeterminate

    let private saveCheckpoint config request phase =
        match config.ExecutionJournal with
        | None -> Task.CompletedTask
        | Some journal ->
            let scope = eventScope request

            let owner =
                if String.IsNullOrWhiteSpace scope.SessionKey then
                    sprintf "execution:%s" (ExecutionId.serialize request.Correlation.ExecutionId)
                else
                    scope.SessionKey

            journal.Checkpoints.Save
                { Id = Guid.NewGuid()
                  IdempotencyKey = request.IdempotencyKey
                  Correlation = request.Correlation
                  Owner = owner
                  TurnId = request.TurnId |> TurnId.value
                  AgentId = request.AgentId
                  Phase = phase
                  RecordedAt = DateTimeOffset.UtcNow }

    let private failResult
        (harnessError: HarnessError)
        (correlation: CorrelationContext)
        (artifacts: Artifact list)
        (usage: ResourceUsage)
        (trace: ExecutionTrace)
        (policyViolations: PolicyViolation list)
        (constitutionViolations: ConstitutionViolation list)
        (metricsOwner: string)
        (metrics: MetricsCollector option)
        (auditEntries: int)
        : ExecutionResult =
        let outputs: ExecutionOutputs =
            { Response = None
              Artifacts = artifacts }

        let evidence: ExecutionEvidence =
            { Trace = Some trace
              Metrics = metrics |> Option.map (fun value -> value.GetMetrics metricsOwner)
              Judgement = None
              Regression = None
              AuditEntries = auditEntries }

        let decisions: ExecutionPolicyDecisions =
            { PolicyViolations = policyViolations
              ConstitutionViolations = constitutionViolations }

        { Correlation = correlation
          Status = terminalStatus harnessError
          Outputs = outputs
          Usage = usage
          Evidence = evidence
          PolicyDecisions = decisions }

    let private successResult correlation response artifacts usage trace metrics judgement regression decisions =
        let outputs: ExecutionOutputs =
            { Response = Some response
              Artifacts = artifacts }

        let evidence: ExecutionEvidence =
            { Trace = Some trace
              Metrics = metrics
              Judgement = judgement
              Regression = regression
              AuditEntries = 1 }

        { Correlation = correlation
          Status = ExecutionTerminalStatus.Succeeded
          Outputs = outputs
          Usage = usage
          Evidence = evidence
          PolicyDecisions = decisions }

    let private stoppedResult status correlation artifacts usage trace policyViolations sessionKey metrics =
        let result =
            failResult
                (HarnessError.ExecutionFailed(status.ToString()))
                correlation
                artifacts
                usage
                trace
                policyViolations
                []
                sessionKey
                metrics
                0

        { result with Status = status }

    [<RequireQualifiedAccess>]
    type private ResumeDecision =
        | Execute
        | Stop of message: string

    let private isTerminalCheckpoint =
        function
        | HarnessCheckpointPhase.Accepted
        | HarnessCheckpointPhase.ExecutionStarted -> false
        | HarnessCheckpointPhase.Succeeded
        | HarnessCheckpointPhase.Failed
        | HarnessCheckpointPhase.Denied
        | HarnessCheckpointPhase.Cancelled
        | HarnessCheckpointPhase.TimedOut
        | HarnessCheckpointPhase.LimitExceeded
        | HarnessCheckpointPhase.Indeterminate -> true

    let private resumeDecision (config: EtclovgConfig) (request: ExecutionRequest) =
        task {
            match config.ExecutionJournal with
            | None -> return ResumeDecision.Execute
            | Some journal ->
                let! checkpoints = journal.Checkpoints.GetByIdempotencyKey request.IdempotencyKey

                if checkpoints |> List.exists (_.Phase >> isTerminalCheckpoint) then
                    return
                        ResumeDecision.Stop
                            "Logical execution already reached a terminal checkpoint; its durable result must be recovered instead of executing again."
                else
                    let executionIds =
                        checkpoints |> List.map _.Correlation.ExecutionId |> List.distinct

                    let! sideEffectGroups = executionIds |> List.map journal.SideEffects.GetByExecution |> Task.WhenAll

                    let latestSideEffects =
                        sideEffectGroups
                        |> Seq.collect id
                        |> Seq.groupBy _.IdempotencyKey
                        |> Seq.map (fun (_, transitions) -> transitions |> Seq.maxBy _.RecordedAt)
                        |> Seq.toList

                    let ambiguousSideEffects =
                        latestSideEffects
                        |> Seq.filter (fun transition ->
                            match transition.Phase with
                            | ToolSideEffectPhase.IntentRecorded
                            | ToolSideEffectPhase.Interrupted
                            | ToolSideEffectPhase.CompensationFailed
                            | ToolSideEffectPhase.Succeeded
                            | ToolSideEffectPhase.Quarantined -> true
                            | ToolSideEffectPhase.Failed
                            | ToolSideEffectPhase.CompensationSucceeded -> false)
                        |> Seq.toList

                    if not ambiguousSideEffects.IsEmpty then
                        for transition in
                            ambiguousSideEffects
                            |> List.filter (fun transition -> transition.Phase <> ToolSideEffectPhase.Quarantined) do
                            do!
                                journal.SideEffects.Save
                                    { transition with
                                        Id = Guid.NewGuid()
                                        Phase = ToolSideEffectPhase.Quarantined
                                        Detail =
                                            Some "Execution ended before a terminal checkpoint confirmed safe recovery."
                                        RecordedAt = DateTimeOffset.UtcNow }

                        return
                            ResumeDecision.Stop
                                "Interrupted execution has an external side effect without a confirmed durable outcome; automatic replay is unsafe."
                    else
                        return ResumeDecision.Execute
        }

    let private indeterminateResult (config: EtclovgConfig) (agent: Agent) (request: ExecutionRequest) message =
        let scope = eventScope request

        let trace =
            Verification.startTrace request.Correlation agent.Metadata.Id request.Input

        stoppedResult
            (ExecutionTerminalStatus.Indeterminate message)
            request.Correlation
            []
            ResourceUsage.Zero
            trace
            []
            scope.SessionKey
            config.Metrics

    let rec private runAgentAsync
        (config: EtclovgConfig)
        (agentContext: AgentContext)
        (agent: Agent)
        (request: ExecutionRequest)
        (parentExecutionContext: ExecutionContext option)
        (executionCancellation: System.Threading.CancellationToken)
        (callerCancellation: System.Threading.CancellationToken)
        : Task<ExecutionResult> =
        task {
            let input = request.Input
            let scope = eventScope request
            let artifacts = ResizeArray<Artifact>()

            let execCtx =
                match parentExecutionContext with
                | Some parent -> parent.CreateChild(request.Correlation)
                | None ->
                    ExecutionContext.CreateForRequest
                        request.Sandbox
                        request.Correlation
                        request.IdempotencyKey
                        executionCancellation

            let publishArtifact = agentContext.PublishArtifact

            let executeChild childContext (child: Agent) (childInput: string) =
                task {
                    let childCorrelation = CorrelationContext.delegateFrom request.Correlation

                    let childRequest =
                        ExecutionRequest.create
                            request.Authorization
                            request.TurnId
                            request.ConversationId
                            child.Metadata.Id
                            childInput
                            request.Sandbox
                            request.PolicyVersions
                            request.DependencyVersions
                            childCorrelation

                    let! result =
                        runAgentAsync
                            config
                            childContext
                            child
                            childRequest
                            (Some execCtx)
                            executionCancellation
                            callerCancellation

                    artifacts.AddRange result.Outputs.Artifacts

                    match result.Status, result.Outputs.Response with
                    | ExecutionTerminalStatus.Succeeded, Some response -> return Ok response
                    | ExecutionTerminalStatus.Succeeded, None ->
                        return
                            Error(
                                PlatformFailure.create
                                    PlatformErrorCategory.InvalidOutput
                                    "Child execution succeeded without producing a response."
                                    false
                                    (childCorrelation.ExecutionId |> ExecutionId.serialize |> Some)
                            )
                    | ExecutionTerminalStatus.LimitExceeded limit, _ ->
                        return raise (ExecutionLimitExceededException limit)
                    | ExecutionTerminalStatus.Cancelled, _
                    | ExecutionTerminalStatus.TimedOut, _ ->
                        return raise (OperationCanceledException(execCtx.CancellationToken))
                    | status, _ ->
                        return
                            Error(
                                status.ToPlatformFailure(childCorrelation.ExecutionId |> ExecutionId.serialize |> Some)
                            )
                }

            let reversibleExecutions = ResizeArray<ExecutionRecord * Tool>()
            let mutable ambiguousOutcome = None

            let successfulToolRecord
                (toolContext: AgentContext)
                (idempotencyKey: IdempotencyKey)
                (tool: Tool)
                (input: string)
                (output: string)
                (durationMs: int64)
                : ExecutionRecord =
                { Id = Guid.NewGuid()
                  IdempotencyKey = idempotencyKey
                  Correlation = request.Correlation
                  Owner = toolContext.SessionKey
                  TurnId = toolContext.TurnId
                  ToolName = tool.Name
                  ReplaySafety = tool.ReplaySafety
                  Input = input
                  Output = output
                  Failure = None
                  ExecutedAt = DateTimeOffset.UtcNow
                  Reverted = false
                  Metadata = Map.ofList [ "duration.ms", string durationMs ] }

            let compensateReversibleExecutions () =
                task {
                    match config.ExecutionJournal with
                    | None -> ()
                    | Some journal ->
                        for record, tool in reversibleExecutions |> Seq.rev do
                            let revertContext: RevertContext =
                                { Input = record.Input
                                  Output = record.Output
                                  ExecutedAt = record.ExecutedAt
                                  Metadata = record.Metadata }

                            let! result =
                                task {
                                    try
                                        return! Tool.revertAsync revertContext tool
                                    with error ->
                                        return Error error.Message
                                }

                            let phase, detail =
                                match result with
                                | Ok() -> ToolSideEffectPhase.CompensationSucceeded, None
                                | Error message -> ToolSideEffectPhase.CompensationFailed, Some message

                            do!
                                journal.SideEffects.Save
                                    { Id = Guid.NewGuid()
                                      IdempotencyKey = record.IdempotencyKey
                                      Correlation = record.Correlation
                                      Owner = record.Owner
                                      TurnId = record.TurnId
                                      ToolName = record.ToolName
                                      Input = record.Input
                                      Phase = phase
                                      Detail = detail
                                      RecordedAt = DateTimeOffset.UtcNow }

                            match result with
                            | Ok() -> do! journal.MarkRevertedAsync record.Id
                            | Error _ -> ()
                }

            let executeTool
                (toolContext: AgentContext)
                (idempotencyKey: IdempotencyKey)
                (tool: Tool)
                (toolInput: string)
                =
                task {
                    let policyResult =
                        config.PolicyEngine
                        |> Option.map (fun engine ->
                            PolicyContext.FromExecutionContext
                                agent.Metadata.Id
                                (sprintf "tool.execute:%s" tool.Name)
                                (Some toolInput)
                                execCtx
                            |> engine.Evaluate)

                    match policyResult with
                    | Some result when not result.Proceed ->
                        let message = result.Violations |> List.map _.Message |> String.concat "; "

                        return
                            Error(
                                PlatformFailure.create
                                    PlatformErrorCategory.PermissionDenied
                                    (sprintf "Blocked by policy: %s" message)
                                    false
                                    (request.Correlation.ExecutionId |> ExecutionId.serialize |> Some)
                            )
                    | result ->
                        let effectiveToolInput =
                            result |> Option.bind _.ModifiedInput |> Option.defaultValue toolInput

                        let correlationId = request.Correlation.ExecutionId |> ExecutionId.serialize |> Some

                        let replayFixture =
                            config.ReplayFixtures
                            |> Option.map (fun fixtures ->
                                match fixtures.Tools |> Map.tryFind idempotencyKey with
                                | Some(ReplayFixture.Recorded fixture) when
                                    fixture.ToolName = tool.Name
                                    && fixture.Input = effectiveToolInput
                                    && fixture.Result.IdempotencyKey = idempotencyKey
                                    && fixture.Result.ReplaySafety = tool.ReplaySafety
                                    ->
                                    fixture
                                | Some(ReplayFixture.Recorded _) ->
                                    raise (
                                        GovernedExecutionException(
                                            sprintf
                                                "Tool replay fixture '%s' does not match the governed invocation."
                                                (IdempotencyKey.value idempotencyKey)
                                        )
                                    )
                                | Some(ReplayFixture.NonReplayable reason) ->
                                    raise (
                                        GovernedExecutionException(
                                            sprintf
                                                "Tool dependency '%s' is explicitly non-replayable: %s"
                                                (IdempotencyKey.value idempotencyKey)
                                                reason
                                        )
                                    )
                                | None ->
                                    raise (
                                        GovernedExecutionException(
                                            sprintf
                                                "Tool replay fixture '%s' is missing; live tool access is disabled."
                                                (IdempotencyKey.value idempotencyKey)
                                        )
                                    ))

                        match execCtx.BeginToolCall() with
                        | Some limit -> return raise (ExecutionLimitExceededException limit)
                        | None ->
                            match replayFixture with
                            | Some fixture when fixture.Result.Success -> return Ok fixture.Result.Output
                            | Some fixture ->
                                return
                                    fixture.Result.Failure
                                    |> Option.map (fun failure -> failure.ToPlatformFailure correlationId)
                                    |> Option.defaultWith (fun () ->
                                        PlatformFailure.create
                                            PlatformErrorCategory.PermanentDependency
                                            (fixture.Result.Error |> Option.defaultValue "Recorded tool failure.")
                                            false
                                            correlationId)
                                    |> Error
                            | None ->
                                let journal = config.ExecutionJournal

                                if Tool.canRevert tool && journal.IsNone then
                                    raise (
                                        GovernedExecutionException
                                            "Reversible tools require a durable execution journal."
                                    )

                                let saveSideEffect phase detail =
                                    if tool.ReplaySafety = ToolReplaySafety.RetrySafe && not (Tool.canRevert tool) then
                                        Task.CompletedTask
                                    else
                                        match journal with
                                        | Some value ->
                                            value.SideEffects.Save
                                                { Id = Guid.NewGuid()
                                                  IdempotencyKey = idempotencyKey
                                                  Correlation = request.Correlation
                                                  Owner = toolContext.SessionKey
                                                  TurnId = toolContext.TurnId
                                                  ToolName = tool.Name
                                                  Input = effectiveToolInput
                                                  Phase = phase
                                                  Detail = detail
                                                  RecordedAt = DateTimeOffset.UtcNow }
                                        | None ->
                                            raise (
                                                GovernedExecutionException
                                                    "Externally side-effecting tools require a durable execution journal."
                                            )

                                do! saveSideEffect ToolSideEffectPhase.IntentRecorded None
                                let started = Stopwatch.StartNew()

                                try
                                    let! result =
                                        (tool.RunAsync toolContext effectiveToolInput)
                                            .WaitAsync(execCtx.CancellationToken)

                                    started.Stop()

                                    match result with
                                    | Ok output ->
                                        do! saveSideEffect ToolSideEffectPhase.Succeeded None

                                        match journal with
                                        | Some value ->
                                            let record =
                                                successfulToolRecord
                                                    toolContext
                                                    idempotencyKey
                                                    tool
                                                    effectiveToolInput
                                                    output
                                                    started.ElapsedMilliseconds

                                            do! value.RecordAsync record

                                            if Tool.canRevert tool then
                                                reversibleExecutions.Add(record, tool)
                                        | None -> ()

                                        return Ok output
                                    | Error failure ->
                                        do! saveSideEffect ToolSideEffectPhase.Failed (Some failure.Message)

                                        if
                                            tool.ReplaySafety = ToolReplaySafety.ExternallySideEffecting
                                            && failure.Category = PlatformErrorCategory.TransientDependency
                                        then
                                            ambiguousOutcome <-
                                                Some
                                                    "An external dependency failure left the side-effect outcome ambiguous; the operation was quarantined."

                                            do!
                                                saveSideEffect
                                                    ToolSideEffectPhase.Quarantined
                                                    (Some
                                                        "The dependency failure did not confirm whether the side effect committed.")

                                        match journal with
                                        | Some value ->
                                            do!
                                                value.RecordAsync
                                                    { Id = Guid.NewGuid()
                                                      IdempotencyKey = idempotencyKey
                                                      Correlation = request.Correlation
                                                      Owner = toolContext.SessionKey
                                                      TurnId = toolContext.TurnId
                                                      ToolName = tool.Name
                                                      ReplaySafety = tool.ReplaySafety
                                                      Input = effectiveToolInput
                                                      Output = ""
                                                      Failure = Some failure
                                                      ExecutedAt = DateTimeOffset.UtcNow
                                                      Reverted = false
                                                      Metadata =
                                                        Map.ofList [ "duration.ms", string started.ElapsedMilliseconds ] }
                                        | None -> ()

                                        return Error(failure.ToPlatformFailure correlationId)
                                with error ->
                                    do! saveSideEffect ToolSideEffectPhase.Interrupted (Some error.Message)

                                    if tool.ReplaySafety = ToolReplaySafety.ExternallySideEffecting then
                                        ambiguousOutcome <-
                                            Some
                                                "An interrupted external operation left the side-effect outcome ambiguous; the operation was quarantined."

                                        do!
                                            saveSideEffect
                                                ToolSideEffectPhase.Quarantined
                                                (Some "Execution was interrupted after side-effect intent was recorded.")

                                    return raise error
                }

            let publishCapturedArtifact artifact : Task =
                (task {
                    do! publishArtifact artifact
                    artifacts.Add artifact
                }
                :> Task)

            let governedContext =
                { agentContext with
                    Correlation = request.Correlation
                    SessionKey = scope.SessionKey
                    TurnId = request.TurnId |> TurnId.value
                    ExecutionBoundary = ExecutionBoundary.HarnessRequired
                    CancellationToken = execCtx.CancellationToken
                    NextToolCallKey = execCtx.NextToolCallKey
                    NextArtifactKey = execCtx.NextArtifactKey
                    PublishArtifact = publishCapturedArtifact }

            let dispatcher =
                { RunAgent = executeChild
                  RunTool = executeTool }

            let mutable policyViolations = []
            let mutable constitutionViolations = []
            let mutable effectiveInput = input

            // === G: Identity and policy pre-checks ===
            let preflightError =
                if request.AgentId <> agent.Metadata.Id then
                    Some HarnessError.PermissionDenied
                else
                    match config.PolicyEngine with
                    | Some engine ->
                        let ctx =
                            PolicyContext.FromExecutionContext agent.Metadata.Id "execute" (Some input) execCtx

                        let result = engine.Evaluate(ctx)
                        policyViolations <- result.Violations
                        effectiveInput <- result.ModifiedInput |> Option.defaultValue input

                        if not result.Proceed then
                            result.Violations
                            |> List.map (fun violation -> violation.Message)
                            |> HarnessError.PolicyBlocked
                            |> Some
                        else
                            None
                    | None -> None

            let mutable trace =
                Verification.startTrace execCtx.Correlation agent.Metadata.Id effectiveInput

            match preflightError with
            | Some error ->
                return
                    failResult
                        error
                        request.Correlation
                        (List.ofSeq artifacts)
                        execCtx.Usage
                        trace
                        policyViolations
                        []
                        agentContext.SessionKey
                        None
                        0
            | None ->

                // === V: Verification — Readiness checks ===
                let! readiness =
                    if config.ReadinessChecks.Length > 0 then
                        Verification.checkReadiness config.ReadinessChecks agent.Metadata.Id effectiveInput
                    else
                        Task.FromResult ReadinessResult.Ready

                match readiness with
                | ReadinessResult.NotReady reasons ->
                    return
                        failResult
                            (HarnessError.NotReady reasons)
                            request.Correlation
                            (List.ofSeq artifacts)
                            execCtx.Usage
                            trace
                            policyViolations
                            []
                            agentContext.SessionKey
                            None
                            0
                | ReadinessResult.Ready ->

                    // === L: Lifecycle — Initialize ===
                    let lifecycle =
                        AgentLifecycle.create () |> AgentLifecycle.withHooks config.Lifecycle

                    let! initResult = AgentLifecycle.initializeAsync agent.Metadata.Id lifecycle

                    match initResult with
                    | Error msg ->
                        return
                            failResult
                                (HarnessError.InitializationFailed msg)
                                request.Correlation
                                (List.ofSeq artifacts)
                                execCtx.Usage
                                trace
                                policyViolations
                                []
                                agentContext.SessionKey
                                None
                                0
                    | Ok initializedLc ->

                        // === L: Lifecycle — Start ===
                        let! _startedLc = AgentLifecycle.startAsync agent.Metadata.Id effectiveInput initializedLc

                        if parentExecutionContext.IsNone then
                            do! saveCheckpoint config request HarnessCheckpointPhase.ExecutionStarted

                        // === O: Observability — Start trace span ===
                        let rootSpan =
                            config.Tracer
                            |> Option.map (fun t ->
                                let s = t.StartTrace execCtx.Correlation (sprintf "harness:%s" agent.Metadata.Name)

                                t.SetAttributes
                                    s
                                    (Map.ofList
                                        [ "agent.name", agent.Metadata.Name
                                          "input", effectiveInput
                                          "execution.id", ExecutionId.serialize execCtx.ExecutionId ])

                                s)

                        // === T: Tool Protocol — Record available tools in span ===
                        match config.ToolProtocol, rootSpan, config.Tracer with
                        | Some protocol, Some span, Some tracer ->
                            let! tools = protocol.ListTools()
                            let toolNames = tools |> List.map (fun t -> t.Name) |> String.concat ","

                            tracer.SetAttributes
                                span
                                (Map.ofList [ "tools.available", toolNames; "tools.count", string tools.Length ])
                        | _ -> ()

                        // === E: Execution — Run agent within sandbox ===
                        let sw = Stopwatch.StartNew()

                        let execSpan =
                            match rootSpan, config.Tracer with
                            | Some parent, Some tracer ->
                                let s = tracer.StartSpan parent "agent.execute"

                                tracer.SetAttributes
                                    s
                                    (Map.ofList [ "sandbox.isolation", string request.Sandbox.Isolation ])

                                Some s
                            | _ -> None
                        // O: hand the orchestrator a tracing context so every tool it invokes is recorded
                        // as a child span (tool name, parameters, round) under agent.execute.
                        let previousMetrics = RuntimeMetrics.get ()
                        let previousJournal = RuntimeExecutionJournal.get ()
                        let previousReplayFixtures = RuntimeReplayFixtures.get ()
                        let previousDispatcher = ExecutionRuntime.get ()
                        let previousBudget = RuntimeExecutionBudget.get ()
                        RuntimeMetrics.set config.Metrics
                        RuntimeExecutionJournal.set config.ExecutionJournal
                        RuntimeReplayFixtures.set config.ReplayFixtures
                        ExecutionRuntime.set (Some dispatcher)
                        RuntimeExecutionBudget.set (Some execCtx)

                        let! executionOutcome =
                            task {
                                try
                                    try
                                        let env =
                                            match request.Sandbox.Isolation with
                                            | SandboxIsolation.None -> ExecutionEnvironment.local ()
                                            | SandboxIsolation.Process ->
                                                config.ProcessEnvironment
                                                |> Option.defaultWith (fun () ->
                                                    raise (
                                                        GovernedExecutionException
                                                            "Process isolation requires a configured process execution environment."
                                                    ))
                                            | SandboxIsolation.Container ->
                                                raise (
                                                    GovernedExecutionException
                                                        "Container isolation requires a configured container execution environment."
                                                )

                                        let! result = env.ExecuteAsync execCtx governedContext agent effectiveInput
                                        return Ok result
                                    with
                                    | ExecutionLimitExceededException limit ->
                                        return Error(ExecutionTerminalStatus.LimitExceeded limit)
                                    | :? OperationCanceledException ->
                                        if callerCancellation.IsCancellationRequested then
                                            return Error ExecutionTerminalStatus.Cancelled
                                        else
                                            return Error ExecutionTerminalStatus.TimedOut
                                    | error ->
                                        return
                                            error
                                            |> PlatformFailure.fromException
                                                PlatformFailureBoundary.Agent
                                                (request.Correlation.ExecutionId |> ExecutionId.serialize |> Some)
                                            |> harnessError
                                            |> terminalStatus
                                            |> Error
                                finally
                                    RuntimeMetrics.set previousMetrics
                                    RuntimeExecutionJournal.set previousJournal
                                    RuntimeReplayFixtures.set previousReplayFixtures
                                    ExecutionRuntime.set previousDispatcher
                                    RuntimeExecutionBudget.set previousBudget
                            }

                        sw.Stop()

                        let normalizedExecutionOutcome =
                            match executionOutcome with
                            | Ok(Error LimitExceeded.Duration) -> Error ExecutionTerminalStatus.TimedOut
                            | Error(ExecutionTerminalStatus.LimitExceeded LimitExceeded.Duration) ->
                                Error ExecutionTerminalStatus.TimedOut
                            | outcome -> outcome

                        let executionOutcome =
                            match ambiguousOutcome with
                            | Some message -> Error(ExecutionTerminalStatus.Indeterminate message)
                            | None -> normalizedExecutionOutcome

                        // === O: End execution span ===
                        match execSpan, config.Tracer with
                        | Some s, Some tracer ->
                            match executionOutcome with
                            | Ok(Ok _) -> tracer.EndSpan s SpanStatus.Ok
                            | Ok(Error limit) -> tracer.EndSpan s (SpanStatus.Error(sprintf "%A" limit))
                            | Error status ->
                                let failure =
                                    status.ToPlatformFailure(
                                        request.Correlation.ExecutionId |> ExecutionId.serialize |> Some
                                    )

                                tracer.EndSpan s (SpanStatus.Error failure.Message)
                        | _ -> ()

                        match executionOutcome with
                        | Error status ->
                            do! compensateReversibleExecutions ()

                            let failure =
                                status.ToPlatformFailure(
                                    request.Correlation.ExecutionId |> ExecutionId.serialize |> Some
                                )

                            let! _ = AgentLifecycle.failAsync agent.Metadata.Id (exn failure.Message) _startedLc
                            trace <- trace |> Verification.fail failure.Message

                            match config.TraceStore with
                            | Some store -> do! store.SaveAsync trace
                            | None -> ()

                            match rootSpan, config.Tracer with
                            | Some span, Some tracer -> tracer.EndSpan span (SpanStatus.Error failure.Message)
                            | _ -> ()

                            return
                                stoppedResult
                                    status
                                    request.Correlation
                                    (List.ofSeq artifacts)
                                    execCtx.Usage
                                    trace
                                    policyViolations
                                    agentContext.SessionKey
                                    config.Metrics

                        | Ok(Error limitExceeded) ->
                            do! compensateReversibleExecutions ()

                            let! _ =
                                AgentLifecycle.failAsync
                                    agent.Metadata.Id
                                    (exn (sprintf "Limit exceeded: %A" limitExceeded))
                                    _startedLc

                            trace <- trace |> Verification.fail (sprintf "Limit exceeded: %A" limitExceeded)

                            match config.TraceStore with
                            | Some store -> do! store.SaveAsync trace
                            | None -> ()
                            // End root span on failure
                            match rootSpan, config.Tracer with
                            | Some s, Some tracer ->
                                tracer.EndSpan s (SpanStatus.Error(sprintf "Limit exceeded: %A" limitExceeded))
                            | _ -> ()

                            return
                                failResult
                                    (HarnessError.ResourceLimitExceeded limitExceeded)
                                    request.Correlation
                                    (List.ofSeq artifacts)
                                    execCtx.Usage
                                    trace
                                    policyViolations
                                    []
                                    agentContext.SessionKey
                                    config.Metrics
                                    0

                        | Ok(Ok response) ->
                            // === G: Constitution — Check output ===
                            let constitutionBlocked =
                                match config.Constitution with
                                | Some constitution ->
                                    let checkResult = Constitution.check constitution response
                                    constitutionViolations <- checkResult.Violations
                                    Constitution.hasHardViolations checkResult
                                | None -> false

                            if constitutionBlocked then
                                do! compensateReversibleExecutions ()

                                // Audit the violation
                                match config.AuditLog with
                                | Some audit ->
                                    let violationNames = constitutionViolations |> List.map (fun v -> v.RuleId)

                                    do!
                                        audit.RecordAsync(
                                            AuditLog.constitutionCheck
                                                agent.Metadata.Id
                                                violationNames
                                                (Some execCtx.ExecutionId)
                                        )
                                | None -> ()

                                let! _ =
                                    AgentLifecycle.failAsync agent.Metadata.Id (exn "Constitution violation") _startedLc
                                // End root span on constitution violation
                                match rootSpan, config.Tracer with
                                | Some s, Some tracer -> tracer.EndSpan s (SpanStatus.Error "Constitution violation")
                                | _ -> ()

                                let violationIds = constitutionViolations |> List.map (fun v -> v.RuleId)

                                return
                                    failResult
                                        (HarnessError.ConstitutionViolation violationIds)
                                        request.Correlation
                                        (List.ofSeq artifacts)
                                        execCtx.Usage
                                        trace
                                        policyViolations
                                        constitutionViolations
                                        agentContext.SessionKey
                                        config.Metrics
                                        1
                            else

                                // === L: Lifecycle — Complete ===
                                let! _ = AgentLifecycle.completeAsync agent.Metadata.Id response _startedLc

                                // === V: Complete trace and store ===
                                trace <-
                                    trace
                                    |> Verification.addStep
                                        (TraceAction.LlmCall "unknown")
                                        effectiveInput
                                        response
                                        sw.ElapsedMilliseconds

                                trace <- trace |> Verification.complete response

                                // === V: Judge the execution ===
                                let! judgement =
                                    match config.Judge with
                                    | Some judge ->
                                        task {
                                            let! j = Judge.judgeAsync trace judge
                                            return Some j
                                        }
                                    | None -> Task.FromResult None

                                // === V: Regression detection ===
                                let! regression =
                                    match config.TraceStore with
                                    | Some store ->
                                        task {
                                            let! baseline = store.GetBaselineAsync agent.Metadata.Id input

                                            match baseline with
                                            | Some b -> return Some(Regression.detect b trace)
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
                                    do!
                                        audit.RecordAsync(
                                            AuditLog.llmCall agent.Metadata.Id "unknown" (Some execCtx.ExecutionId)
                                        )
                                | None -> ()

                                // === O: End root span ===
                                match rootSpan, config.Tracer with
                                | Some s, Some tracer ->
                                    tracer.AddEvent
                                        s
                                        "harness.complete"
                                        (Map.ofList [ "response.length", string response.Length ])

                                    tracer.EndSpan s SpanStatus.Ok
                                | _ -> ()

                                EventBus.publishAsync
                                    (NaoEvent.TurnProgress(scope, ProgressSignal.AnswerProduced response))
                                    config.Bus
                                |> ignore

                                let metrics =
                                    config.Metrics
                                    |> Option.map (fun value -> value.GetMetrics agentContext.SessionKey)

                                let decisions: ExecutionPolicyDecisions =
                                    { PolicyViolations = policyViolations
                                      ConstitutionViolations = constitutionViolations }

                                return
                                    successResult
                                        request.Correlation
                                        response
                                        (List.ofSeq artifacts)
                                        execCtx.Usage
                                        trace
                                        metrics
                                        judgement
                                        regression
                                        decisions
        }

    /// Run an agent through the full ETCLOVG harness with caller cancellation.
    let runAsync
        (config: EtclovgConfig)
        (agentContext: AgentContext)
        (agent: Agent)
        (request: ExecutionRequest)
        cancellationToken
        =
        task {
            let! decision = resumeDecision config request

            match decision with
            | ResumeDecision.Stop message ->
                let result = indeterminateResult config agent request message
                do! saveCheckpoint config request HarnessCheckpointPhase.Indeterminate
                return result
            | ResumeDecision.Execute ->
                do! saveCheckpoint config request HarnessCheckpointPhase.Accepted

                use deadline =
                    new System.Threading.CancellationTokenSource(request.Sandbox.Limits.MaxDuration)

                use execution =
                    System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token)

                let! result = runAgentAsync config agentContext agent request None execution.Token cancellationToken
                do! saveCheckpoint config request (checkpointPhase result.Status)
                return result
        }
