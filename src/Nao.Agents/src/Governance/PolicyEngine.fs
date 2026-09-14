namespace Nao.Agents

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type PolicyStage =
    | PreInput
    | PreAction
    | PreSideEffect
    | PostOutput
    | PreCommit

[<RequireQualifiedAccess>]
type PolicyScope =
    private
    | Platform
    | Tenant of TenantId
    | Workspace of TenantId * GroupId option * WorkspaceId
    | Agent of TenantId * GroupId option * WorkspaceId * agentId: string
    | Tool of TenantId * GroupId option * WorkspaceId * agentId: string * toolName: string
    | Session of AuthorizationIdentity

[<RequireQualifiedAccess>]
type PolicyScopeKind =
    | Platform
    | Tenant
    | Workspace
    | Agent
    | Tool
    | Session

[<RequireQualifiedAccess>]
module PolicyScope =
    let private validateName parameterName value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg parameterName "Policy scope names must be non-blank and have no surrounding whitespace."

    let platform = PolicyScope.Platform
    let tenant tenantId = PolicyScope.Tenant tenantId

    let workspace tenantId groupId workspaceId =
        PolicyScope.Workspace(tenantId, groupId, workspaceId)

    let agent tenantId groupId workspaceId agentId =
        validateName (nameof agentId) agentId
        PolicyScope.Agent(tenantId, groupId, workspaceId, agentId)

    let tool tenantId groupId workspaceId agentId toolName =
        validateName (nameof agentId) agentId
        validateName (nameof toolName) toolName
        PolicyScope.Tool(tenantId, groupId, workspaceId, agentId, toolName)

    let session authorization =
        if AuthorizationScope.sessionId authorization |> Option.isNone then
            invalidArg (nameof authorization) "Session policy scope requires session-bound authorization."

        PolicyScope.Session(AuthorizationScope.identity authorization)

    let internal kind =
        function
        | PolicyScope.Platform -> PolicyScopeKind.Platform
        | PolicyScope.Tenant _ -> PolicyScopeKind.Tenant
        | PolicyScope.Workspace _ -> PolicyScopeKind.Workspace
        | PolicyScope.Agent _ -> PolicyScopeKind.Agent
        | PolicyScope.Tool _ -> PolicyScopeKind.Tool
        | PolicyScope.Session _ -> PolicyScopeKind.Session

type PolicyEvaluationFailure =
    private
        { Code: string
          Message: string }

[<RequireQualifiedAccess>]
module PolicyEvaluationFailure =
    let create code message =
        if String.IsNullOrWhiteSpace code || code <> code.Trim() then
            invalidArg (nameof code) "Policy failure codes must be non-blank and have no surrounding whitespace."

        if String.IsNullOrWhiteSpace message || message <> message.Trim() then
            invalidArg (nameof message) "Policy failure messages must be non-blank and have no surrounding whitespace."

        { Code = code; Message = message }

    let code failure = failure.Code
    let message failure = failure.Message

/// A governance policy that can be enforced at runtime
type Policy =
    {
        /// Policy identifier
        Id: string
        /// Host-managed policy definition version.
        Version: string
        /// Human-readable description
        Description: string
        /// Hierarchical scope in which this policy applies.
        Scope: PolicyScope
        /// The enforcement action
        Enforcement: PolicyEnforcement
        /// Cancellation-aware check that distinguishes policy violations from evaluation failures.
        EvaluateAsync: PolicyContext -> CancellationToken -> Task<Result<string option, PolicyEvaluationFailure>>
    }

/// How a policy violation is handled
and [<RequireQualifiedAccess>] PolicyEnforcement =
    /// Block the action entirely
    | Block
    /// Allow but log a warning
    | Warn
    /// Allow but require confirmation callback
    | Confirm
    /// Modify the action (e.g., redact content)
    | Modify of transform: (string -> string)

