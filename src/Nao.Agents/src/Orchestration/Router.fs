namespace Nao.Agents

open System.Threading.Tasks

/// Strategy for selecting which agent handles a request
type RoutingStrategy =
    | ByName of string
    | ByPrompt of IAgent
    | RoundRobin
    | Custom of (string -> IAgent list -> Task<IAgent>)

/// Router dispatches input to the most appropriate agent
type Router =
    { Agents: IAgent list
      Strategy: RoutingStrategy }

module Router =

    /// Create a router with a list of agents and a strategy
    let create (agents: IAgent list) (strategy: RoutingStrategy) =
        { Agents = agents; Strategy = strategy }

    /// Find an agent by name
    let findAgent (name: string) (router: Router) =
        router.Agents |> List.tryFind (fun a -> a.Id.Name = name)

    /// Route input to an agent and return its response
    let routeAsync (input: string) (router: Router) : Task<string> =
        task {
            match router.Strategy with
            | ByName name ->
                match findAgent name router with
                | Some agent -> return! agent.RunAsync input
                | None -> return sprintf "Agent '%s' not found" name
            | ByPrompt supervisor ->
                let! selectedName = supervisor.RunAsync input
                match findAgent (selectedName.Trim()) router with
                | Some agent -> return! agent.RunAsync input
                | None -> return sprintf "Agent '%s' not found" (selectedName.Trim())
            | RoundRobin ->
                match router.Agents with
                | agent :: _ -> return! agent.RunAsync input
                | [] -> return "No agents available"
            | Custom selector ->
                let! agent = selector input router.Agents
                return! agent.RunAsync input
        }
