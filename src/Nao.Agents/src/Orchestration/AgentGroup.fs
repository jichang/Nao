namespace Nao.Agents

open System.Threading.Tasks

/// Termination condition for a collaborative group conversation
type TerminationCondition =
    | MaxRounds of int
    | ContentContains of string
    | Custom of (AgentMessage list -> bool)

/// A collaborative group where agents communicate via messages
type AgentGroup =
    { Agents: IAgent list
      Moderator: IAgent option
      Termination: TerminationCondition }

module AgentGroup =

    /// Create a group with agents and a termination condition
    let create (agents: IAgent list) (termination: TerminationCondition) =
        { Agents = agents
          Moderator = None
          Termination = termination }

    /// Create a moderated group where a moderator coordinates turns
    let createModerated (agents: IAgent list) (moderator: IAgent) (termination: TerminationCondition) =
        { Agents = agents
          Moderator = Some moderator
          Termination = termination }

    /// Check if the conversation should terminate
    let shouldTerminate (history: AgentMessage list) (group: AgentGroup) =
        match group.Termination with
        | MaxRounds maxRounds -> history.Length >= maxRounds * group.Agents.Length
        | ContentContains keyword ->
            history
            |> List.exists (fun m -> m.Content.Contains(keyword))
        | Custom predicate -> predicate history

    /// Run a collaborative conversation starting with an initial input
    let runAsync (input: string) (group: AgentGroup) : Task<AgentMessage list> =
        task {
            let history = ResizeArray<AgentMessage>()

            let seedId = { Name = "user"; Description = "Initial input" }
            let seed = AgentMessage.broadcast seedId input
            history.Add(seed)

            let mutable finished = false
            while not finished do
                for agent in group.Agents do
                    if not finished then
                        let lastMessage = history.[history.Count - 1]
                        let! reply = agent.HandleMessageAsync lastMessage
                        match reply with
                        | Some msg ->
                            history.Add(msg)
                            if shouldTerminate (history |> Seq.toList) group then
                                finished <- true
                        | None -> ()

            return history |> Seq.toList
        }
