namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Agents

/// Readiness check result
[<RequireQualifiedAccess>]
type ReadinessResult =
    | Ready
    | NotReady of reasons: string list

/// Task grounding: validates that the agent understands what it needs to do
type TaskGrounding =
    {
        /// The original user input/task
        Task: string
        /// Reformulated task understanding (agent's interpretation)
        Understanding: string option
        /// Key success criteria extracted from the task
        SuccessCriteria: string list
        /// Required capabilities to complete the task
        RequiredCapabilities: string list
        /// Estimated complexity (1-10)
        EstimatedComplexity: int option
    }

/// Functional pre-flight readiness check before agent execution.
type ReadinessCheck =
    {
        /// Check name.
        Name: string
        /// Perform the check.
        CheckAsync: string -> string -> Task<ReadinessResult>
    }

/// Functions for constructing and invoking readiness checks.
[<RequireQualifiedAccess>]
module ReadinessCheck =

    /// Construct a readiness check from its name and check function.
    let create name checkAsync : ReadinessCheck =
        { Name = name; CheckAsync = checkAsync }

    /// Perform a readiness check.
    let checkAsync agentId input (check: ReadinessCheck) = check.CheckAsync agentId input

/// Captures a complete execution trace for offline analysis
type ExecutionTrace =
    { Id: Guid
      Correlation: CorrelationContext
      AgentId: string
      Input: string
      Output: string option
      Steps: TraceStep list
      StartedAt: DateTimeOffset
      CompletedAt: DateTimeOffset option
      Success: bool
      Metadata: Map<string, string> }

/// A single step in an execution trace
and TraceStep =
    {
        /// Step number (1-based)
        StepNumber: int
        /// What action was taken
        Action: TraceAction
        /// Input to this step
        Input: string
        /// Output from this step
        Output: string
        /// Duration in milliseconds
        DurationMs: int64
        /// Timestamp
        Timestamp: DateTimeOffset
    }

/// Actions that can appear in a trace
and [<RequireQualifiedAccess>] TraceAction =
    | LlmCall of model: string
    | ToolInvocation of toolName: string
    | AgentDelegation of agentName: string
    | MemoryAccess of operation: string
    | Thinking
    | Validation

/// Verdict from an automated judge
[<RequireQualifiedAccess>]
type JudgementVerdict =
    | Pass
    | Fail
    | Partial of score: float
    | Inconclusive of reason: string

/// Result of automated judgement on an execution
type JudgementResult =
    {
        /// The verdict
        Verdict: JudgementVerdict
        /// Explanation for the verdict
        Explanation: string
        /// Scores on individual criteria
        CriteriaScores: Map<string, float>
        /// Suggestions for improvement
        Suggestions: string list
        /// The judge that produced this result
        JudgeName: string
    }

/// Functional capability for automated quality judgement.
type Judge =
    {
        /// Judge name.
        Name: string
        /// Evaluate an execution trace and produce a judgement.
        JudgeAsync: ExecutionTrace -> Task<JudgementResult>
    }

/// Functions for constructing and invoking judges.
[<RequireQualifiedAccess>]
module Judge =

    /// Construct a judge from its name and judgement function.
    let create name judgeAsync : Judge =
        { Name = name; JudgeAsync = judgeAsync }

    /// Evaluate an execution trace.
    let judgeAsync trace (judge: Judge) = judge.JudgeAsync trace

