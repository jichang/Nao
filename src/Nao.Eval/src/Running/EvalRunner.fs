namespace Nao.Eval

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Nao.Agents

/// Host-owned execution dependencies applied to every evaluated case.
type EvalExecutionConfig =
    { Authorization: AuthorizationScope
      CreateAgentContext: unit -> AgentContext
      Harness: EtclovgConfig
      Sandbox: SandboxConfig
      Specification: EvalExecutionSpecification
      Mode: EvalExecutionMode
      Runtime: EvalRuntime
      PolicyVersions: Map<string, string>
      DependencyVersions: Map<string, string> }

[<RequireQualifiedAccess>]
module EvalExecutionConfig =

    let create authorization createAgentContext harness sandbox specification mode runtime =
        match mode with
        | EvalExecutionMode.PinnedLive -> EvalExecutionSpecification.requirePinnedLiveDependencies specification
        | _ -> ()

        match mode, harness.ReplayFixtures with
        | EvalExecutionMode.RecordedReplay _, None
        | EvalExecutionMode.PinnedLive, None
        | EvalExecutionMode.PinnedMock, None -> ()
        | EvalExecutionMode.RecordedReplay _, Some _ ->
            invalidArg (nameof harness) "Recorded evaluation fixtures are owned by the execution mode."
        | _, Some _ -> invalidArg (nameof harness) "Live and mock evaluation modes cannot install replay fixtures."

        { Authorization = authorization
          CreateAgentContext = createAgentContext
          Harness = harness
          Sandbox = sandbox
          Specification = specification
          Mode = mode
          Runtime = runtime
          PolicyVersions = Map.empty
          DependencyVersions = Map.empty }

/// Configuration for the evaluation runner
type EvalRunnerConfig =
    {
        Execution: EvalExecutionConfig
        /// Maximum parallelism for running eval cases
        MaxParallelism: int
        /// Optional timeout per case in ms
        TimeoutPerCaseMs: int option
        /// Whether to stop on first failure
        StopOnFirstFailure: bool
        /// Whether to capture execution traces for regression analysis
        CaptureTraces: bool
    }

[<RequireQualifiedAccess>]
module EvalRunnerConfig =

    let create execution =
        { Execution = execution
          MaxParallelism = 1
          TimeoutPerCaseMs = None
          StopOnFirstFailure = false
          CaptureTraces = false }

    let withParallelism maxParallelism execution =
        { create execution with
            MaxParallelism = maxParallelism }

    let withTracing config = { config with CaptureTraces = true }

