namespace Nao.Agents

open System.Threading.Tasks

/// A pipeline processes input through a sequence of agents
type Pipeline =
    { Stages: IAgent list }

module Pipeline =

    /// Create a pipeline from an ordered list of agents
    let create (stages: IAgent list) =
        { Stages = stages }

    /// Run input through all stages sequentially, passing each output to the next
    let runAsync (input: string) (pipeline: Pipeline) : Task<string> =
        task {
            let mutable current = input
            for agent in pipeline.Stages do
                let! result = agent.RunAsync current
                current <- result
            return current
        }
