namespace Nao.Agents

open System
open System.Diagnostics
open System.Threading.Tasks

/// Executes an agent within an environment that enforces execution limits.
type IExecutionEnvironment =
    abstract member ExecuteAsync: ExecutionContext -> AgentContext -> IAgent -> string -> Task<Result<string, LimitExceeded>>

/// Default execution environment that runs agents in-process with resource tracking
type LocalExecutionEnvironment() =

    interface IExecutionEnvironment with
        member _.ExecuteAsync (ctx: ExecutionContext) (agentContext: AgentContext) (agent: IAgent) (input: string) : Task<Result<string, LimitExceeded>> =
            task {
                // Check limits before starting
                match ctx.CheckLimits() with
                | Some exceeded -> return Error exceeded
                | None ->
                    // Check cancellation
                    if ctx.CancellationToken.IsCancellationRequested then
                        return Error LimitExceeded.Duration
                    else
                        let! result = agent.RunAsync(agentContext, input)

                        // Check limits after execution
                        match ctx.CheckLimits() with
                        | Some exceeded -> return Error exceeded
                        | None -> return Ok result
            }

module ExecutionEnvironment =
    /// Create a local (in-process) execution environment
    let local () : IExecutionEnvironment =
        LocalExecutionEnvironment() :> IExecutionEnvironment

    /// Execute with timeout wrapping
    let executeWithTimeout (env: IExecutionEnvironment) (ctx: ExecutionContext) (agentContext: AgentContext) (agent: IAgent) (input: string) : Task<Result<string, LimitExceeded>> =
        task {
            let timeout = ctx.Sandbox.Limits.MaxDuration
            use cts = new System.Threading.CancellationTokenSource(timeout)
            let linkedCtx = { ctx with CancellationToken = cts.Token }
            try
                return! env.ExecuteAsync linkedCtx agentContext agent input
            with
            | :? TaskCanceledException ->
                return Error LimitExceeded.Duration
            | :? OperationCanceledException ->
                return Error LimitExceeded.Duration
        }
