namespace Nao.E2E.Tests

open System.Threading.Tasks
open Nao.Agents
open Nao.Runtime.Orleans.Grains

module internal AgentHelpers =

    let tryParseToolCall (content: string) =
        // Simple pattern: {"tool":"name","args":"value"}
        let marker = "{" + "\"tool\"" + ":"

        if content.Contains(marker) then
            let parts = content.Split('"')
            let toolName = parts |> Array.tryItem 3 |> Option.defaultValue ""
            let args = parts |> Array.tryItem 7 |> Option.defaultValue ""
            if toolName <> "" then Some(toolName, args) else None
        else
            None

    let findTool (tools: Tool list) (name: string) =
        tools |> List.tryFind (fun t -> t.Name = name)

/// A demo agent that uses the local LLM provider and tools.
/// When the LLM response contains a tool invocation JSON pattern,
/// the agent executes the tool and feeds the result back to the LLM.
module DemoAgent =
    let create (provider: LlmProvider) (tools: Tool list) (prompt: Prompt) =
        let runCore (context: AgentContext) (input: string) : Task<string> =
            task {
                let systemMsg =
                    { Role = System
                      Content = Prompt.render prompt }

                let userMsg = { Role = User; Content = input }
                let conv1 = [ systemMsg; userMsg ]

                let! result = provider.CompleteAsync (CorrelationContext.root ()) conv1 CompletionOptions.Default

                let assistantMsg =
                    { Role = Assistant
                      Content = result.Content }

                let conv2 = conv1 @ [ assistantMsg ]

                match AgentHelpers.tryParseToolCall result.Content with
                | Some(toolName, args) ->
                    match AgentHelpers.findTool tools toolName with
                    | Some tool ->
                        let! execution = tool.RunAsync context args

                        let toolResult =
                            match execution with
                            | Ok output -> output
                            | Error failure -> failure.Message

                        let toolMsg =
                            { Role = User
                              Content = "tool_result: " + toolResult }

                        let conv3 = conv2 @ [ toolMsg ]

                        let! finalResult =
                            provider.CompleteAsync (CorrelationContext.root ()) conv3 CompletionOptions.Default

                        return finalResult.Content
                    | None -> return "Unknown tool: " + toolName
                | None -> return result.Content
            }

        let handleMessage context (message: AgentMessage) =
            task {
                let! response = runCore context message.Content
                return Some(AgentMessage.create "demo-agent" message.From response)
            }

        Agent.create
            "demo-agent"
            "demo-agent"
            "A demo agent for E2E testing"
            0
            []
            AgentContract.Text
            runCore
            handleMessage

/// Test workspace definitions that provide DemoAgent via built agents/tools
module DemoWorkspace =
    let private provider = LocalLlmProvider.create ()

    let private tools =
        [ DemoTools.getWeather; DemoTools.calculator; DemoTools.greeter ]

    let private prompt =
        { Prompt.Empty with
            Role = "You are a helpful assistant with access to tools."
            Objective = "Help the user by answering questions. Use tools when needed."
            Constraints = [ "Always use a tool when the user asks about weather or math." ] }

    let createAgent () = DemoAgent.create provider tools prompt

    let definitions: Nao.Runtime.Orleans.WorkspaceDefinitions =
        { Agents = [ createAgent () ]
          Tools = tools
          Constitutions = [] }
