namespace Nao.Agents

open System
open System.Threading.Tasks

/// An entry in the audit log
type AuditEntry =
    {
        /// Unique entry identifier
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
        /// Permission decision that was applied
        Decision: PermissionDecision
        /// Any constitution violations
        ConstitutionViolations: string list
        /// Execution context identifier
        ExecutionId: Guid option
        /// Additional metadata
        Metadata: Map<string, string>
    }

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

/// Functional audit logging operations.
type AuditLog =
    {
        /// Record an audit entry
        RecordAsync: AuditEntry -> Task<unit>
        /// Query audit entries for an agent
        QueryAsync: string -> DateTimeOffset -> Task<AuditEntry list>
        /// Query all entries for an execution
        QueryByExecutionAsync: Guid -> Task<AuditEntry list>
        /// Get a count of denied actions for an agent
        GetDeniedCountAsync: string -> DateTimeOffset -> Task<int>
        /// Delete every audit entry owned by an agent
        DeleteOwnerAsync: string -> Task<Result<int, PlatformFailure>>
        /// Delete audit entries owned by an agent that precede a retention cutoff
        DeleteExpiredAsync: string -> DateTimeOffset -> Task<Result<int, PlatformFailure>>
    }

module AuditLog =

    /// Create an audit entry for a tool invocation
    let toolInvocation
        (agentId: string)
        (toolName: string)
        (input: string)
        (output: string)
        (permitted: bool)
        (decision: PermissionDecision)
        (execId: Guid option)
        : AuditEntry =
        { Id = Guid.NewGuid()
          Timestamp = DateTimeOffset.UtcNow
          AgentId = agentId
          Action = AuditAction.ToolInvocation toolName
          Input = Some input
          Output = Some output
          Permitted = permitted
          Decision = decision
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
          Decision = PermissionDecision.Allow
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
          Decision =
            if violations.IsEmpty then
                PermissionDecision.Allow
            else
                PermissionDecision.Deny
          ConstitutionViolations = violations
          ExecutionId = execId
          Metadata = Map.empty }
