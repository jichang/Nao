namespace Nao.Agents

open System
open System.Threading.Tasks

/// Termination condition for a collaborative group conversation
type TerminationCondition =
    | MaxRounds of int
    | ContentContains of string
    | Custom of (AgentMessage list -> bool)

/// Functional collaborative group definition.
type AgentGroup =
    { Agents: Agent list
      Moderator: Agent option
      Termination: TerminationCondition }

[<RequireQualifiedAccess>]
module AgentGroup =

    let create agents termination =
        { Agents = agents
          Moderator = None
          Termination = termination }

    let createModerated agents moderator termination =
        { Agents = agents
          Moderator = Some moderator
          Termination = termination }

    let shouldTerminate (history: AgentMessage list) (group: AgentGroup) =
        match group.Termination with
        | MaxRounds maxRounds ->
            group.Agents.IsEmpty
            || history.Length >= 1 + max 0 maxRounds * group.Agents.Length
        | ContentContains keyword -> history |> List.exists (fun message -> message.Content.Contains(keyword))
        | Custom predicate -> predicate history

    let runAsync (context: AgentContext) (input: string) (group: AgentGroup) : Task<AgentMessage list> =
        task {
            let history = ResizeArray<AgentMessage>()
            history.Add(AgentMessage.broadcast "user" input)

            let mutable finished = shouldTerminate (history |> Seq.toList) group

            while not finished do
                let mutable progressed = false

                for agent in group.Agents do
                    if not finished then
                        let previous = history.[history.Count - 1]
                        let! result = ExecutionRuntime.runAgent context agent previous.Content

                        match result with
                        | Ok output ->
                            let message = AgentMessage.create agent.Metadata.Id previous.From output
                            progressed <- true
                            history.Add message
                            finished <- shouldTerminate (history |> Seq.toList) group
                        | Error failure -> PlatformFailure.raiseException failure

                if not progressed then
                    finished <- true

            return history |> Seq.toList
        }
