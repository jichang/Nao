namespace Nao.Eval

open System
open System.Diagnostics
open System.Threading.Tasks
open Nao.Agents

/// Configuration for the evaluation runner
type EvalRunnerConfig =
    { /// Maximum parallelism for running eval cases
      MaxParallelism: int
      /// Optional timeout per case in ms
      TimeoutPerCaseMs: int option
      /// Whether to stop on first failure
      StopOnFirstFailure: bool
      /// Whether to capture execution traces for regression analysis
      CaptureTraces: bool }

    static member Default =
        { MaxParallelism = 1
          TimeoutPerCaseMs = None
          StopOnFirstFailure = false
          CaptureTraces = false }

    static member Parallel n =
        { EvalRunnerConfig.Default with MaxParallelism = n }

    static member WithTracing =
        { EvalRunnerConfig.Default with CaptureTraces = true }

/// The evaluation runner: runs cases against an agent and scores them
module EvalRunner =

    /// Run a single eval case against an agent with a given evaluator
    let runCaseAsync (evaluator: IEvaluator) (agent: IAgent) (case: EvalCase) : Task<EvalResult> =
        task {
            let sw = Stopwatch.StartNew()
            let! output = agent.RunAsync case.Input
            sw.Stop()

            let! (verdict, reason) = evaluator.EvaluateAsync case output

            // Capture execution trace for the agent call
            let trace =
                Verification.startTrace agent.Id case.Input
                |> Verification.addStep (TraceAction.LlmCall "unknown") case.Input output sw.ElapsedMilliseconds
                |> Verification.complete output

            return
                { CaseId = case.Id
                  ActualOutput = output
                  Verdict = verdict
                  Reason = reason
                  LatencyMs = sw.ElapsedMilliseconds
                  EvaluatorName = evaluator.Name
                  Timestamp = DateTimeOffset.UtcNow
                  ExecutionTrace = Some trace }
        }

    /// Run a single eval case without trace capture (lightweight)
    let runCaseLightAsync (evaluator: IEvaluator) (agent: IAgent) (case: EvalCase) : Task<EvalResult> =
        task {
            let sw = Stopwatch.StartNew()
            let! output = agent.RunAsync case.Input
            sw.Stop()

            let! (verdict, reason) = evaluator.EvaluateAsync case output

            return
                { CaseId = case.Id
                  ActualOutput = output
                  Verdict = verdict
                  Reason = reason
                  LatencyMs = sw.ElapsedMilliseconds
                  EvaluatorName = evaluator.Name
                  Timestamp = DateTimeOffset.UtcNow
                  ExecutionTrace = None }
        }

    /// Run all cases in a dataset against an agent
    let runDatasetAsync
        (config: EvalRunnerConfig)
        (evaluator: IEvaluator)
        (agent: IAgent)
        (dataset: EvalDataset)
        : Task<EvalReport> =
        task {
            let results = ResizeArray<EvalResult>()
            let runCase = if config.CaptureTraces then runCaseAsync else runCaseLightAsync

            if config.MaxParallelism <= 1 then
                // Sequential execution
                for case in dataset.Cases do
                    let! result = runCase evaluator agent case
                    results.Add result
                    if config.StopOnFirstFailure && not (EvalResult.passed result) then
                        ()  // remaining cases skipped
            else
                // Parallel execution with bounded concurrency
                let semaphore = new System.Threading.SemaphoreSlim(config.MaxParallelism)
                let tasks =
                    dataset.Cases
                    |> List.map (fun case ->
                        task {
                            do! semaphore.WaitAsync()
                            try
                                let! result = runCase evaluator agent case
                                lock results (fun () -> results.Add result)
                            finally
                                semaphore.Release() |> ignore
                        })
                do! Task.WhenAll(tasks) :> Task

            return EvalReport.fromCasesAndResults dataset.Name (dataset.Cases) (results |> Seq.toList)
        }

    /// Run cases with multiple evaluators and combine results
    let runWithMultipleEvaluatorsAsync
        (config: EvalRunnerConfig)
        (evaluators: IEvaluator list)
        (agent: IAgent)
        (dataset: EvalDataset)
        : Task<EvalReport> =
        task {
            let results = ResizeArray<EvalResult>()
            let runCase = if config.CaptureTraces then runCaseAsync else runCaseLightAsync

            for case in dataset.Cases do
                for evaluator in evaluators do
                    let! result = runCase evaluator agent case
                    results.Add result

            return EvalReport.fromCasesAndResults dataset.Name (dataset.Cases) (results |> Seq.toList)
        }

    /// Compare two agents on the same dataset
    let compareAgentsAsync
        (config: EvalRunnerConfig)
        (evaluator: IEvaluator)
        (agents: (string * IAgent) list)
        (dataset: EvalDataset)
        : Task<(string * EvalReport) list> =
        task {
            let mutable reports = []
            for (name, agent) in agents do
                let! report = runDatasetAsync config evaluator agent dataset
                reports <- reports @ [ (name, { report with Name = sprintf "%s - %s" dataset.Name name }) ]
            return reports
        }
