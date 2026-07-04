namespace Nao.Agents

open System
open System.Threading.Tasks

/// Regression detection: compare current trace against baseline
type RegressionResult =
    { /// Whether regression was detected
      IsRegression: bool
      /// Specific regressions found
      Regressions: RegressionItem list
      /// Baseline trace used for comparison
      BaselineTraceId: Guid option }

/// A single regression item
and RegressionItem =
    { /// What regressed
      Description: string
      /// Category of regression
      Category: RegressionCategory
      /// Severity (0.0 to 1.0)
      Severity: float
      /// Baseline value
      BaselineValue: string
      /// Current value
      CurrentValue: string }

/// Categories of regression
and [<RequireQualifiedAccess>] RegressionCategory =
    | Quality
    | Latency
    | Cost
    | ToolUsage
    | SuccessRate

/// Stores execution traces for regression comparison
type ITraceStore =
    /// Save an execution trace
    abstract member SaveAsync: ExecutionTrace -> Task<unit>
    /// Get the most recent successful trace for an agent + task pattern
    abstract member GetBaselineAsync: AgentId -> taskPattern: string -> Task<ExecutionTrace option>
    /// Get all traces for an agent
    abstract member GetTracesAsync: AgentId -> limit: int -> Task<ExecutionTrace list>

module Regression =

    /// Compare current trace against a baseline to detect regressions
    let detect (baseline: ExecutionTrace) (current: ExecutionTrace) : RegressionResult =
        let regressions = ResizeArray<RegressionItem>()

        // Check step count regression (significantly more steps = potential regression)
        let baselineSteps = baseline.Steps.Length
        let currentSteps = current.Steps.Length
        if currentSteps > baselineSteps * 2 then
            regressions.Add
                { Description = "Step count significantly increased"
                  Category = RegressionCategory.Latency
                  Severity = 0.5
                  BaselineValue = string baselineSteps
                  CurrentValue = string currentSteps }

        // Check total duration regression
        let baselineDuration =
            match baseline.CompletedAt with
            | Some c -> (c - baseline.StartedAt).TotalMilliseconds
            | None -> 0.0
        let currentDuration =
            match current.CompletedAt with
            | Some c -> (c - current.StartedAt).TotalMilliseconds
            | None -> 0.0
        if currentDuration > baselineDuration * 2.0 && baselineDuration > 0.0 then
            regressions.Add
                { Description = "Execution duration significantly increased"
                  Category = RegressionCategory.Latency
                  Severity = 0.7
                  BaselineValue = sprintf "%.0fms" baselineDuration
                  CurrentValue = sprintf "%.0fms" currentDuration }

        // Check success regression
        if baseline.Success && not current.Success then
            regressions.Add
                { Description = "Execution that previously succeeded now fails"
                  Category = RegressionCategory.SuccessRate
                  Severity = 1.0
                  BaselineValue = "Success"
                  CurrentValue = "Failure" }

        { IsRegression = regressions.Count > 0
          Regressions = regressions |> Seq.toList
          BaselineTraceId = Some baseline.Id }
