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
        /// Exact policy and grant versions used for a permission check, when this entry records one.
        PermissionEvidence: PermissionDecisionEvidence option
        /// Exact policy version and outcome for a runtime policy evaluation, when this entry records one.
        PolicyEvidence: PolicyEvaluationEvidence option
        /// Exact constitution rule evaluation, when this entry records one.
        ConstitutionEvidence: ConstitutionEvaluationEvidence option
        /// Execution context identifier
        ExecutionId: ExecutionId option
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
    | PolicyEvaluation of policyId: string
    | ConstitutionEvaluation of ruleId: string
    | LifecycleTransition of fromState: string * toState: string

/// Functional audit logging operations.
type AuditLog =
    {
        /// Record an audit entry
        RecordAsync: AuthorizationScope -> AuditEntry -> Task<unit>
        /// Query audit entries for an agent within an authenticated scope
        QueryAsync: AuthorizationScope -> string -> DateTimeOffset -> Task<AuditEntry list>
        /// Query all entries for an execution within an authenticated scope
        QueryByExecutionAsync: AuthorizationScope -> ExecutionId -> Task<AuditEntry list>
        /// Get a count of denied actions for an agent within an authenticated scope
        GetDeniedCountAsync: AuthorizationScope -> string -> DateTimeOffset -> Task<int>
        /// Delete every audit entry in an authenticated scope
        DeleteScopeAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
        /// Delete audit entries in an authenticated scope that precede a retention cutoff
        DeleteExpiredAsync: AuthorizationScope -> DateTimeOffset -> Task<Result<int, PlatformFailure>>
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
        (execId: ExecutionId option)
        : AuditEntry =
        { Id = Guid.NewGuid()
          Timestamp = DateTimeOffset.UtcNow
          AgentId = agentId
          Action = AuditAction.ToolInvocation toolName
          Input = Some input
          Output = Some output
          Permitted = permitted
          Decision = decision
          PermissionEvidence = None
          PolicyEvidence = None
          ConstitutionEvidence = None
          ExecutionId = execId
          Metadata = Map.empty }

    /// Create an audit entry for an LLM call
    let llmCall (agentId: string) (model: string) (execId: ExecutionId option) : AuditEntry =
        { Id = Guid.NewGuid()
          Timestamp = DateTimeOffset.UtcNow
          AgentId = agentId
          Action = AuditAction.LlmCall model
          Input = None
          Output = None
          Permitted = true
          Decision = PermissionDecision.Allow
          PermissionEvidence = None
          PolicyEvidence = None
          ConstitutionEvidence = None
          ExecutionId = execId
          Metadata = Map.empty }

    /// Create an audit entry for one constitution rule evaluation.
    let constitutionEvaluation agentId (evidence: ConstitutionEvaluationEvidence) execId =
        let permitted =
            evidence.Outcome = ConstitutionEvaluationOutcome.Passed
            || evidence.RuleSeverity = RuleSeverity.Advisory
            || evidence.RuleSeverity = RuleSeverity.Warning

        { Id = evidence.Id
          Timestamp = evidence.EvaluatedAt
          AgentId = agentId
          Action = AuditAction.ConstitutionEvaluation evidence.RuleId
          Input = None
          Output = None
          Permitted = permitted
          Decision =
            if permitted then
                PermissionDecision.Allow
            else
                PermissionDecision.Deny
          PermissionEvidence = None
          PolicyEvidence = None
          ConstitutionEvidence = Some evidence
          ExecutionId = execId
          Metadata = Map.empty }

    let permissionDecision agentId (evidence: PermissionDecisionEvidence) execId =
        { Id = Guid.NewGuid()
          Timestamp = evidence.EvaluatedAt
          AgentId = agentId
          Action =
            AuditAction.ResourceAccess(ResourceAccess.kind evidence.Access, ResourceAccess.resource evidence.Access)
          Input = None
          Output = None
          Permitted = evidence.Decision = PermissionDecision.Allow
          Decision = evidence.Decision
          PermissionEvidence = Some evidence
          PolicyEvidence = None
          ConstitutionEvidence = None
          ExecutionId = execId
          Metadata = Map.empty }

    let policyEvaluation (evidence: PolicyEvaluationEvidence) execId =
        let permitted =
            evidence.Outcome <> PolicyEvaluationOutcome.Blocked
            && evidence.Outcome <> PolicyEvaluationOutcome.Failed
            && evidence.Confirmation <> Some PolicyConfirmation.Denied

        { Id = evidence.Id
          Timestamp = evidence.EvaluatedAt
          AgentId = evidence.AgentId
          Action = AuditAction.PolicyEvaluation evidence.PolicyId
          Input = None
          Output = None
          Permitted = permitted
          Decision =
            if permitted then
                PermissionDecision.Allow
            else
                PermissionDecision.Deny
          PermissionEvidence = None
          PolicyEvidence = Some evidence
          ConstitutionEvidence = None
          ExecutionId = execId
          Metadata = Map.empty }
