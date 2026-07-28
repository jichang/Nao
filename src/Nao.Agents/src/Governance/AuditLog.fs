namespace Nao.Agents

open System
open System.Threading.Tasks

/// An entry in the audit log
type AuditEntry =
    { /// Unique entry identifier
      Id: Guid
      /// When the action occurred
      Timestamp: DateTimeOffset
      /// Agent that performed the action
      AgentId: string
      /// What action was taken
      Action: AuditAction
      /// The input/context for the action
      Input: string option
      /// The output/result of the action
      Output: string option
      /// Whether the action was permitted
      Permitted: bool
      /// Permission level that was applied
      PermissionLevel: PermissionLevel
      /// Any constitution violations
      ConstitutionViolations: string list
      /// Execution context identifier
      ExecutionId: Guid option
      /// Additional metadata
      Metadata: Map<string, string> }

/// Actions that can be audited
and [<RequireQualifiedAccess>] AuditAction =
    | LlmCall of model: string
    | ToolInvocation of toolName: string
    | AgentDelegation of agentName: string
    | MemoryWrite of key: string
    | MemoryRead of key: string
    | ResourceAccess of resourceType: string * resource: string
    | PermissionCheck of capability: string
    | ConstitutionCheck
    | LifecycleTransition of fromState: string * toState: string

/// Interface for audit logging
type IAuditLog =
    /// Record an audit entry
    abstract member RecordAsync: AuditEntry -> Task<unit>
    /// Query audit entries for an agent
    abstract member QueryAsync: string -> since: DateTimeOffset -> Task<AuditEntry list>
    /// Query all entries for an execution
    abstract member QueryByExecutionAsync: Guid -> Task<AuditEntry list>
    /// Get a count of denied actions for an agent
    abstract member GetDeniedCountAsync: string -> since: DateTimeOffset -> Task<int>

module AuditLog =

    /// Create an audit entry for a tool invocation
    let toolInvocation (agentId: string) (toolName: string) (input: string) (output: string) (permitted: bool) (level: PermissionLevel) (execId: Guid option) : AuditEntry =
        { Id = Guid.NewGuid()
          Timestamp = DateTimeOffset.UtcNow
          AgentId = agentId
          Action = AuditAction.ToolInvocation toolName
          Input = Some input
          Output = Some output
          Permitted = permitted
          PermissionLevel = level
          ConstitutionViolations = []
          ExecutionId = execId
          Metadata = Map.empty }

    /// Create an audit entry for an LLM call
    let llmCall (agentId: string) (model: string) (execId: Guid option) : AuditEntry =
        { Id = Guid.NewGuid()
          Timestamp = DateTimeOffset.UtcNow
          AgentId = agentId
          Action = AuditAction.LlmCall model
          Input = None
          Output = None
          Permitted = true
          PermissionLevel = PermissionLevel.Allow
          ConstitutionViolations = []
          ExecutionId = execId
          Metadata = Map.empty }

    /// Create an audit entry for a constitution check
    let constitutionCheck (agentId: string) (violations: string list) (execId: Guid option) : AuditEntry =
        { Id = Guid.NewGuid()
          Timestamp = DateTimeOffset.UtcNow
          AgentId = agentId
          Action = AuditAction.ConstitutionCheck
          Input = None
          Output = None
          Permitted = violations.IsEmpty
          PermissionLevel = if violations.IsEmpty then PermissionLevel.Allow else PermissionLevel.Deny
          ConstitutionViolations = violations
          ExecutionId = execId
          Metadata = Map.empty }