/// Captures and manages execution traces for verification
module Verification =

    /// Create a new execution trace
    let startTrace (correlation: CorrelationContext) (agentId: string) (input: string) : ExecutionTrace =
        { Id = Guid.NewGuid()
          Correlation = correlation
          AgentId = agentId
          Input = input
          Output = None
          Steps = []
          StartedAt = DateTimeOffset.UtcNow
          CompletedAt = None
          Success = false
          Metadata = Map.empty }

    /// Add a step to the trace
    let addStep
        (action: TraceAction)
        (input: string)
        (output: string)
        (durationMs: int64)
        (trace: ExecutionTrace)
        : ExecutionTrace =
        let step =
            { StepNumber = trace.Steps.Length + 1
              Action = action
              Input = input
              Output = output
              DurationMs = durationMs
              Timestamp = DateTimeOffset.UtcNow }

        { trace with
            Steps = trace.Steps @ [ step ] }

    /// Complete the trace with success
    let complete (output: string) (trace: ExecutionTrace) : ExecutionTrace =
        { trace with
            Output = Some output
            CompletedAt = Some DateTimeOffset.UtcNow
            Success = true }

    /// Complete the trace with failure
    let fail (error: string) (trace: ExecutionTrace) : ExecutionTrace =
        { trace with
            Output = Some error
            CompletedAt = Some DateTimeOffset.UtcNow
            Success = false }

    /// Run all readiness checks
    let checkReadiness (checks: ReadinessCheck list) (agentId: string) (input: string) : Task<ReadinessResult> =
        task {
            let! results = checks |> List.map (ReadinessCheck.checkAsync agentId input) |> Task.WhenAll

            let allReasons =
                results
                |> Array.toList
                |> List.collect (function
                    | ReadinessResult.Ready -> []
                    | ReadinessResult.NotReady reasons -> reasons)

            if allReasons.IsEmpty then
                return ReadinessResult.Ready
            else
                return ReadinessResult.NotReady allReasons
        }

    /// Ground a task by having the agent reformulate its understanding
    let groundTaskAsync
        (correlation: CorrelationContext)
        (provider: LlmProvider)
        (options: CompletionOptions)
        (taskDescription: string)
        : Task<TaskGrounding> =
        task {
            let system =
                Prompt.render
                    { Prompt.Empty with
                        Objective = "Analyze the following task and reformulate your understanding of it."
                        OutputFormat =
                            OutputFormat.Schema
                                "1. Your understanding of what needs to be done\n2. Key success criteria (one per line, prefixed with '- ')\n3. Required capabilities\n4. Estimated complexity (1-10)" }

            let prompt =
                [ { Role = System; Content = system }
                  { Role = User
                    Content = taskDescription } ]

            let! result = provider.CompleteAsync correlation prompt options
            // Parse the LLM response into structured grounding
            return
                { Task = taskDescription
                  Understanding = Some result.Content
                  SuccessCriteria = []
                  RequiredCapabilities = []
                  EstimatedComplexity = None }
        }

/// Factories for LLM-based execution-trace judges.
[<RequireQualifiedAccess>]
module LlmJudge =

    /// Create an LLM-based judge for the supplied criteria.
    let create (provider: LlmProvider) (options: CompletionOptions) (criteria: string list) : Judge =
        Judge.create "llm-judge" (fun trace ->
            task {
                let traceDescription =
                    trace.Steps
                    |> List.map (fun s ->
                        sprintf
                            "Step %d [%A]: Input=%s, Output=%s (%dms)"
                            s.StepNumber
                            s.Action
                            s.Input
                            (s.Output.Substring(0, min 200 s.Output.Length))
                            s.DurationMs)
                    |> String.concat "\n"

                let criteriaStr = criteria |> List.map (sprintf "- %s") |> String.concat "\n"

                let system =
                    Prompt.render
                        { Prompt.Empty with
                            Role = "You are a quality judge."
                            Objective =
                                "Evaluate the following agent execution trace against these criteria:\n"
                                + criteriaStr
                            OutputFormat =
                                OutputFormat.Schema "PASS, FAIL, or PARTIAL(score), followed by an explanation." }

                let prompt =
                    [ { Role = System; Content = system }
                      { Role = User
                        Content =
                          sprintf
                              "Task: %s\nOutput: %s\nSteps:\n%s"
                              trace.Input
                              (trace.Output |> Option.defaultValue "N/A")
                              traceDescription } ]

                let! result = provider.CompleteAsync trace.Correlation prompt options

                let verdict =
                    if result.Content.StartsWith("PASS") then
                        JudgementVerdict.Pass
                    elif result.Content.StartsWith("FAIL") then
                        JudgementVerdict.Fail
                    else
                        JudgementVerdict.Inconclusive result.Content

                return
                    { Verdict = verdict
                      Explanation = result.Content
                      CriteriaScores = Map.empty
                      Suggestions = []
                      JudgeName = "llm-judge" }
            })
