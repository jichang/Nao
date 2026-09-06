namespace Nao.Agents

open System.Threading
open System.Threading.Tasks

/// Canonical agent and tool dispatch installed for the current asynchronous execution flow.
type ExecutionDispatcher =
    { RunAgent: AgentContext -> Agent -> string -> Task<Result<string, PlatformFailure>>
      RunTool: AgentContext -> Tool -> string -> Task<Result<string, PlatformFailure>> }

[<RequireQualifiedAccess>]
module ExecutionRuntime =
    let private current = AsyncLocal<ExecutionDispatcher option>()

    let get () = current.Value
    let set dispatcher = current.Value <- dispatcher

    let private missingHarness context capability =
        PlatformFailure.create
            PlatformErrorCategory.PermissionDenied
            (sprintf "%s execution requires an active harness." capability)
            false
            (context.Correlation.ExecutionId |> ExecutionId.serialize |> Some)
        |> Error
        |> Task.FromResult

    let runAgent context (agent: Agent) input =
        match current.Value, context.ExecutionBoundary with
        | Some dispatcher, _ -> dispatcher.RunAgent context agent input
        | None, ExecutionBoundary.Unrestricted ->
            task {
                let! output = agent.Execute context input
                return Ok output
            }
        | None, ExecutionBoundary.HarnessRequired -> missingHarness context "Child agent"

    let runTool context (tool: Tool) input =
        match current.Value, context.ExecutionBoundary with
        | Some dispatcher, _ -> dispatcher.RunTool context tool input
        | None, ExecutionBoundary.Unrestricted ->
            task {
                let! result = tool.RunAsync context input

                return
                    result
                    |> Result.mapError (fun failure ->
                        failure.ToPlatformFailure(context.Correlation.ExecutionId |> ExecutionId.serialize |> Some))
            }
        | None, ExecutionBoundary.HarnessRequired -> missingHarness context "Tool"