/// The evaluation runner: runs cases against an agent and scores them
module EvalRunner =

    let private harness execution =
        match execution.Mode with
        | EvalExecutionMode.RecordedReplay fixtures ->
            { execution.Harness with
                ReplayFixtures = Some(EvalRecordedFixtureSet.toReplayFixtures fixtures) }
        | _ -> execution.Harness

    let private sandboxForCase timeoutPerCaseMs (sandbox: SandboxConfig) =
        match timeoutPerCaseMs with
        | None -> sandbox
        | Some timeoutMs when timeoutMs <= 0 -> invalidArg (nameof timeoutPerCaseMs) "Case timeout must be positive."
        | Some timeoutMs ->
            let timeout = TimeSpan.FromMilliseconds(float timeoutMs)

            { sandbox with
                Limits =
                    { sandbox.Limits with
                        MaxDuration = min sandbox.Limits.MaxDuration timeout } }

    let private runCase
        captureTrace
        timeoutPerCaseMs
        (execution: EvalExecutionConfig)
        (run: EvalRun)
        (evaluator: Evaluator)
        (agent: Agent)
        (case: EvalCase)
        caseOrdinal
        evaluatorOrdinal
        (cancellationToken: CancellationToken)
        : Task<EvalResult> =
        task {
            AuthorizationScope.requireOwner run.Owner execution.Authorization
            let startedAt = EvalRuntime.utcNow execution.Runtime

            let purpose =
                sprintf "case|%s|%d|%d" (EvalCase.id case) caseOrdinal evaluatorOrdinal

            let context =
                { execution.CreateAgentContext() with
                    Correlation = EvalRuntime.correlation purpose execution.Runtime }

            let request =
                ExecutionRequest.create
                    execution.Authorization
                    (execution.Runtime
                     |> EvalRuntime.guid (purpose + "|turn")
                     |> _.ToString("N")
                     |> TurnId.parse)
                    (run.Id.ToString("N"))
                    agent.Metadata.Id
                    (EvalCase.input case)
                    (sandboxForCase timeoutPerCaseMs execution.Sandbox)
                    execution.PolicyVersions
                    execution.DependencyVersions
                    context.Correlation

            let! executionResult = EtclovgHarness.runAsync (harness execution) context agent request cancellationToken
            let completedAt = EvalRuntime.utcNow execution.Runtime

            let! output, verdict, reason, outcome =
                task {
                    match executionResult.Status, executionResult.Outputs.Response with
                    | ExecutionTerminalStatus.Succeeded, Some output ->
                        let evaluationContext =
                            { Authorization = execution.Authorization
                              Correlation = executionResult.Correlation
                              Execution = executionResult
                              CancellationToken = cancellationToken }

                        let! verdict, reason =
                            evaluator.EvaluateAsync evaluationContext case output
                            |> _.WaitAsync(cancellationToken)

                        let outcome =
                            if verdict = EvalVerdict.Fail then
                                EvalResultOutcome.CorrectnessFailure
                            else
                                EvalResultOutcome.Succeeded

                        return output, verdict, reason, outcome
                    | ExecutionTerminalStatus.Succeeded, None ->
                        return
                            "",
                            EvalVerdict.Fail,
                            "Execution succeeded without producing a response.",
                            EvalResultOutcome.InfrastructureFailure
                    | (ExecutionTerminalStatus.Denied _ | ExecutionTerminalStatus.LimitExceeded _) as status, _ ->
                        let correlationId =
                            executionResult.Correlation.ExecutionId |> ExecutionId.serialize |> Some

                        let failure = status.ToPlatformFailure correlationId
                        return "", EvalVerdict.Fail, failure.Message, EvalResultOutcome.CorrectnessFailure
                    | status, _ ->
                        let correlationId =
                            executionResult.Correlation.ExecutionId |> ExecutionId.serialize |> Some

                        let failure = status.ToPlatformFailure correlationId
                        return "", EvalVerdict.Fail, failure.Message, EvalResultOutcome.InfrastructureFailure
                }

            let trace =
                if captureTrace then
                    executionResult.Evidence.Trace
                else
                    None

            return
                ({ Id = EvalRuntime.guid (purpose + "|result") execution.Runtime
                   Owner = run.Owner
                   DatasetId = run.DatasetId
                   RunId = run.Id
                   ExecutionId = executionResult.Correlation.ExecutionId
                   CaseId = EvalCase.id case
                   ActualOutput = output
                   Verdict = verdict
                   Reason = reason
                   LatencyMs = max 0L (int64 (completedAt - startedAt).TotalMilliseconds)
                   EvaluatorName = Evaluator.name evaluator
                   EvaluatorAuthority = evaluator.Authority
                   RationaleIsUntrusted = evaluator.Authority = EvaluatorAuthority.Probabilistic
                   Outcome = outcome
                   Timestamp = completedAt
                   ExecutionTrace = trace }
                : EvalResult)
        }

    /// Run a single eval case against an agent with a given evaluator.
    let runCaseAsync execution run evaluator agent case cancellationToken =
        runCase true None execution run evaluator agent case 0 0 cancellationToken

    /// Run a single eval case without trace capture (lightweight)
    let runCaseLightAsync execution run evaluator agent case cancellationToken =
        runCase false None execution run evaluator agent case 0 0 cancellationToken

    /// Run all cases in a dataset against an agent
    let runDatasetAsync
        (config: EvalRunnerConfig)
        (evaluator: Evaluator)
        (agent: Agent)
        (dataset: EvalDataset)
        (cancellationToken: CancellationToken)
        : Task<EvalReport> =
        task {
            AuthorizationScope.requireOwner (EvalDataset.owner dataset) config.Execution.Authorization

            if config.MaxParallelism > 1 && config.StopOnFirstFailure then
                invalidArg
                    (nameof config)
                    "StopOnFirstFailure requires sequential execution because parallel cases may already be running."

            let results = ResizeArray<EvalResult>()

            let configuration =
                EvalExecutionSpecification.resolve
                    agent.Metadata.Id
                    [ evaluator.Identity ]
                    config.Execution.Specification

            let reproducibility =
                EvalReproducibility.create
                    config.Execution.Mode
                    config.Execution.Runtime
                    { MaximumParallelism = config.MaxParallelism
                      StopOnFirstFailure = config.StopOnFirstFailure }

            let run =
                EvalRun.create
                    (EvalDataset.owner dataset)
                    (EvalDataset.id dataset)
                    (EvalDataset.revision dataset)
                    (EvalDataset.contentSha256 dataset)
                    configuration
                    reproducibility
                    config.Execution.Runtime

            let runCase =
                if config.CaptureTraces then
                    runCase true config.TimeoutPerCaseMs config.Execution run
                else
                    runCase false config.TimeoutPerCaseMs config.Execution run

            if config.MaxParallelism <= 1 then
                let mutable continueRunning = true

                for caseOrdinal, case in EvalDataset.cases dataset |> List.indexed do
                    if continueRunning then
                        let! result = runCase evaluator agent case caseOrdinal 0 cancellationToken
                        results.Add result

                        let shouldStop =
                            cancellationToken.IsCancellationRequested
                            || (config.StopOnFirstFailure && not (EvalResult.passed result))

                        continueRunning <- not shouldStop
            else
                // Parallel execution with bounded concurrency
                let semaphore = new System.Threading.SemaphoreSlim(config.MaxParallelism)

                let tasks =
                    EvalDataset.cases dataset
                    |> List.mapi (fun caseOrdinal case ->
                        task {
                            do! semaphore.WaitAsync(cancellationToken)

                            try
                                let! result = runCase evaluator agent case caseOrdinal 0 cancellationToken
                                lock results (fun () -> results.Add result)
                            finally
                                semaphore.Release() |> ignore
                        })

                do! Task.WhenAll(tasks) :> Task

            return
                EvalReport.fromCasesAndResults
                    run
                    (EvalDataset.name dataset)
                    (EvalDataset.cases dataset)
                    (results |> Seq.toList)
        }

    /// Run cases with multiple evaluators and combine results
    let runWithMultipleEvaluatorsAsync
        (config: EvalRunnerConfig)
        (evaluators: Evaluator list)
        (agent: Agent)
        (dataset: EvalDataset)
        (cancellationToken: CancellationToken)
        : Task<EvalReport> =
        task {
            AuthorizationScope.requireOwner (EvalDataset.owner dataset) config.Execution.Authorization
            let results = ResizeArray<EvalResult>()

            let configuration =
                EvalExecutionSpecification.resolve
                    agent.Metadata.Id
                    (evaluators |> List.map _.Identity)
                    config.Execution.Specification

            let reproducibility =
                EvalReproducibility.create
                    config.Execution.Mode
                    config.Execution.Runtime
                    { MaximumParallelism = config.MaxParallelism
                      StopOnFirstFailure = config.StopOnFirstFailure }

            let run =
                EvalRun.create
                    (EvalDataset.owner dataset)
                    (EvalDataset.id dataset)
                    (EvalDataset.revision dataset)
                    (EvalDataset.contentSha256 dataset)
                    configuration
                    reproducibility
                    config.Execution.Runtime

            let runCase =
                if config.CaptureTraces then
                    runCase true config.TimeoutPerCaseMs config.Execution run
                else
                    runCase false config.TimeoutPerCaseMs config.Execution run

            for caseOrdinal, case in EvalDataset.cases dataset |> List.indexed do
                for evaluatorOrdinal, evaluator in evaluators |> List.indexed do
                    if not cancellationToken.IsCancellationRequested then
                        let! result = runCase evaluator agent case caseOrdinal evaluatorOrdinal cancellationToken
                        results.Add result

            return
                EvalReport.fromCasesAndResults
                    run
                    (EvalDataset.name dataset)
                    (EvalDataset.cases dataset)
                    (results |> Seq.toList)
        }

    /// Compare two agents on the same dataset
    let compareAgentsAsync
        (config: EvalRunnerConfig)
        (evaluator: Evaluator)
        (agents: (string * Agent) list)
        (dataset: EvalDataset)
        (cancellationToken: CancellationToken)
        : Task<(string * EvalReport) list> =
        task {
            AuthorizationScope.requireOwner (EvalDataset.owner dataset) config.Execution.Authorization
            let mutable reports = []

            for (name, agent) in agents do
                cancellationToken.ThrowIfCancellationRequested()
                let! report = runDatasetAsync config evaluator agent dataset cancellationToken

                reports <-
                    reports
                    @ [ (name,
                         { report with
                             Name = sprintf "%s - %s" (EvalDataset.name dataset) name }) ]

            return reports
        }
