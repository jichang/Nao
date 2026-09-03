namespace Nao.Agents

open System
open System.Threading.Tasks

/// A functional pipeline of immutable agents.
type Pipeline =
    { Stages: Agent list }

module Pipeline =

    /// Create a pipeline from an ordered list of agents.
    let create (stages: Agent list) =
        { Stages = stages }

    /// Run input through all stages sequentially, passing each output to the next.
    let runAsync (context: AgentContext) (input: string) (pipeline: Pipeline) : Task<string> =
        task {
            match ExecutionGraph.linear pipeline.Stages with
            | None -> return input
            | Some graph ->
                let! result = ExecutionGraph.runAsync context input graph

                match result with
                | Ok execution -> return execution.Output
                | Error(InvalidGraph problems) -> return raise (InvalidOperationException(String.concat " " problems))
                | Error(StepLimitReached(maxSteps, _)) -> return raise (InvalidOperationException(sprintf "Pipeline exceeded its graph limit of %d steps." maxSteps))
        }