/// Context passed to policy evaluation
and PolicyContext =
    { Authorization: AuthorizationScope
      ExecutionId: Nao.Agents.ExecutionId option
      Stage: PolicyStage
      AgentId: string
      ToolName: string option
      Action: string
      Input: string option
      Facts: PolicyFacts
      CurrentUsage: ResourceUsage option }

    /// Create a PolicyContext from an ExecutionContext (canonical factory)
    static member FromExecutionContext
        (authorization: AuthorizationScope)
        (agentId: string)
        (stage: PolicyStage)
        (action: string)
        (toolName: string option)
        (input: string option)
        (facts: PolicyFacts)
        (ctx: Nao.Agents.ExecutionContext)
        =
        { Authorization = authorization
          AgentId = agentId
          Stage = stage
          ToolName = toolName
          Action = action
          Input = input
          Facts = facts
          ExecutionId = Some ctx.ExecutionId
          CurrentUsage = Some ctx.Usage }

[<RequireQualifiedAccess>]
module Policy =
    let evaluateSynchronously evaluate context _ =
        evaluate context |> Ok |> Task.FromResult

[<RequireQualifiedAccess>]
type PolicyConfirmation =
    | Approved
    | Denied

/// Result of policy engine evaluation
type PolicyResult =
    {
        /// Whether execution should proceed
        Proceed: bool
        /// Policies that were violated
        Violations: PolicyViolation list
        /// Immutable evidence for every matching policy, including policies that passed.
        Evaluations: PolicyEvaluationEvidence list
        /// Modified input (if any policy transforms applied)
        ModifiedInput: string option
        /// Warnings generated
        Warnings: string list
        /// Whether one or more matching policies require host confirmation.
        RequiresConfirmation: bool
    }

and PolicyViolation =
    { PolicyId: string
      PolicyVersion: string
      Description: string
      Scope: PolicyScope
      Enforcement: PolicyEnforcement
      Message: string }

and [<RequireQualifiedAccess>] PolicyEvaluationOutcome =
    | Passed
    | Warned
    | Modified
    | ConfirmationRequired
    | Blocked
    | Failed

and PolicyEvaluationEvidence =
    { Id: Guid
      PolicyId: string
      PolicyVersion: string
      Scope: PolicyScopeKind
      Stage: PolicyStage
      AgentId: string
      ToolName: string option
      Action: string
      Outcome: PolicyEvaluationOutcome
      FailureCode: string option
      MessageDigest: string option
      InputDigest: string option
      EffectiveInputDigest: string option
      Confirmation: PolicyConfirmation option
      EvaluatedAt: DateTimeOffset }

type PolicyConfirmationRequest =
    { Authorization: AuthorizationScope
      Correlation: CorrelationContext
      Context: PolicyContext
      Violations: PolicyViolation list
      EffectiveInput: string option
      RequestedAt: DateTimeOffset }

[<NoEquality; NoComparison>]
type PolicyConfirmationBroker =
    private
        { Timeout: TimeSpan
          ResolveAsync: PolicyConfirmationRequest -> CancellationToken -> Task<PolicyConfirmation> }

[<RequireQualifiedAccess>]
module PolicyConfirmationBroker =
    let create timeout resolveAsync =
        if timeout <= TimeSpan.Zero then
            invalidArg (nameof timeout) "Policy confirmation timeout must be positive."

        { Timeout = timeout
          ResolveAsync = resolveAsync }

    let unavailable =
        create (TimeSpan.FromSeconds 30.0) (fun _ _ -> Task.FromResult PolicyConfirmation.Denied)

    let resolve broker request cancellationToken =
        task {
            try
                return!
                    broker.ResolveAsync request cancellationToken
                    |> fun pending -> pending.WaitAsync(broker.Timeout, cancellationToken)
            with :? TimeoutException ->
                return PolicyConfirmation.Denied
        }

