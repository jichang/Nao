namespace Nao.Agents

open System
open System.Threading

type ResourceBudget =
    private
        { SyncRoot: obj
          mutable Usage: ResourceUsage }

type OperationSequence =
    private
        { SyncRoot: obj
          mutable ToolCalls: int
          mutable ProviderCalls: int
          mutable Artifacts: int }

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
        /// Stable turn key and operation sequence shared by this execution and nested work.
        IdempotencyKey: IdempotencyKey
        OperationSequence: OperationSequence
        /// When the execution started.
        StartedAt: DateTimeOffset
        /// Parent context for a delegated sub-agent execution.
        ParentContext: ExecutionContext option
    }

    static member Create(sandbox: SandboxConfig) =
        ExecutionContext.CreateWithCorrelation sandbox (CorrelationContext.root ())

    static member CreateWithCorrelation (sandbox: SandboxConfig) (correlation: CorrelationContext) =
        let fallbackKey = IdempotencyKey.forExecution correlation.ExecutionId

        let budget =
            { SyncRoot = obj ()
              Usage = ResourceUsage.Zero }

        let sequence =
            { SyncRoot = obj ()
              ToolCalls = 0
              ProviderCalls = 0
              Artifacts = 0 }

        let context: ExecutionContext =
            { ExecutionId = correlation.ExecutionId
              Correlation = correlation
              Sandbox = sandbox
              CancellationToken = CancellationToken.None
              Budget = budget
              IdempotencyKey = fallbackKey
              OperationSequence = sequence
              StartedAt = DateTimeOffset.UtcNow
              ParentContext = None }

        context

    static member CreateWithCorrelationAndCancellation
        (sandbox: SandboxConfig)
        (correlation: CorrelationContext)
        (cancellationToken: CancellationToken)
        =
        { ExecutionContext.CreateWithCorrelation sandbox correlation with
            CancellationToken = cancellationToken }

    static member CreateForRequest
        (sandbox: SandboxConfig)
        (correlation: CorrelationContext)
        (idempotencyKey: IdempotencyKey)
        (cancellationToken: CancellationToken)
        =
        { ExecutionContext.CreateWithCorrelationAndCancellation sandbox correlation cancellationToken with
            IdempotencyKey = idempotencyKey }

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

    member private this.NextOperationKey
        (kind: string, read: OperationSequence -> int, write: OperationSequence -> int -> unit)
        =
        lock this.OperationSequence.SyncRoot (fun () ->
            let ordinal = read this.OperationSequence + 1
            write this.OperationSequence ordinal
            IdempotencyKey.operation kind ordinal this.IdempotencyKey)

    member this.NextToolCallKey() =
        this.NextOperationKey("tool", _.ToolCalls, fun sequence value -> sequence.ToolCalls <- value)

    member this.NextProviderCallKey() =
        this.NextOperationKey("provider", _.ProviderCalls, fun sequence value -> sequence.ProviderCalls <- value)

    member this.NextArtifactKey() =
        this.NextOperationKey("artifact", _.Artifacts, fun sequence value -> sequence.Artifacts <- value)

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

    member this.RecordWorkerUsage(usage: ResourceUsage) =
        if
            usage.LlmCalls < 0
            || usage.TotalTokens < 0
            || usage.ToolCalls < 0
            || usage.EstimatedCostUsd < 0m
        then
            invalidArg (nameof usage) "Worker resource usage cannot be negative."

        lock this.Budget.SyncRoot (fun () ->
            this.Budget.Usage <-
                { this.Budget.Usage with
                    LlmCalls = this.Budget.Usage.LlmCalls + usage.LlmCalls
                    TotalTokens = this.Budget.Usage.TotalTokens + usage.TotalTokens
                    ToolCalls = this.Budget.Usage.ToolCalls + usage.ToolCalls
                    EstimatedCostUsd = this.Budget.Usage.EstimatedCostUsd + usage.EstimatedCostUsd
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
