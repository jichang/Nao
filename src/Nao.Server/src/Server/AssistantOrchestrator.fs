namespace Nao.Assistant

open System.Threading
open System.Threading.Tasks
open Nao.Core
open Nao.Agents

/// Event raised when the orchestrator wants user confirmation before executing a tool action.
type ToolConfirmationRequest =
    { ToolName: string
      Input: string
      Completion: TaskCompletionSource<bool> }

/// Custom orchestrator that prompts the user for confirmation before tool execution.
type AssistantOrchestrator(config: OrchestratorConfig, onConfirmation: ToolConfirmationRequest -> unit) =
    inherit OrchestratorBase(config)

    override this.TryParseActionsAsync(content: string) =
        task {
            let actions = this.DefaultTryParseActions(content)
            let approved = ResizeArray<AgentAction>()
            for action in actions do
                match action with
                | InvokeTool (toolName, input) ->
                    let tcs = TaskCompletionSource<bool>()
                    let request = { ToolName = toolName; Input = input; Completion = tcs }
                    onConfirmation request
                    let! confirmed = tcs.Task
                    if confirmed then approved.Add(InvokeTool (toolName, input))
                | other -> approved.Add other
            return List.ofSeq approved
        }

    /// When the LLM delegates to an agent flagged async (e.g. the converter), launch a
    /// background task that runs that agent in its own sub-session and return a token
    /// immediately, instead of blocking the conversation until it finishes. The user can
    /// track the task's status or download its generated files from the task tag. For
    /// synchronous agents this returns None so the base class delegates in-process.
    override _.TryHandleDelegationAsync(agentName: string, input: string) =
        task {
            match SessionExecution.current () with
            | Some scope when scope.AsyncAgents.Contains agentName ->
                let title = sprintf "%s agent" agentName
                let spec: SessionExecution.TaskSpec =
                    { Kind = "agent"
                      Title = title
                      Params = Map [ "agent", agentName; "input", input ] }
                let! taskId = scope.SpawnTask spec
                if System.String.IsNullOrEmpty taskId then
                    // No async task host available — fall back to in-process delegation.
                    return None
                else
                    return
                        Some(
                            sprintf
                                "Started a background **%s** task (`%s`). I've handed the work off to that specialist and you can keep chatting — track its status or download the result from the task tag when it finishes."
                                agentName taskId
                        )
            | _ -> return None
        }

/// Factory that creates AssistantOrchestrator instances with user confirmation support.
type AssistantOrchestratorFactory(onConfirmation: ToolConfirmationRequest -> unit) =
    interface IOrchestratorFactory with
        member _.Create(config) = AssistantOrchestrator(config, onConfirmation) :> IAgent
