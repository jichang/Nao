namespace Nao.Agents

open System
open System.Threading

type ResourceBudget =
    private
        { SyncRoot: obj
          mutable Usage: ResourceUsage }

/// Mutable resource-tracking state for one agent execution.
type ExecutionContext =
    {
        /// Unique identifier for this execution run.
        ExecutionId: ExecutionId
        /// Correlation, causation, and attempt identity for this execution.
        Correlation: CorrelationContext
        /// Sandbox configuration governing this execution.
        Sandbox: SandboxConfig
        /// Cancellation token for cooperative cancellation.
        CancellationToken: CancellationToken
        /// Resource usage shared by this execution and its nested work.
        Budget: ResourceBudget
        /// When the execution started.
        StartedAt: DateTimeOffset
        /// Parent context for a delegated sub-agent execution.
        ParentContext: ExecutionContext option
    }

    static member Create(sandbox: SandboxConfig) =
        ExecutionContext.CreateWithCorrelation sandbox (CorrelationContext.root ())

    static member CreateWithCorrelation (sandbox: SandboxConfig) (correlation: CorrelationContext) =
        { ExecutionId = correlation.ExecutionId
          Correlation = correlation
          Sandbox = sandbox
          CancellationToken = CancellationToken.None
          Budget =
            { SyncRoot = obj ()
              Usage = ResourceUsage.Zero }
          StartedAt = DateTimeOffset.UtcNow
          ParentContext = None }

    member this.Usage = lock this.Budget.SyncRoot (fun () -> this.Budget.Usage)

    static member CreateWithCancellation (sandbox: SandboxConfig) (cancellationToken: CancellationToken) =
        { ExecutionContext.Create sandbox with
            CancellationToken = cancellationToken }

    member this.CreateChild(correlation: CorrelationContext) =
        { this with
            ExecutionId = correlation.ExecutionId
            Correlation = correlation
            ParentContext = Some this }

    member this.CreateChild() =
        this.CreateChild(CorrelationContext.delegateFrom this.Correlation)

    member this.CreateRetry() =
        let correlation = CorrelationContext.retry this.Correlation

        { this with
            ExecutionId = correlation.ExecutionId
            Correlation = correlation
            ParentContext = this.ParentContext }

    member this.RecordLlmCall(tokens: int, costUsd: decimal) =
        lock this.Budget.SyncRoot (fun () ->
            this.Budget.Usage <-
                { this.Budget.Usage with
                    LlmCalls = this.Budget.Usage.LlmCalls + 1
                    TotalTokens = this.Budget.Usage.TotalTokens + tokens
                    EstimatedCostUsd = this.Budget.Usage.EstimatedCostUsd + costUsd
                    ElapsedTime = DateTimeOffset.UtcNow - this.StartedAt })

    member this.BeginLlmCall() =
        lock this.Budget.SyncRoot (fun () ->
            let previous = this.Budget.Usage

            this.Budget.Usage <-
                { this.Budget.Usage with
                    LlmCalls = this.Budget.Usage.LlmCalls + 1
                    ElapsedTime = DateTimeOffset.UtcNow - this.StartedAt }

            match ResourceUsage.check this.Sandbox.Limits this.Budget.Usage with
            | None -> None
            | Some limit ->
                this.Budget.Usage <- previous
                Some limit)

    member this.RecordLlmUsage(tokens: int, costUsd: decimal) =
        lock this.Budget.SyncRoot (fun () ->
            this.Budget.Usage <-
                { this.Budget.Usage with
                    TotalTokens = this.Budget.Usage.TotalTokens + tokens
                    EstimatedCostUsd = this.Budget.Usage.EstimatedCostUsd + costUsd
                    ElapsedTime = DateTimeOffset.UtcNow - this.StartedAt }

            ResourceUsage.check this.Sandbox.Limits this.Budget.Usage)

    member this.RecordToolCall() =
        lock this.Budget.SyncRoot (fun () ->
            this.Budget.Usage <-
                { this.Budget.Usage with
                    ToolCalls = this.Budget.Usage.ToolCalls + 1
                    ElapsedTime = DateTimeOffset.UtcNow - this.StartedAt })

    member this.BeginToolCall() =
        lock this.Budget.SyncRoot (fun () ->
            let previous = this.Budget.Usage

            this.Budget.Usage <-
                { this.Budget.Usage with
                    ToolCalls = this.Budget.Usage.ToolCalls + 1
                    ElapsedTime = DateTimeOffset.UtcNow - this.StartedAt }

            match ResourceUsage.check this.Sandbox.Limits this.Budget.Usage with
            | None -> None
            | Some limit ->
                this.Budget.Usage <- previous
                Some limit)

    member this.CheckLimits() : LimitExceeded option =
        lock this.Budget.SyncRoot (fun () ->
            this.Budget.Usage <-
                { this.Budget.Usage with
                    ElapsedTime = DateTimeOffset.UtcNow - this.StartedAt }

            ResourceUsage.check this.Sandbox.Limits this.Budget.Usage)
