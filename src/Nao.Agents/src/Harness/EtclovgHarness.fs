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
      ContainerEnvironment: ExecutionEnvironment option
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
      ConstitutionRemediationBroker: ConstitutionRemediationBroker
      AuditLog: AuditLog option
      PolicyEngine: PolicyEngine option
      PolicyConfirmationBroker: PolicyConfirmationBroker
      Bus: EventBus }

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
            (request.Authorization |> AuthorizationScope.tenantId |> TenantId.value),
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

    let private saveCheckpoint (config: EtclovgConfig) (request: ExecutionRequest) phase =
        match config.ExecutionJournal with
        | None -> Task.CompletedTask
        | Some journal ->
            journal.Checkpoints.Save
                request.Authorization
                { Id = Guid.NewGuid()
                  IdempotencyKey = request.IdempotencyKey
                  Correlation = request.Correlation
                  TurnId = request.TurnId |> TurnId.value
                  AgentId = request.AgentId
                  Phase = phase
                  RecordedAt = DateTimeOffset.UtcNow }

    let private terminalFailure =
        function
        | ExecutionTerminalStatus.Succeeded -> None
        | ExecutionTerminalStatus.Failed error
        | ExecutionTerminalStatus.Denied error -> Some error.Message
        | ExecutionTerminalStatus.Cancelled -> Some "Execution cancelled"
        | ExecutionTerminalStatus.TimedOut -> Some "Execution timed out"
        | ExecutionTerminalStatus.LimitExceeded limit -> Some(sprintf "Resource limit exceeded: %A" limit)
        | ExecutionTerminalStatus.Indeterminate message -> Some message

    let private saveTerminalOutcome (config: EtclovgConfig) (request: ExecutionRequest) (result: ExecutionResult) =
        match config.ExecutionJournal with
        | None -> Task.CompletedTask
        | Some journal ->
            journal.Outcomes.Save
                request.Authorization
                { Id = Guid.NewGuid()
                  IdempotencyKey = request.IdempotencyKey
                  Correlation = result.Correlation
                  TurnId = request.TurnId |> TurnId.value
                  AgentId = request.AgentId
                  Phase = checkpointPhase result.Status
                  Response = result.Outputs.Response
                  Artifacts = result.Outputs.Artifacts
                  Failure = terminalFailure result.Status
                  RecordedAt = DateTimeOffset.UtcNow }

    let private failResult
        (harnessError: HarnessError)
        (correlation: CorrelationContext)
        (artifacts: Artifact list)
        (usage: ResourceUsage)
        (trace: ExecutionTrace)
        (policyViolations: PolicyViolation list)
        (policyEvaluations: PolicyEvaluationEvidence list)
        (constitutionViolations: ConstitutionViolation list)
        (metricsScope: AuthorizationScope)
        (metrics: MetricsCollector option)
        (auditEntries: int)
        : ExecutionResult =
        let outputs: ExecutionOutputs =
            { Response = None
              Artifacts = artifacts }

        let evidence: ExecutionEvidence =
            { Trace = Some trace
              Metrics = metrics |> Option.map (fun value -> value.GetMetrics metricsScope)
              Judgement = None
              Regression = None
              AuditEntries = auditEntries }

        let decisions: ExecutionPolicyDecisions =
            { PolicyViolations = policyViolations
              PolicyEvaluations = policyEvaluations
              ConstitutionViolations = constitutionViolations
              ConstitutionDisposition = ConstitutionDisposition.NotEvaluated }

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

    let private stoppedResult
        status
        correlation
        artifacts
        usage
        trace
        policyViolations
        policyEvaluations
        authorization
        metrics
        =
        let result =
            failResult
                (HarnessError.ExecutionFailed(status.ToString()))
                correlation
                artifacts
                usage
                trace
                policyViolations
                policyEvaluations
                []
                authorization
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
                let! checkpoints = journal.Checkpoints.GetByIdempotencyKey request.Authorization request.IdempotencyKey

                if checkpoints |> List.exists (_.Phase >> isTerminalCheckpoint) then
                    return
                        ResumeDecision.Stop
                            "Logical execution already reached a terminal checkpoint; its durable result must be recovered instead of executing again."
                else
                    let executionIds =
                        checkpoints |> List.map _.Correlation.ExecutionId |> List.distinct

                    let! sideEffectGroups =
                        executionIds
                        |> List.map (journal.SideEffects.GetByExecution request.Authorization)
                        |> Task.WhenAll

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
                                    request.Authorization
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
            []
            request.Authorization
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
            let authorization =
                match parentExecutionContext with
                | None ->
                    AuthorizationScope.startAgent
                        agent.Metadata.Id
                        request.Correlation.ExecutionId
                        request.Authorization
                | Some _ ->
                    AuthorizationScope.requireAgent
                        agent.Metadata.Id
                        request.Correlation.ExecutionId
                        request.Authorization

                    request.Authorization

            let request =
                { request with
                    Authorization = authorization }

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

            let mutable policyViolations = []
            let mutable policyEvaluations = []
            let mutable constitutionViolations = []
            let mutable constitutionDisposition = ConstitutionDisposition.NotEvaluated
            let publishArtifact = agentContext.PublishArtifact

            let confirmPolicy context (result: PolicyResult) =
                task {
                    if not result.RequiresConfirmation then
                        return None
                    else
                        let request =
                            { Authorization = request.Authorization
                              Correlation = request.Correlation
                              Context = context
                              Violations =
                                result.Violations
                                |> List.filter (fun violation ->
                                    match violation.Enforcement with
                                    | PolicyEnforcement.Confirm -> true
                                    | _ -> false)
                              EffectiveInput = result.ModifiedInput |> Option.orElse context.Input
                              RequestedAt = DateTimeOffset.UtcNow }

                        let! confirmation =
                            PolicyConfirmationBroker.resolve
                                config.PolicyConfirmationBroker
                                request
                                execCtx.CancellationToken

                        return Some confirmation
                }

            let evaluatePolicyStage stage action toolName facts input =
                task {
                    match config.PolicyEngine, config.AuditLog with
                    | None, _ -> return Ok input
                    | Some _, None -> return Error [ "Policy evaluation requires an authenticated audit log." ]
                    | Some engine, Some audit ->
                        let context =
                            PolicyContext.FromExecutionContext
                                request.Authorization
                                agent.Metadata.Id
                                stage
                                action
                                toolName
                                (Some input)
                                facts
                                execCtx

                        let! result = engine.EvaluateAsync(context, execCtx.CancellationToken)
                        policyViolations <- policyViolations @ result.Violations

                        let! confirmation =
                            if result.Proceed then
                                confirmPolicy context result
                            else
                                Task.FromResult None

                        let evaluations =
                            result.Evaluations
                            |> List.map (fun evidence ->
                                if evidence.Outcome = PolicyEvaluationOutcome.ConfirmationRequired then
                                    { evidence with
                                        Confirmation = confirmation }
                                else
                                    evidence)

                        policyEvaluations <- policyEvaluations @ evaluations

                        for evidence in evaluations do
                            do!
                                audit.RecordAsync
                                    request.Authorization
                                    (AuditLog.policyEvaluation evidence (Some execCtx.ExecutionId))

                        if not result.Proceed then
                            return Error(result.Violations |> List.map _.Message)
                        elif confirmation = Some PolicyConfirmation.Denied then
                            return Error [ "Policy confirmation was denied." ]
                        else
                            return Ok(result.ModifiedInput |> Option.defaultValue input)
                }

            let executeChild childContext (child: Agent) (childInput: string) =
                task {
                    let childCorrelation = CorrelationContext.delegateFrom request.Correlation

                    let childRequest =
                        let childAuthorization =
                            AuthorizationScope.delegateAgent
                                agent.Metadata.Id
                                child.Metadata.Id
                                childCorrelation.ExecutionId
                                request.Authorization

                        ExecutionRequest.create
                            childAuthorization
                            request.TurnId
                            request.ConversationId
                            child.Metadata.Id
                            childInput
                            request.Sandbox
                            request.PolicyVersions
                            request.DependencyVersions
                            childCorrelation
                        |> ExecutionRequest.withPolicyFacts request.PolicyFacts

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
                                    request.Authorization
                                    { Id = Guid.NewGuid()
                                      IdempotencyKey = record.IdempotencyKey
                                      Correlation = record.Correlation
                                      TurnId = record.TurnId
                                      ToolName = record.ToolName
                                      Input = record.Input
                                      Phase = phase
                                      Detail = detail
                                      RecordedAt = DateTimeOffset.UtcNow }

                            match result with
                            | Ok() -> do! journal.MarkRevertedAsync request.Authorization record.Id
                            | Error _ -> ()
                }

            let executeTool
                (toolContext: AgentContext)
                (idempotencyKey: IdempotencyKey)
                (tool: Tool)
                (toolInput: string)
                =
                task {
                    let toolContext =
                        { toolContext with
                            Authorization = Some request.Authorization }

                    match!
                        evaluatePolicyStage
                            PolicyStage.PreAction
                            (sprintf "tool.execute:%s" tool.Name)
                            (Some tool.Name)
                            (PolicyFacts.combine request.PolicyFacts (PolicyFacts.ofResourceAccess tool.Permissions))
                            toolInput
                    with
                    | Error messages ->
                        return
                            Error(
                                PlatformFailure.create
                                    PlatformErrorCategory.PermissionDenied
                                    (sprintf "Blocked by policy: %s" (String.concat "; " messages))
                                    false
                                    (request.Correlation.ExecutionId |> ExecutionId.serialize |> Some)
                            )
                    | Ok actionInput ->
                        let! sideEffectPolicy =
                            if
                                tool.ReplaySafety = ToolReplaySafety.ExternallySideEffecting
                                || Tool.canRevert tool
                            then
                                evaluatePolicyStage
                                    PolicyStage.PreSideEffect
                                    (sprintf "tool.side-effect:%s" tool.Name)
                                    (Some tool.Name)
                                    (PolicyFacts.combine
                                        request.PolicyFacts
                                        (PolicyFacts.ofResourceAccess tool.Permissions))
                                    actionInput
                            else
                                Task.FromResult(Ok actionInput)

                        match sideEffectPolicy with
                        | Error messages ->
                            return
                                Error(
                                    PlatformFailure.create
                                        PlatformErrorCategory.PermissionDenied
                                        (sprintf "Blocked by policy: %s" (String.concat "; " messages))
                                        false
                                        (request.Correlation.ExecutionId |> ExecutionId.serialize |> Some)
                                )
                        | Ok effectiveToolInput ->
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
                                        if
                                            tool.ReplaySafety = ToolReplaySafety.RetrySafe && not (Tool.canRevert tool)
                                        then
                                            Task.CompletedTask
                                        else
                                            match journal with
                                            | Some value ->
                                                value.SideEffects.Save
                                                    request.Authorization
                                                    { Id = Guid.NewGuid()
                                                      IdempotencyKey = idempotencyKey
                                                      Correlation = request.Correlation
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

                                                do! value.RecordAsync request.Authorization record

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
                                                        request.Authorization
                                                        { Id = Guid.NewGuid()
                                                          IdempotencyKey = idempotencyKey
                                                          Correlation = request.Correlation
                                                          TurnId = toolContext.TurnId
                                                          ToolName = tool.Name
                                                          ReplaySafety = tool.ReplaySafety
                                                          Input = effectiveToolInput
                                                          Output = ""
                                                          Failure = Some failure
                                                          ExecutedAt = DateTimeOffset.UtcNow
                                                          Reverted = false
                                                          Metadata =
                                                            Map.ofList
                                                                [ "duration.ms", string started.ElapsedMilliseconds ] }
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
                                                    (Some
                                                        "Execution was interrupted after side-effect intent was recorded.")

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
                    PublishArtifact = publishCapturedArtifact
                    RequestModelEligibility =
                        fun model ->
                            task {
                                let modelFacts =
                                    PolicyFacts.create Seq.empty Seq.empty [ model ]
                                    |> PolicyFacts.combine request.PolicyFacts

                                match!
                                    evaluatePolicyStage
                                        PolicyStage.PreAction
                                        (sprintf "model.invoke:%s" model)
                                        None
                                        modelFacts
                                        ""
                                with
                                | Ok _ -> return true
                                | Error _ -> return false
                            }
                    Authorization = Some request.Authorization }

            let dispatcher =
                { RunAgent = executeChild
                  RunTool = executeTool }

            let mutable effectiveInput = input

            // === G: Identity and policy pre-checks ===
            let! preflightError =
                task {
                    if request.AgentId <> agent.Metadata.Id then
                        return Some HarnessError.PermissionDenied
                    elif config.Constitution.IsSome && config.AuditLog.IsNone then
                        return
                            Some(
                                HarnessError.InitializationFailed
                                    "Constitution evaluation requires an authenticated audit log."
                            )
                    else
                        match! evaluatePolicyStage PolicyStage.PreInput "execute" None request.PolicyFacts input with
                        | Ok modifiedInput ->
                            effectiveInput <- modifiedInput
                            return None
                        | Error messages -> return Some(HarnessError.PolicyBlocked messages)
                }

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
                        policyEvaluations
                        []
                        request.Authorization
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
                            policyEvaluations
                            []
                            request.Authorization
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
                                policyEvaluations
                                []
                                request.Authorization
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
                                let s = t.StartTrace request.Authorization execCtx.Correlation TelemetrySpan.Harness

                                t.SetAttributes
                                    request.Authorization
                                    s
                                    (TelemetryAttributes.execution
                                        request.Authorization
                                        request.TurnId
                                        request.AgentId
                                        execCtx.Correlation)

                                s)

                        let endRootSpan terminalState errorCategory status =
                            match rootSpan, config.Tracer with
                            | Some span, Some tracer ->
                                tracer.SetAttributes
                                    request.Authorization
                                    span
                                    (Map.ofList
                                        [ TelemetryAttribute.TerminalState, TelemetryTerminalState.value terminalState
                                          TelemetryAttribute.ErrorCategory, TelemetryErrorCategory.value errorCategory ])

                                tracer.EndSpan request.Authorization span status
                            | _ -> ()

                        // === T: Tool Protocol — Record available tools in span ===
                        match config.ToolProtocol, rootSpan, config.Tracer with
                        | Some protocol, Some span, Some tracer ->
                            let! tools = protocol.ListTools()

                            tracer.SetAttributes
                                request.Authorization
                                span
                                (Map.ofList [ "nao.tool.available_count", string tools.Length ])
                        | _ -> ()

                        // === E: Execution — Run agent within sandbox ===
                        let sw = Stopwatch.StartNew()

                        let execSpan =
                            match rootSpan, config.Tracer with
                            | Some parent, Some tracer ->
                                let s = tracer.StartSpan request.Authorization parent TelemetrySpan.HarnessStage

                                tracer.SetAttributes
                                    request.Authorization
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

                        let previousInstrumentation, instrumentation =
                            ExecutionInstrumentation.install
                                request.Authorization
                                execCtx.Correlation
                                config.Tracer
                                execSpan

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
                                                config.ContainerEnvironment
                                                |> Option.defaultWith (fun () ->
                                                    raise (
                                                        GovernedExecutionException
                                                            "Container isolation requires a configured container execution environment."
                                                    ))

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
                                    ExecutionInstrumentation.restore previousInstrumentation
                            }

                        sw.Stop()

                        for observation in ExecutionInstrumentation.embeddingCalls instrumentation do
                            let durationMs =
                                max 0L (int64 (observation.CompletedAt - observation.StartedAt).TotalMilliseconds)

                            trace <-
                                trace
                                |> Verification.addStep
                                    (TraceAction.EmbeddingCall observation.ProfileId)
                                    $"batch={observation.BatchId};inputs={observation.InputCount}"
                                    $"tokens={observation.InputTokens};attempts={observation.ProviderAttempts};cost_usd={observation.EstimatedCostUsd}"
                                    durationMs

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

                        let! executionOutcome =
                            match executionOutcome with
                            | Ok(Ok response) ->
                                task {
                                    match!
                                        evaluatePolicyStage
                                            PolicyStage.PostOutput
                                            "response.output"
                                            None
                                            request.PolicyFacts
                                            response
                                    with
                                    | Error messages ->
                                        return
                                            Error(ExecutionTerminalStatus.Denied(HarnessError.PolicyBlocked messages))
                                    | Ok output ->
                                        match!
                                            evaluatePolicyStage
                                                PolicyStage.PreCommit
                                                "response.commit"
                                                None
                                                request.PolicyFacts
                                                output
                                        with
                                        | Error messages ->
                                            return
                                                Error(
                                                    ExecutionTerminalStatus.Denied(HarnessError.PolicyBlocked messages)
                                                )
                                        | Ok committedOutput -> return Ok(Ok committedOutput)
                                }
                            | outcome -> Task.FromResult outcome

                        // === O: End execution span ===
                        match execSpan, config.Tracer with
                        | Some s, Some tracer ->
                            match executionOutcome with
                            | Ok(Ok _) -> tracer.EndSpan request.Authorization s SpanStatus.Ok
                            | Ok(Error limit) ->
                                tracer.EndSpan
                                    request.Authorization
                                    s
                                    (SpanStatus.Error TelemetryErrorCategory.ResourceLimit)
                            | Error status ->
                                let failure =
                                    status.ToPlatformFailure(
                                        request.Correlation.ExecutionId |> ExecutionId.serialize |> Some
                                    )

                                tracer.EndSpan
                                    request.Authorization
                                    s
                                    (SpanStatus.Error(TelemetryErrorCategory.ofPlatform failure.Category))
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
                            | Some store -> do! store.SaveAsync request.Authorization trace
                            | None -> ()

                            let errorCategory = TelemetryErrorCategory.ofPlatform failure.Category

                            endRootSpan
                                (match status with
                                 | ExecutionTerminalStatus.Cancelled -> TelemetryTerminalState.Cancelled
                                 | ExecutionTerminalStatus.TimedOut -> TelemetryTerminalState.TimedOut
                                 | ExecutionTerminalStatus.Denied _ -> TelemetryTerminalState.Denied
                                 | ExecutionTerminalStatus.LimitExceeded _ -> TelemetryTerminalState.LimitExceeded
                                 | ExecutionTerminalStatus.Indeterminate _ -> TelemetryTerminalState.Indeterminate
                                 | ExecutionTerminalStatus.Failed _ -> TelemetryTerminalState.Failed
                                 | ExecutionTerminalStatus.Succeeded -> TelemetryTerminalState.Succeeded)
                                errorCategory
                                (SpanStatus.Error errorCategory)

                            return
                                stoppedResult
                                    status
                                    request.Correlation
                                    (List.ofSeq artifacts)
                                    execCtx.Usage
                                    trace
                                    policyViolations
                                    policyEvaluations
                                    request.Authorization
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
                            | Some store -> do! store.SaveAsync request.Authorization trace
                            | None -> ()
                            // End root span on failure
                            endRootSpan
                                TelemetryTerminalState.LimitExceeded
                                TelemetryErrorCategory.ResourceLimit
                                (SpanStatus.Error TelemetryErrorCategory.ResourceLimit)

                            return
                                failResult
                                    (HarnessError.ResourceLimitExceeded limitExceeded)
                                    request.Correlation
                                    (List.ofSeq artifacts)
                                    execCtx.Usage
                                    trace
                                    policyViolations
                                    policyEvaluations
                                    []
                                    request.Authorization
                                    config.Metrics
                                    0

                        | Ok(Ok response) ->
                            // === G: Constitution — Check output ===
                            let evaluateConstitution output =
                                task {
                                    match config.Constitution, config.AuditLog with
                                    | Some constitution, Some audit ->
                                        let context =
                                            ConstitutionContext.create request.Authorization agent.Metadata.Id output

                                        let! result =
                                            Constitution.evaluateAsync constitution context execCtx.CancellationToken

                                        for evidence in result.Evaluations do
                                            do!
                                                audit.RecordAsync
                                                    request.Authorization
                                                    (AuditLog.constitutionEvaluation
                                                        agent.Metadata.Id
                                                        evidence
                                                        (Some execCtx.ExecutionId))

                                        return Some result
                                    | None, _ -> return None
                                    | Some _, None ->
                                        return
                                            invalidOp
                                                "Constitution audit capability disappeared after preflight validation."
                                }

                            let! initialCheck = evaluateConstitution response
                            let mutable governedResponse = response

                            let! shouldRelease =
                                task {
                                    match initialCheck with
                                    | None -> return true
                                    | Some checkResult ->
                                        constitutionViolations <- checkResult.Violations

                                        let remediationRequest =
                                            { Authorization = request.Authorization
                                              ExecutionId = execCtx.ExecutionId
                                              AgentId = agent.Metadata.Id
                                              Output = response
                                              Violations = checkResult.Violations
                                              RequestedAt = DateTimeOffset.UtcNow }

                                        match Constitution.requiredRemediation checkResult.Violations with
                                        | ConstitutionRemediation.Observe ->
                                            constitutionDisposition <- ConstitutionDisposition.Released
                                            return true
                                        | ConstitutionRemediation.Redact
                                        | ConstitutionRemediation.Repair as remediation ->
                                            let! transformed =
                                                match remediation with
                                                | ConstitutionRemediation.Redact ->
                                                    ConstitutionRemediationBroker.redactAsync
                                                        remediationRequest
                                                        execCtx.CancellationToken
                                                        config.ConstitutionRemediationBroker
                                                | _ ->
                                                    ConstitutionRemediationBroker.repairAsync
                                                        remediationRequest
                                                        execCtx.CancellationToken
                                                        config.ConstitutionRemediationBroker

                                            let! governedTransformation =
                                                task {
                                                    match!
                                                        evaluatePolicyStage
                                                            PolicyStage.PostOutput
                                                            "response.output"
                                                            None
                                                            request.PolicyFacts
                                                            transformed
                                                    with
                                                    | Error _ -> return None
                                                    | Ok postOutput ->
                                                        match!
                                                            evaluatePolicyStage
                                                                PolicyStage.PreCommit
                                                                "response.commit"
                                                                None
                                                                request.PolicyFacts
                                                                postOutput
                                                        with
                                                        | Error _ -> return None
                                                        | Ok preCommit -> return Some preCommit
                                                }

                                            match governedTransformation with
                                            | None ->
                                                constitutionDisposition <- ConstitutionDisposition.Rejected
                                                return false
                                            | Some transformedOutput ->
                                                let! transformedCheck = evaluateConstitution transformedOutput

                                                match transformedCheck with
                                                | Some result ->
                                                    constitutionViolations <- constitutionViolations @ result.Violations

                                                    if
                                                        Constitution.requiredRemediation result.Violations = ConstitutionRemediation.Observe
                                                    then
                                                        governedResponse <- transformedOutput

                                                        constitutionDisposition <-
                                                            if remediation = ConstitutionRemediation.Redact then
                                                                ConstitutionDisposition.Redacted
                                                            else
                                                                ConstitutionDisposition.Repaired

                                                        return true
                                                    else
                                                        constitutionDisposition <- ConstitutionDisposition.Rejected
                                                        return false
                                                | None ->
                                                    return
                                                        invalidOp
                                                            "Constitution configuration disappeared during remediation."
                                        | ConstitutionRemediation.Reject ->
                                            constitutionDisposition <- ConstitutionDisposition.Rejected
                                            return false
                                        | ConstitutionRemediation.Escalate ->
                                            do!
                                                ConstitutionRemediationBroker.escalateAsync
                                                    remediationRequest
                                                    execCtx.CancellationToken
                                                    config.ConstitutionRemediationBroker

                                            constitutionDisposition <- ConstitutionDisposition.Escalated
                                            return false
                                        | ConstitutionRemediation.Quarantine ->
                                            do!
                                                ConstitutionRemediationBroker.quarantineAsync
                                                    remediationRequest
                                                    execCtx.CancellationToken
                                                    config.ConstitutionRemediationBroker

                                            constitutionDisposition <- ConstitutionDisposition.Quarantined
                                            return false
                                }

                            if not shouldRelease then
                                do! compensateReversibleExecutions ()

                                let! _ =
                                    AgentLifecycle.failAsync agent.Metadata.Id (exn "Constitution violation") _startedLc

                                endRootSpan
                                    TelemetryTerminalState.Denied
                                    TelemetryErrorCategory.Authorization
                                    (SpanStatus.Error TelemetryErrorCategory.Authorization)

                                let violationIds = constitutionViolations |> List.map _.RuleId

                                let failed =
                                    failResult
                                        (HarnessError.ConstitutionViolation violationIds)
                                        request.Correlation
                                        (List.ofSeq artifacts)
                                        execCtx.Usage
                                        trace
                                        policyViolations
                                        policyEvaluations
                                        constitutionViolations
                                        request.Authorization
                                        config.Metrics
                                        1

                                return
                                    { failed with
                                        PolicyDecisions =
                                            { failed.PolicyDecisions with
                                                ConstitutionDisposition = constitutionDisposition } }
                            else
                                let response = governedResponse

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
                                            let! baseline =
                                                store.GetBaselineAsync request.Authorization agent.Metadata.Id input

                                            match baseline with
                                            | Some b -> return Some(Regression.detect b trace)
                                            | None -> return None
                                        }
                                    | None -> Task.FromResult None

                                // === V: Save trace ===
                                match config.TraceStore with
                                | Some store -> do! store.SaveAsync request.Authorization trace
                                | None -> ()

                                // === G: Audit ===
                                match config.AuditLog with
                                | Some audit ->
                                    do!
                                        audit.RecordAsync
                                            request.Authorization
                                            (AuditLog.llmCall agent.Metadata.Id "unknown" (Some execCtx.ExecutionId))
                                | None -> ()

                                // === O: End root span ===
                                match rootSpan, config.Tracer with
                                | Some s, Some tracer ->
                                    tracer.AddEvent
                                        request.Authorization
                                        s
                                        "harness.complete"
                                        (Map.ofList [ "response.length", string response.Length ])
                                | _ -> ()

                                endRootSpan TelemetryTerminalState.Succeeded TelemetryErrorCategory.None SpanStatus.Ok

                                EventBus.publishAsync
                                    (NaoEvent.TurnProgress(scope, ProgressSignal.AnswerProduced response))
                                    config.Bus
                                |> ignore

                                let metrics =
                                    config.Metrics
                                    |> Option.map (fun value -> value.GetMetrics request.Authorization)

                                let decisions: ExecutionPolicyDecisions =
                                    { PolicyViolations = policyViolations
                                      PolicyEvaluations = policyEvaluations
                                      ConstitutionViolations = constitutionViolations
                                      ConstitutionDisposition = constitutionDisposition }

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
        SecretRedaction.runAsync (fun () ->
            task {
                let! decision = resumeDecision config request

                match decision with
                | ResumeDecision.Stop message ->
                    let result =
                        indeterminateResult config agent request message |> ExecutionResult.redactResult

                    match config.ExecutionJournal with
                    | Some journal ->
                        let! existing =
                            journal.Outcomes.TryGetByIdempotencyKey request.Authorization request.IdempotencyKey

                        if existing.IsNone then
                            do! saveTerminalOutcome config request result
                    | None -> ()

                    do! saveCheckpoint config request HarnessCheckpointPhase.Indeterminate
                    return result
                | ResumeDecision.Execute ->
                    do! saveCheckpoint config request HarnessCheckpointPhase.Accepted

                    use deadline =
                        new System.Threading.CancellationTokenSource(request.Sandbox.Limits.MaxDuration)

                    use execution =
                        System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            deadline.Token
                        )

                    let! rawResult =
                        runAgentAsync config agentContext agent request None execution.Token cancellationToken

                    let result = ExecutionResult.redactResult rawResult
                    do! saveTerminalOutcome config request result
                    do! saveCheckpoint config request (checkpointPhase result.Status)
                    return result
            })
