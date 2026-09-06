namespace Nao.Agents

open System
open System.Diagnostics
open System.Threading.Tasks

/// Executable environment capability represented as an immutable function record.
type ExecutionEnvironment =
    { ExecuteAsync: ExecutionContext -> AgentContext -> Agent -> string -> Task<Result<string, LimitExceeded>> }

module ExecutionEnvironment =
    /// Create a local (in-process) execution environment
    let local () : ExecutionEnvironment =
        { ExecuteAsync =
            fun ctx agentContext agent input ->
                task {
                    if ctx.CancellationToken.IsCancellationRequested then
                        return raise (OperationCanceledException(ctx.CancellationToken))
                    else
                        match ctx.CheckLimits() with
                        | Some exceeded -> return Error exceeded
                        | None ->
                            let! result = agent.Execute agentContext input |> _.WaitAsync(ctx.CancellationToken)

                            if ctx.CancellationToken.IsCancellationRequested then
                                return raise (OperationCanceledException(ctx.CancellationToken))
                            else
                                match ctx.CheckLimits() with
                                | Some exceeded -> return Error exceeded
                                | None -> return Ok result
                } }

    /// Execute with timeout wrapping
    let executeWithTimeout
        (env: ExecutionEnvironment)
        (ctx: ExecutionContext)
        (agentContext: AgentContext)
        (agent: Agent)
        (input: string)
        : Task<Result<string, LimitExceeded>> =
        task {
            let timeout = ctx.Sandbox.Limits.MaxDuration
            use deadline = new System.Threading.CancellationTokenSource(timeout)

            use cts =
                System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken, deadline.Token)

            let linkedCtx =
                { ctx with
                    CancellationToken = cts.Token }

            let linkedAgentContext =
                { agentContext with
                    CancellationToken = cts.Token }

            try
                return! env.ExecuteAsync linkedCtx linkedAgentContext agent input
            with
            | :? TaskCanceledException -> return Error LimitExceeded.Duration
            | :? OperationCanceledException -> return Error LimitExceeded.Duration
        }
