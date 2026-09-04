namespace Nao.Agents

open System
open System.Threading.Tasks

/// The result of one iteration of a stateful execution loop.
type LoopTransition<'State, 'Output> =
    /// Continue with the supplied immutable state.
    | Continue of 'State
    /// Finish the loop with a final output.
    | Complete of 'Output

/// The bounded outcome of executing a loop.
type LoopOutcome<'State, 'Output> =
    /// The loop explicitly completed and reports how many iterations were executed.
    | Completed of output: 'Output * iterations: int
    /// The loop consumed its iteration budget without completing.
    | IterationLimitReached of lastState: 'State * iterations: int

/// A reusable, bounded state machine. Domain-specific code owns the state and transition;
/// the engine owns iteration accounting and termination enforcement.
type LoopDefinition<'State, 'Output> =
    { MaxIterations: int
      StepAsync: int -> 'State -> Task<LoopTransition<'State, 'Output>> }

[<RequireQualifiedAccess>]
module Loop =

    /// Execute a loop until it completes or reaches its configured iteration limit.
    let runAsync
        (definition: LoopDefinition<'State, 'Output>)
        (initialState: 'State)
        : Task<LoopOutcome<'State, 'Output>> =
        task {
            if definition.MaxIterations < 1 then
                invalidArg (nameof definition.MaxIterations) "A loop must allow at least one iteration."

            let mutable state = initialState
            let mutable iteration = 0
            let mutable output = None

            while output.IsNone && iteration < definition.MaxIterations do
                iteration <- iteration + 1
                let! transition = definition.StepAsync iteration state

                match transition with
                | Continue nextState -> state <- nextState
                | Complete result -> output <- Some result

            return
                match output with
                | Some result -> Completed(result, iteration)
                | None -> IterationLimitReached(state, iteration)
        }

/// An agent-specific loop definition. It adapts explicit domain state to Nao's stable text
/// transport contract while leaving lifecycle, governance, and observability to the harness.
type AgentLoopDefinition<'State> =
    { MaxIterations: int
      Initialize: AgentContext -> string -> 'State
      StepAsync: AgentContext -> int -> 'State -> Task<LoopTransition<'State, string>>
      OnLimitReached: 'State -> string }

[<RequireQualifiedAccess>]
module LoopAgent =

    /// Adapt a custom bounded loop to `IAgent`, allowing it to run through the ETCLOVG harness.
    let create
        (id: string)
        (name: string)
        (description: string)
        (priority: int)
        (responsibilities: string list)
        (contract: AgentContract)
        (definition: AgentLoopDefinition<'State>)
        : Agent =
        let execute (context: AgentContext) (input: string) =
            task {
                let loop =
                    { MaxIterations = definition.MaxIterations
                      StepAsync = definition.StepAsync context }

                let! outcome = Loop.runAsync loop (definition.Initialize context input)

                return
                    match outcome with
                    | Completed(output, _) -> output
                    | IterationLimitReached(state, _) -> definition.OnLimitReached state
            }

        Agent.createContextual id name description priority responsibilities contract execute
