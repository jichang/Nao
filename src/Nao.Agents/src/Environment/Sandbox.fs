namespace Nao.Agents

open System
open System.Threading
open System.Threading.Tasks

/// Isolation level for agent execution sandbox
[<RequireQualifiedAccess>]
type SandboxIsolation =
    /// No isolation — runs in the host process (default, for trusted agents)
    | None
    /// Process-level isolation — separate process per agent execution
    | Process
    /// Container-level isolation — separate container per execution
    | Container

/// Configuration for the execution sandbox
type SandboxConfig =
    { /// Resource budget for this execution
      Limits: ResourceLimits
      /// Isolation level
      Isolation: SandboxIsolation
      /// Working directory for file operations (if any)
      WorkingDirectory: string option
      /// Environment variables available to the agent
      EnvironmentVariables: Map<string, string>
      /// Whether the agent can access the network
      AllowNetwork: bool
      /// Whether the agent can access the filesystem
      AllowFileSystem: bool
      /// Allowed filesystem paths (only relevant if AllowFileSystem is true)
      AllowedPaths: string list }

    static member Default =
        { Limits = ResourceLimits.Unlimited
          Isolation = SandboxIsolation.None
          WorkingDirectory = None
          EnvironmentVariables = Map.empty
          AllowNetwork = true
          AllowFileSystem = false
          AllowedPaths = [] }

    static member Restricted limits =
        { SandboxConfig.Default with
            Limits = limits
            AllowNetwork = false
            AllowFileSystem = false }

/// Represents the execution context passed to an agent during its run
type ExecutionContext =
    { /// Unique identifier for this execution run
      ExecutionId: Guid
      /// The sandbox configuration governing this execution
      Sandbox: SandboxConfig
      /// Cancellation token for cooperative cancellation
      CancellationToken: CancellationToken
      /// Current resource usage (mutable tracking)
      mutable Usage: ResourceUsage
      /// When the execution started
      StartedAt: DateTimeOffset
      /// Parent execution context (for delegated sub-agent calls)
      ParentContext: ExecutionContext option }

    static member Create(sandbox: SandboxConfig) =
        { ExecutionId = Guid.NewGuid()
          Sandbox = sandbox
          CancellationToken = CancellationToken.None
          Usage = ResourceUsage.Zero
          StartedAt = DateTimeOffset.UtcNow
          ParentContext = None }

    static member CreateWithCancellation (sandbox: SandboxConfig) (ct: CancellationToken) =
        { ExecutionContext.Create sandbox with CancellationToken = ct }

    member this.CreateChild() =
        { ExecutionContext.Create this.Sandbox with ParentContext = Some this }

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
        this.Usage <- { this.Usage with ElapsedTime = DateTimeOffset.UtcNow - this.StartedAt }
        ResourceUsage.check this.Sandbox.Limits this.Usage

/// Interface for execution environment providers
type IExecutionEnvironment =
    /// Execute an agent within the sandbox, respecting resource limits
    abstract member ExecuteAsync: ExecutionContext -> IAgent -> string -> Task<Result<string, LimitExceeded>>
