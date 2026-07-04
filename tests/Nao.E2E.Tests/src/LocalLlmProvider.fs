namespace Nao.E2E.Tests

open System.Threading.Tasks
open Nao.Agents

/// A local in-memory LLM provider for E2E testing.
/// Routes prompts to simple pattern-matched responses,
/// simulating tool invocation and final answers.
type LocalLlmProvider() =

    interface ILlmProvider with
        member _.Name = "LocalTest"

        member _.CompleteAsync (conversation: Conversation) (_options: CompletionOptions) : Task<CompletionResult> =
            let lastMessage =
                conversation
                |> List.tryFindBack (fun m -> m.Role = User)
                |> Option.map (fun m -> m.Content)
                |> Option.defaultValue ""

            let response =
                if lastMessage.Contains("tool_result:") then
                    // After receiving a tool result, produce a final answer
                    let result = lastMessage.Split("tool_result:") |> Array.last |> fun s -> s.Trim()
                    sprintf "Based on the tool result, the answer is: %s" result
                elif lastMessage.Contains("weather") then
                    """{"tool":"get_weather","args":"London"}"""
                elif lastMessage.Contains("calculate") || lastMessage.Contains("math") then
                    """{"tool":"calculator","args":"2 + 2"}"""
                else
                    sprintf "I can help with that. You said: %s" lastMessage

            Task.FromResult(
                { Content = response
                  FinishReason = "stop"
                  TokensUsed = Some (lastMessage.Length + response.Length) })
