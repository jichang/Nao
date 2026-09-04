namespace Nao.Agents

open System
open System.Threading

/// Mutable resource-tracking state for one agent execution.
type ExecutionContext =
    {
        /// Unique identifier for this execution run.
        ExecutionId: Guid
        /// Sandbox configuration governing this execution.
        Sandbox: SandboxConfig
        /// Cancellation token for cooperative cancellation.
        CancellationToken: CancellationToken
        /// Current resource usage.
        mutable Usage: ResourceUsage
        /// When the execution started.
        StartedAt: DateTimeOffset
        /// Parent context for a delegated sub-agent execution.
        ParentContext: ExecutionContext option
    }

    static member Create(sandbox: SandboxConfig) =
        { ExecutionId = Guid.NewGuid()
          Sandbox = sandbox
          CancellationToken = CancellationToken.None
          Usage = ResourceUsage.Zero
          StartedAt = DateTimeOffset.UtcNow
          ParentContext = None }

    static member CreateWithCancellation (sandbox: SandboxConfig) (cancellationToken: CancellationToken) =
        { ExecutionContext.Create sandbox with
            CancellationToken = cancellationToken }

    member this.CreateChild() =
        { ExecutionContext.Create this.Sandbox with
            ParentContext = Some this }

    member this.RecordLlmCall(tokens: int, costUsd: decimal) =
        this.Usage <-
            { this.Usage with
                LlmCalls = this.Usage.LlmCalls + 1
                TotalTokens = this.Usage.TotalTokens + tokens
                EstimatedCostUsd = this.Usage.EstimatedCostUsd + costUsd
                ElapsedTime = DateTimeOffset.UtcNow - this.StartedAt }

    member this.RecordToolCall() =
        this.Usage <-
            { this.Usage with
                ToolCalls = this.Usage.ToolCalls + 1
                ElapsedTime = DateTimeOffset.UtcNow - this.StartedAt }

    member this.CheckLimits() : LimitExceeded option =
        this.Usage <-
            { this.Usage with
                ElapsedTime = DateTimeOffset.UtcNow - this.StartedAt }

        ResourceUsage.check this.Sandbox.Limits this.Usage
