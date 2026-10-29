namespace Nao.DocumentationSnippets

open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
module AgentSnippet =
    let createEchoAgent () =
        Agent.create "echo" "Echo" "Returns the supplied text." 0 [ "Echo text" ] AgentContract.Text (fun _ input ->
            Task.FromResult input)

    let verify () =
        let agent = createEchoAgent ()

        if agent.Metadata.Id <> "echo" || agent.Metadata.Contract <> AgentContract.Text then
            invalidOp "The documented agent contract was not preserved."
