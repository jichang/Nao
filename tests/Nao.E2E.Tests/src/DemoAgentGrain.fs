namespace Nao.E2E.Tests

open System.Threading.Tasks
open Nao.Agents
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
            if toolName <> "" then Some (toolName, args) else None
        else
            None

    let findTool (tools: Tool list) (name: string) =
        tools |> List.tryFind (fun t -> t.Name = name)

/// A demo agent that uses the local LLM provider and tools.
/// When the LLM response contains a tool invocation JSON pattern,
/// the agent executes the tool and feeds the result back to the LLM.
type DemoAgent(provider: ILlmProvider, tools: Tool list, prompt: Prompt) =
    let id = { Name = "demo-agent"; Description = "A demo agent for E2E testing" }

    member private _.RunCore(input: string) : Task<string> =
        let systemMsg = { Role = System; Content = Prompt.render prompt }
        let userMsg = { Role = User; Content = input }
        let conv1 = [systemMsg; userMsg]

        let result = (provider.CompleteAsync conv1 CompletionOptions.Default).Result
        let assistantMsg = { Role = Assistant; Content = result.Content }
        let conv2 = conv1 @ [assistantMsg]

        match AgentHelpers.tryParseToolCall result.Content with
        | Some (toolName, args) ->
            match AgentHelpers.findTool tools toolName with
            | Some tool ->
                let toolResult = (tool.Execute ToolContext.allowAll args).Result
                let toolMsg = { Role = User; Content = "tool_result: " + toolResult }
                let conv3 = conv2 @ [toolMsg]
                let finalResult = (provider.CompleteAsync conv3 CompletionOptions.Default).Result
                Task.FromResult(finalResult.Content)
            | None ->
                Task.FromResult("Unknown tool: " + toolName)
        | None ->
            Task.FromResult(result.Content)

    interface IAgent with
        member _.Id = id
        member this.RunAsync(input: string) = this.RunCore(input)
        member this.HandleMessageAsync(msg: AgentMessage) =
            let response = this.RunCore(msg.Content).Result
            let reply = AgentMessage.create id msg.From response
            Task.FromResult(Some reply)

/// Test workspace definitions that provide DemoAgent via built agents/tools
module DemoWorkspace =
    let private provider = LocalLlmProvider() :> ILlmProvider
    let private tools = [ DemoTools.getWeather; DemoTools.calculator; DemoTools.greeter ]
    let private prompt =
        { Prompt.Empty with
            Role = "You are a helpful assistant with access to tools."
            Objective = "Help the user by answering questions. Use tools when needed."
            Constraints = ["Always use a tool when the user asks about weather or math."] }

    let createAgent () = DemoAgent(provider, tools, prompt) :> IAgent

    let definitions : Nao.Runtime.Orleans.WorkspaceDefinitions =
        { Agents = [ createAgent () ]
          Tools = tools
          Constitutions = [] }
