namespace Nao.Agents

open System
open System.Threading.Tasks

/// A composed tool that chains, parallelizes, or conditionally routes between tools
[<RequireQualifiedAccess>]
type ToolComposition =
    /// Execute tools sequentially, passing output of one as input to next
    | Chain of steps: ToolStep list
    /// Execute tools in parallel and collect all results
    | Parallel of steps: ToolStep list
    /// Execute one of several tools based on a condition
    | Conditional of condition: (string -> string) * branches: Map<string, ToolStep>
    /// Execute a tool and if it fails, try the fallback
    | Fallback of primary: ToolStep * fallback: ToolStep
    /// Execute a tool, then use an LLM to decide the next step
    | Adaptive of step: ToolStep * router: (string -> Task<ToolStep option>)

/// A step in a tool composition
and ToolStep =
    { ToolName: string
      /// Optional transform applied to input before tool execution
      InputTransform: (string -> string) option
      /// Optional transform applied to output after tool execution
      OutputTransform: (string -> string) option
      /// Timeout for this step
      Timeout: TimeSpan option }

    static member Of(name: string) =
        { ToolName = name; InputTransform = None; OutputTransform = None; Timeout = None }

    static member WithTransform (inputFn: string -> string) (outputFn: string -> string) (step: ToolStep) =
        { step with InputTransform = Some inputFn; OutputTransform = Some outputFn }

/// Result of a composed tool execution
type CompositionResult =
    { FinalOutput: string
      StepResults: (string * ToolInvocationResult) list
      TotalDurationMs: int64 }

/// Executes tool compositions against an IToolProtocol
module ToolComposer =

    let private executeStep (protocol: IToolProtocol) (step: ToolStep) (input: string) : Task<ToolInvocationResult> =
        task {
            let transformedInput =
                match step.InputTransform with
                | Some fn -> fn input
                | None -> input
            let! result = protocol.InvokeAsync step.ToolName transformedInput
            return
                match step.OutputTransform with
                | Some fn -> { result with Output = fn result.Output }
                | None -> result
        }

    let rec executeAsync (protocol: IToolProtocol) (composition: ToolComposition) (input: string) : Task<CompositionResult> =
        task {
            let sw = System.Diagnostics.Stopwatch.StartNew()

            match composition with
            | ToolComposition.Chain steps ->
                let mutable currentInput = input
                let mutable failed = false
                let stepResults = ResizeArray<string * ToolInvocationResult>()
                for step in steps do
                    if not failed then
                        let! result = executeStep protocol step currentInput
                        stepResults.Add(step.ToolName, result)
                        if result.Success then
                            currentInput <- result.Output
                        else
                            failed <- true
                sw.Stop()
                return
                    { FinalOutput = if failed then "" else currentInput
                      StepResults = stepResults |> Seq.toList
                      TotalDurationMs = sw.ElapsedMilliseconds }

            | ToolComposition.Parallel steps ->
                let tasks =
                    steps
                    |> List.map (fun step ->
                        task {
                            let! result = executeStep protocol step input
                            return (step.ToolName, result)
                        })
                let! results = Task.WhenAll(tasks |> List.toArray)
                sw.Stop()
                let outputs =
                    results
                    |> Array.filter (fun (_, r) -> r.Success)
                    |> Array.map (fun (_, r) -> r.Output)
                    |> String.concat "\n---\n"
                return
                    { FinalOutput = outputs
                      StepResults = results |> Array.toList
                      TotalDurationMs = sw.ElapsedMilliseconds }

            | ToolComposition.Conditional (condition, branches) ->
                let branchKey = condition input
                match branches |> Map.tryFind branchKey with
                | Some step ->
                    let! result = executeStep protocol step input
                    sw.Stop()
                    return
                        { FinalOutput = result.Output
                          StepResults = [step.ToolName, result]
                          TotalDurationMs = sw.ElapsedMilliseconds }
                | None ->
                    sw.Stop()
                    return
                        { FinalOutput = ""
                          StepResults = []
                          TotalDurationMs = sw.ElapsedMilliseconds }

            | ToolComposition.Fallback (primary, fallback) ->
                let! primaryResult = executeStep protocol primary input
                if primaryResult.Success then
                    sw.Stop()
                    return
                        { FinalOutput = primaryResult.Output
                          StepResults = [primary.ToolName, primaryResult]
                          TotalDurationMs = sw.ElapsedMilliseconds }
                else
                    let! fallbackResult = executeStep protocol fallback input
                    sw.Stop()
                    return
                        { FinalOutput = fallbackResult.Output
                          StepResults = [primary.ToolName, primaryResult; fallback.ToolName, fallbackResult]
                          TotalDurationMs = sw.ElapsedMilliseconds }

            | ToolComposition.Adaptive (step, router) ->
                let mutable currentInput = input
                let stepResults = ResizeArray<string * ToolInvocationResult>()
                let mutable continueLoop = true
                let mutable currentStep = Some step
                while continueLoop do
                    match currentStep with
                    | Some s ->
                        let! result = executeStep protocol s currentInput
                        stepResults.Add(s.ToolName, result)
                        if result.Success then
                            currentInput <- result.Output
                            let! nextStep = router result.Output
                            currentStep <- nextStep
                            if nextStep.IsNone then continueLoop <- false
                        else
                            continueLoop <- false
                    | None -> continueLoop <- false
                sw.Stop()
                return
                    { FinalOutput = currentInput
                      StepResults = stepResults |> Seq.toList
                      TotalDurationMs = sw.ElapsedMilliseconds }
        }
