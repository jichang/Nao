namespace Nao.Agents

open System
open System.Threading.Tasks

/// Strategy for selecting an immutable agent.
type RoutingStrategy =
    | ByName of string
    | ByPrompt of Agent
    | RoundRobin
    | Custom of (string -> Agent list -> Task<Agent>)

/// Functional router definition.
type Router =
    { Agents: Agent list
      Strategy: RoutingStrategy }

[<RequireQualifiedAccess>]
module Router =

    let private invoke context input agent =
        task {
            match! ExecutionRuntime.runAgent context agent input with
            | Ok output -> return output
            | Error failure -> return PlatformFailure.raiseException failure
        }

    let create agents strategy =
        { Agents = agents; Strategy = strategy }

    let findAgent name router =
        router.Agents |> List.tryFind (fun agent -> agent.Metadata.Name = name)

    let routeAsync (context: AgentContext) (input: string) (router: Router) : Task<string> =
        task {
            let! selected =
                match router.Strategy with
                | ByName name -> Task.FromResult(findAgent name router)
                | ByPrompt supervisor ->
                    task {
                        let! selectedName = invoke context input supervisor
                        return findAgent (selectedName.Trim()) router
                    }
                | RoundRobin -> Task.FromResult(List.tryHead router.Agents)
                | Custom selector ->
                    task {
                        let! agent = selector input router.Agents
                        return Some agent
                    }

            match selected with
            | Some agent -> return! invoke context input agent
            | None -> return "No matching agent available"
        }
