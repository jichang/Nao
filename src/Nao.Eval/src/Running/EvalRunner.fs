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
      PolicyVersions: Map<string, string>
      DependencyVersions: Map<string, string> }

[<RequireQualifiedAccess>]
module EvalExecutionConfig =

    let create authorization createAgentContext =
        { Authorization = authorization
          CreateAgentContext = createAgentContext
          Harness = EtclovgConfig.Default
          Sandbox = SandboxConfig.Default
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
        (cancellationToken: CancellationToken)
        : Task<EvalResult> =
        task {
            let sw = Stopwatch.StartNew()
            let context = execution.CreateAgentContext()

            let request =
                ExecutionRequest.create
                    execution.Authorization
                    (TurnId.generate ())
                    (run.Id.ToString("N"))
                    agent.Metadata.Id
                    case.Input
                    (sandboxForCase timeoutPerCaseMs execution.Sandbox)
                    execution.PolicyVersions
                    execution.DependencyVersions
                    context.Correlation

            let! executionResult = EtclovgHarness.runAsync execution.Harness context agent request cancellationToken

            sw.Stop()

            let! output, verdict, reason =
                task {
                    match executionResult.Status, executionResult.Outputs.Response with
                    | ExecutionTerminalStatus.Succeeded, Some output ->
                        let! verdict, reason =
                            evaluator.EvaluateAsync executionResult.Correlation case output
                            |> _.WaitAsync(cancellationToken)

                        return output, verdict, reason
                    | ExecutionTerminalStatus.Succeeded, None ->
                        return "", EvalVerdict.Fail, "Execution succeeded without producing a response."
                    | status, _ ->
                        let correlationId =
                            executionResult.Correlation.ExecutionId |> ExecutionId.serialize |> Some

                        let failure = status.ToPlatformFailure correlationId
                        return "", EvalVerdict.Fail, failure.Message
                }

            let trace =
                if captureTrace then
                    executionResult.Evidence.Trace
                else
                    None

            return
                ({ Id = Guid.NewGuid()
                   Owner = run.Owner
                   DatasetId = run.DatasetId
                   RunId = run.Id
                   ExecutionId = executionResult.Correlation.ExecutionId
                   CaseId = case.Id
                   ActualOutput = output
                   Verdict = verdict
                   Reason = reason
                   LatencyMs = sw.ElapsedMilliseconds
                   EvaluatorName = evaluator.Name
                   Timestamp = DateTimeOffset.UtcNow
                   ExecutionTrace = trace }
                : EvalResult)
        }

    /// Run a single eval case against an agent with a given evaluator.
    let runCaseAsync execution run evaluator agent case cancellationToken =
        runCase true None execution run evaluator agent case cancellationToken

    /// Run a single eval case without trace capture (lightweight)
    let runCaseLightAsync execution run evaluator agent case cancellationToken =
        runCase false None execution run evaluator agent case cancellationToken

    /// Run all cases in a dataset against an agent
    let runDatasetAsync
        (config: EvalRunnerConfig)
        (evaluator: Evaluator)
        (agent: Agent)
        (dataset: EvalDataset)
        (cancellationToken: CancellationToken)
        : Task<EvalReport> =
        task {
            if config.MaxParallelism > 1 && config.StopOnFirstFailure then
                invalidArg
                    (nameof config)
                    "StopOnFirstFailure requires sequential execution because parallel cases may already be running."

            let results = ResizeArray<EvalResult>()
            let run = EvalRun.create dataset.Owner dataset.Id

            let runCase =
                if config.CaptureTraces then
                    runCase true config.TimeoutPerCaseMs config.Execution run
                else
                    runCase false config.TimeoutPerCaseMs config.Execution run

            if config.MaxParallelism <= 1 then
                let mutable continueRunning = true

                for case in dataset.Cases do
                    if continueRunning then
                        let! result = runCase evaluator agent case cancellationToken
                        results.Add result

                        let shouldStop =
                            cancellationToken.IsCancellationRequested
                            || (config.StopOnFirstFailure && not (EvalResult.passed result))

                        continueRunning <- not shouldStop
            else
                // Parallel execution with bounded concurrency
                let semaphore = new System.Threading.SemaphoreSlim(config.MaxParallelism)

                let tasks =
                    dataset.Cases
                    |> List.map (fun case ->
                        task {
                            do! semaphore.WaitAsync(cancellationToken)

                            try
                                let! result = runCase evaluator agent case cancellationToken
                                lock results (fun () -> results.Add result)
                            finally
                                semaphore.Release() |> ignore
                        })

                do! Task.WhenAll(tasks) :> Task

            return EvalReport.fromCasesAndResults run dataset.Name dataset.Cases (results |> Seq.toList)
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
            let results = ResizeArray<EvalResult>()
            let run = EvalRun.create dataset.Owner dataset.Id

            let runCase =
                if config.CaptureTraces then
                    runCase true config.TimeoutPerCaseMs config.Execution run
                else
                    runCase false config.TimeoutPerCaseMs config.Execution run

            for case in dataset.Cases do
                for evaluator in evaluators do
                    if not cancellationToken.IsCancellationRequested then
                        let! result = runCase evaluator agent case cancellationToken
                        results.Add result

            return EvalReport.fromCasesAndResults run dataset.Name dataset.Cases (results |> Seq.toList)
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
            let mutable reports = []

            for (name, agent) in agents do
                cancellationToken.ThrowIfCancellationRequested()
                let! report = runDatasetAsync config evaluator agent dataset cancellationToken

                reports <-
                    reports
                    @ [ (name,
                         { report with
                             Name = sprintf "%s - %s" dataset.Name name }) ]

            return reports
        }