/// Closure-backed policy evaluator.
[<NoEquality; NoComparison>]
type PolicyEngine =
    private
        { EvaluatePolicyAsync: PolicyContext -> CancellationToken -> Task<PolicyResult> }

    /// Evaluate all policies against a context with caller cancellation.
    member engine.EvaluateAsync(context: PolicyContext, cancellationToken: CancellationToken) =
        engine.EvaluatePolicyAsync context cancellationToken

module PolicyEngine =
    let private digest (value: string option) =
        value
        |> Option.map (fun value ->
            value
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexString
            |> _.ToLowerInvariant())

    let private scopeRank =
        function
        | PolicyScope.Platform -> 0
        | PolicyScope.Tenant _ -> 1
        | PolicyScope.Workspace _ -> 2
        | PolicyScope.Agent _ -> 3
        | PolicyScope.Tool _ -> 4
        | PolicyScope.Session _ -> 5

    let private scopeMatches (context: PolicyContext) =
        function
        | PolicyScope.Platform -> true
        | PolicyScope.Tenant tenantId -> AuthorizationScope.tenantId context.Authorization = tenantId
        | PolicyScope.Workspace(tenantId, groupId, workspaceId) ->
            AuthorizationScope.tenantId context.Authorization = tenantId
            && AuthorizationScope.groupId context.Authorization = groupId
            && AuthorizationScope.workspaceId context.Authorization = workspaceId
        | PolicyScope.Agent(tenantId, groupId, workspaceId, agentId) ->
            AuthorizationScope.tenantId context.Authorization = tenantId
            && AuthorizationScope.groupId context.Authorization = groupId
            && AuthorizationScope.workspaceId context.Authorization = workspaceId
            && context.AgentId = agentId
        | PolicyScope.Tool(tenantId, groupId, workspaceId, agentId, toolName) ->
            AuthorizationScope.tenantId context.Authorization = tenantId
            && AuthorizationScope.groupId context.Authorization = groupId
            && AuthorizationScope.workspaceId context.Authorization = workspaceId
            && context.AgentId = agentId
            && context.ToolName = Some toolName
        | PolicyScope.Session identity -> AuthorizationScope.identity context.Authorization = identity

    let private result proceed violations evaluations modifiedInput warnings requiresConfirmation : PolicyResult =
        { Proceed = proceed
          Violations = violations
          Evaluations = evaluations
          ModifiedInput = modifiedInput
          Warnings = warnings
          RequiresConfirmation = requiresConfirmation }

    let private isFatalException (error: exn) =
        error :? OutOfMemoryException
        || error :? StackOverflowException
        || error :? AccessViolationException
        || error :? AppDomainUnloadedException

    let private callbackFailure (policy: Policy) (error: exn) =
        PolicyEvaluationFailure.create
            "policy_callback_exception"
            (sprintf "Policy '%s@%s' raised %s: %s" policy.Id policy.Version (error.GetType().Name) error.Message)

    let private timeoutFailure (policy: Policy) timeout =
        PolicyEvaluationFailure.create
            "policy_evaluation_timeout"
            (sprintf "Policy '%s@%s' exceeded its %O evaluation timeout." policy.Id policy.Version timeout)

    /// Create a timeout-bounded policy engine from a list of policies.
    let create evaluationTimeout (policies: Policy list) : PolicyEngine =
        if evaluationTimeout <= TimeSpan.Zero then
            invalidArg (nameof evaluationTimeout) "Policy evaluation timeout must be positive."

        for policy in policies do
            if String.IsNullOrWhiteSpace policy.Id || policy.Id <> policy.Id.Trim() then
                invalidArg (nameof policies) "Policy identities must be non-blank and have no surrounding whitespace."

            if
                String.IsNullOrWhiteSpace policy.Version
                || policy.Version <> policy.Version.Trim()
            then
                invalidArg (nameof policies) "Policy versions must be non-blank and have no surrounding whitespace."

        let duplicateId =
            policies |> List.countBy _.Id |> List.tryFind (fun (_, count) -> count > 1)

        match duplicateId with
        | Some(id, _) -> invalidArg (nameof policies) $"Policy identity '{id}' is duplicated."
        | None -> ()

        let orderedPolicies =
            policies
            |> List.mapi (fun index policy -> index, policy)
            |> List.sortBy (fun (index, policy) -> scopeRank policy.Scope, index)
            |> List.map snd

        { EvaluatePolicyAsync =
            fun context cancellationToken ->
                task {
                    let violations = ResizeArray<PolicyViolation>()
                    let evaluations = ResizeArray<PolicyEvaluationEvidence>()
                    let warnings = ResizeArray<string>()
                    let mutable blocked = false
                    let mutable requiresConfirmation = false
                    let mutable effectiveInput = context.Input
                    let mutable inputModified = false

                    for policy in orderedPolicies |> List.filter (fun policy -> scopeMatches context policy.Scope) do
                        let effectiveContext = { context with Input = effectiveInput }
                        let inputDigest = digest effectiveInput

                        let! evaluation =
                            task {
                                use evaluationCancellation =
                                    CancellationTokenSource.CreateLinkedTokenSource cancellationToken

                                evaluationCancellation.CancelAfter evaluationTimeout

                                try
                                    return!
                                        policy.EvaluateAsync effectiveContext evaluationCancellation.Token
                                        |> fun pending -> pending.WaitAsync(evaluationTimeout, cancellationToken)
                                with
                                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                                    return raise (OperationCanceledException(cancellationToken))
                                | :? OperationCanceledException when evaluationCancellation.IsCancellationRequested ->
                                    return Error(timeoutFailure policy evaluationTimeout)
                                | :? TimeoutException -> return Error(timeoutFailure policy evaluationTimeout)
                                | error when not (isFatalException error) -> return Error(callbackFailure policy error)
                            }

                        let addEvidence outcome failureCode messageDigest effectiveDigest =
                            evaluations.Add
                                { Id = Guid.NewGuid()
                                  PolicyId = policy.Id
                                  PolicyVersion = policy.Version
                                  Scope = PolicyScope.kind policy.Scope
                                  Stage = context.Stage
                                  AgentId = context.AgentId
                                  ToolName = context.ToolName
                                  Action = context.Action
                                  Outcome = outcome
                                  FailureCode = failureCode
                                  MessageDigest = messageDigest
                                  InputDigest = inputDigest
                                  EffectiveInputDigest = effectiveDigest
                                  Confirmation = None
                                  EvaluatedAt = DateTimeOffset.UtcNow }

                        let fail failure =
                            let message =
                                sprintf
                                    "Policy evaluation failed [%s]: %s"
                                    (PolicyEvaluationFailure.code failure)
                                    (PolicyEvaluationFailure.message failure)

                            blocked <- true

                            violations.Add
                                { PolicyId = policy.Id
                                  PolicyVersion = policy.Version
                                  Description = policy.Description
                                  Scope = policy.Scope
                                  Enforcement = PolicyEnforcement.Block
                                  Message = message }

                            addEvidence
                                PolicyEvaluationOutcome.Failed
                                (Some(PolicyEvaluationFailure.code failure))
                                (digest (Some message))
                                (digest effectiveInput)

                        match evaluation with
                        | Error failure -> fail failure
                        | Ok(Some message) ->
                            violations.Add
                                { PolicyId = policy.Id
                                  PolicyVersion = policy.Version
                                  Description = policy.Description
                                  Scope = policy.Scope
                                  Enforcement = policy.Enforcement
                                  Message = message }

                            match policy.Enforcement with
                            | PolicyEnforcement.Block ->
                                blocked <- true

                                addEvidence
                                    PolicyEvaluationOutcome.Blocked
                                    None
                                    (digest (Some message))
                                    (digest effectiveInput)
                            | PolicyEnforcement.Confirm ->
                                requiresConfirmation <- true

                                addEvidence
                                    PolicyEvaluationOutcome.ConfirmationRequired
                                    None
                                    (digest (Some message))
                                    (digest effectiveInput)
                            | PolicyEnforcement.Warn ->
                                warnings.Add(sprintf "[Policy %s@%s]: %s" policy.Id policy.Version message)

                                addEvidence
                                    PolicyEvaluationOutcome.Warned
                                    None
                                    (digest (Some message))
                                    (digest effectiveInput)
                            | PolicyEnforcement.Modify transform ->
                                let transformed =
                                    try
                                        Ok(effectiveInput |> Option.map transform)
                                    with error when not (isFatalException error) ->
                                        Error(callbackFailure policy error)

                                match transformed with
                                | Error failure -> fail failure
                                | Ok value ->
                                    effectiveInput <- value
                                    inputModified <- effectiveInput.IsSome

                                    addEvidence
                                        PolicyEvaluationOutcome.Modified
                                        None
                                        (digest (Some message))
                                        (digest effectiveInput)
                        | Ok None -> addEvidence PolicyEvaluationOutcome.Passed None None inputDigest

                    return
                        result
                            (not blocked)
                            (violations |> Seq.toList)
                            (evaluations |> Seq.toList)
                            (if inputModified then effectiveInput else None)
                            (warnings |> Seq.toList)
                            requiresConfirmation
                } }

    let private requirePositive name value =
        if value <= 0 then
            invalidArg name "Policy limits must be positive."

    let private requireCanonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name "Policy values must be non-blank and have no surrounding whitespace."

    let private synchronousEvaluation evaluate context _ =
        evaluate context |> Ok |> Task.FromResult

    /// Blocks when observed execution cost exceeds the configured budget.
    let costBudgetPolicy id version scope (maxUsd: decimal) : Policy =
        if maxUsd <= 0m then
            invalidArg (nameof maxUsd) "Policy limits must be positive."

        { Id = id
          Version = version
          Description = sprintf "Enforce maximum cost budget of $%.2f" maxUsd
          Scope = scope
          Enforcement = PolicyEnforcement.Block
          EvaluateAsync =
            synchronousEvaluation (fun ctx ->
                match ctx.CurrentUsage with
                | Some usage when usage.EstimatedCostUsd > maxUsd ->
                    Some(sprintf "Cost budget exceeded: $%.4f > $%.2f" usage.EstimatedCostUsd maxUsd)
                | Some _ -> None
                | None -> Some "Cost policy requires current usage evidence.") }

    /// Blocks when observed token usage exceeds the configured budget.
    let totalTokenBudgetPolicy id version scope maxTokens : Policy =
        requirePositive (nameof maxTokens) maxTokens

        { Id = id
          Version = version
          Description = sprintf "Enforce maximum total token budget of %d" maxTokens
          Scope = scope
          Enforcement = PolicyEnforcement.Block
          EvaluateAsync =
            synchronousEvaluation (fun ctx ->
                match ctx.CurrentUsage with
                | Some usage when usage.TotalTokens > maxTokens ->
                    Some(sprintf "Token budget exceeded: %d > %d" usage.TotalTokens maxTokens)
                | Some _ -> None
                | None -> Some "Token policy requires current usage evidence.") }

    /// Process-local sliding-window limiter. Hosts needing distributed limits should supply a shared policy.
    let rateLimitPolicy id version scope (capability: string) maxPerMinute : Policy =
        requireCanonical (nameof capability) capability
        requirePositive (nameof maxPerMinute) maxPerMinute
        let sync = obj ()
        let timestamps = Collections.Generic.Queue<DateTimeOffset>()

        { Id = id
          Version = version
          Description = sprintf "Rate limit %s to %d per minute" capability maxPerMinute
          Scope = scope
          Enforcement = PolicyEnforcement.Block
          EvaluateAsync =
            synchronousEvaluation (fun ctx ->
                if ctx.Action = capability then
                    lock sync (fun () ->
                        let now = DateTimeOffset.UtcNow
                        let cutoff = now.AddMinutes(-1.0)

                        while timestamps.Count > 0 && timestamps.Peek() < cutoff do
                            timestamps.Dequeue() |> ignore

                        if timestamps.Count >= maxPerMinute then
                            Some(
                                sprintf
                                    "Rate limit exceeded: %d/%d calls in last minute"
                                    timestamps.Count
                                    maxPerMinute
                            )
                        else
                            timestamps.Enqueue(now)
                            None)
                else
                    None) }

    /// Requires labeled data and blocks classifications above the configured maximum.
    let dataClassificationPolicy id version scope maximum : Policy =
        let rank =
            function
            | DataClassification.Public -> 0
            | DataClassification.Internal -> 1
            | DataClassification.Confidential -> 2
            | DataClassification.Restricted -> 3

        { Id = id
          Version = version
          Description = sprintf "Allow data classifications through %A" maximum
          Scope = scope
          Enforcement = PolicyEnforcement.Block
          EvaluateAsync =
            synchronousEvaluation (fun ctx ->
                let classifications = PolicyFacts.classifications ctx.Facts

                if classifications.IsEmpty then
                    Some "Data classification is required."
                else
                    classifications
                    |> Seq.tryFind (fun classification -> rank classification > rank maximum)
                    |> Option.map (fun classification ->
                        sprintf "Data classification %A exceeds allowed maximum %A." classification maximum)) }

    /// Blocks declared HTTP(S) destinations outside the configured origin allowlist.
    let destinationPolicy id version scope (allowedDestinations: Uri seq) : Policy =
        let allowed =
            allowedDestinations |> Seq.map PolicyFacts.destinationOriginValue |> Set.ofSeq

        if allowed.IsEmpty then
            invalidArg (nameof allowedDestinations) "Destination policies require at least one allowed origin."

        { Id = id
          Version = version
          Description = "Restrict network destinations to approved origins"
          Scope = scope
          Enforcement = PolicyEnforcement.Block
          EvaluateAsync =
            synchronousEvaluation (fun ctx ->
                PolicyFacts.destinations ctx.Facts
                |> Seq.tryFind (fun destination -> not (Set.contains destination allowed))
                |> Option.map (sprintf "Destination is not eligible: %s")) }

    /// Blocks declared model targets outside the configured canonical model allowlist.
    let modelEligibilityPolicy id version scope allowedModels : Policy =
        let allowed = allowedModels |> Seq.map PolicyFacts.canonicalModelValue |> Set.ofSeq

        if allowed.IsEmpty then
            invalidArg (nameof allowedModels) "Model eligibility policies require at least one allowed model."

        { Id = id
          Version = version
          Description = "Restrict model execution to eligible models"
          Scope = scope
          Enforcement = PolicyEnforcement.Block
          EvaluateAsync =
            synchronousEvaluation (fun ctx ->
                PolicyFacts.models ctx.Facts
                |> Seq.tryFind (fun model -> not (Set.contains model allowed))
                |> Option.map (sprintf "Model is not eligible: %s")) }

    /// Content length policy: blocks excessively long outputs
    let maxOutputLengthPolicy id version scope (maxChars: int) : Policy =
        requirePositive (nameof maxChars) maxChars

        { Id = id
          Version = version
          Description = sprintf "Limit output to %d characters" maxChars
          Scope = scope
          Enforcement =
            PolicyEnforcement.Modify(fun s ->
                if s.Length > maxChars then
                    s.Substring(0, maxChars) + "... [truncated]"
                else
                    s)
          EvaluateAsync =
            synchronousEvaluation (fun ctx ->
                match ctx.Input with
                | Some input when input.Length > maxChars ->
                    Some(sprintf "Output exceeds maximum length: %d > %d" input.Length maxChars)
                | _ -> None) }
