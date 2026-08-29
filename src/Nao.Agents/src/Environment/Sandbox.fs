namespace Nao.Agents

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
