namespace Nao.Agents.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type PermissionGateTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate
                principal
                None
                (WorkspaceId.parse "workspace")
                (Some(SessionId.parse "session"))
            |> Option.get

    let request allowedScopes =
        { Authorization = authorization
          Correlation = CorrelationContext.root ()
          Access = ResourceAccess.tool "dangerous"
          Reason = "Required for the requested operation."
          ForceConfirmation = false
          RequestedAt = DateTimeOffset.UtcNow
          AllowedGrantScopes = Set.ofList allowedScopes }

    let approved scope =
        PermissionApproval.Approved { Scope = scope; ExpiresAt = None }

    [<TestMethod>]
    member _.UnavailableBrokerDeniesAccess() =
        let outcome =
            PermissionGate.resolve
                PermissionApprovalBroker.unavailable
                (request [ PermissionGrantScope.Operation ])
                CancellationToken.None
            |> _.Result

        Assert.AreEqual(PermissionApproval.Denied, outcome)

    [<TestMethod>]
    member _.BrokerReceivesAuthenticatedRequestAndApprovedScope() =
        let mutable observed = None

        let broker =
            PermissionApprovalBroker.create (TimeSpan.FromSeconds 1.0) (fun permission _ ->
                observed <- Some permission
                Task.FromResult(approved PermissionGrantScope.Session))

        let expected =
            { request [ PermissionGrantScope.Operation; PermissionGrantScope.Session ] with
                ForceConfirmation = true }

        let outcome =
            PermissionGate.resolve broker expected CancellationToken.None |> _.Result

        Assert.AreEqual(approved PermissionGrantScope.Session, outcome)
        Assert.AreEqual(Some expected, observed)

    [<TestMethod>]
    member _.UnsupportedGrantScopeDeniesAccess() =
        let broker =
            PermissionApprovalBroker.create (TimeSpan.FromSeconds 1.0) (fun _ _ ->
                Task.FromResult(approved PermissionGrantScope.Durable))

        let outcome =
            PermissionGate.resolve
                broker
                (request [ PermissionGrantScope.Operation; PermissionGrantScope.Session ])
                CancellationToken.None
            |> _.Result

        Assert.AreEqual(PermissionApproval.Denied, outcome)

    [<TestMethod>]
    member _.BrokerTimeoutDeniesButCallerCancellationPropagates() =
        let broker =
            PermissionApprovalBroker.create (TimeSpan.FromMilliseconds 20.0) (fun _ cancellation ->
                task {
                    do! Task.Delay(TimeSpan.FromSeconds 5.0, cancellation)
                    return approved PermissionGrantScope.Operation
                })

        let permission = request [ PermissionGrantScope.Operation ]

        Assert.AreEqual(
            PermissionApproval.Denied,
            PermissionGate.resolve broker permission CancellationToken.None |> _.Result
        )

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        Assert.ThrowsExactly<TaskCanceledException>(fun () ->
            PermissionGate.resolve broker permission cancellation.Token
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type PermissionGrantStoreTests() =
    let principal =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []

    let scope workspace session =
        AuthorizationScope.tryCreate
            principal
            None
            (WorkspaceId.parse workspace)
            (session |> Option.map SessionId.parse)
        |> Option.get

    [<TestMethod>]
    member _.GrantScopesExpireAndRevokeWithoutCaching() =
        let store = InMemoryPermissionGrantStore.create ()
        let now = DateTimeOffset.UtcNow
        let current = scope "workspace" (Some "session")
        let otherSession = scope "workspace" (Some "other-session")
        let otherWorkspace = scope "other-workspace" (Some "session")

        let sessionGrant =
            PermissionGrant.create
                PermissionGrantScope.Session
                (ResourcePermission.targetForAccess (ResourceAccess.file FileAction.Read "/data"))
                now
                None

        let workspaceGrant =
            PermissionGrant.create
                PermissionGrantScope.Workspace
                (ResourcePermission.targetForAccess (ResourceAccess.tool "workspace-tool"))
                now
                None

        let durableGrant =
            PermissionGrant.create
                PermissionGrantScope.Durable
                (ResourcePermission.targetForAccess (ResourceAccess.model "durable-model"))
                now
                (Some(now.AddMinutes 1.0))

        store.SaveAsync current sessionGrant |> _.Wait()
        store.SaveAsync current workspaceGrant |> _.Wait()
        store.SaveAsync current durableGrant |> _.Wait()

        Assert.AreEqual(3, store.GetActiveAsync current now |> _.Result.Length)
        Assert.AreEqual(2, store.GetActiveAsync otherSession now |> _.Result.Length)
        Assert.AreEqual(1, store.GetActiveAsync otherWorkspace now |> _.Result.Length)
        Assert.AreEqual(2, store.GetActiveAsync current (now.AddMinutes 1.0) |> _.Result.Length)

        Assert.IsTrue(store.RevokeAsync current workspaceGrant.Id (now.AddSeconds 1.0) |> _.Result)
        Assert.AreEqual(2, store.GetActiveAsync current (now.AddSeconds 2.0) |> _.Result.Length)
        Assert.IsFalse(store.RevokeAsync current workspaceGrant.Id (now.AddSeconds 3.0) |> _.Result)
        Assert.AreEqual(1, store.DeleteScopeAsync current PermissionGrantScope.Session |> _.Result)
        Assert.AreEqual(0, store.GetActiveAsync current (now.AddMinutes 2.0) |> _.Result.Length)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            store.SaveAsync
                current
                { sessionGrant with
                    Version = PermissionGrant.currentVersion + 1 }
            |> _.GetAwaiter().GetResult())
        |> ignore

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type ConstitutionTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "constitution-user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let context output =
        ConstitutionContext.create authorization "constitution-agent" output

    [<TestMethod>]
    member _.EmptyConstitutionPassesAll() =
        let constitution = Constitution.create "test" "test-v1"
        let result = Constitution.checkDeterministic constitution (context "any content")
        Assert.IsTrue(result.Passed)
        Assert.AreEqual(0, result.Violations.Length)

    [<TestMethod>]
    member _.AddRuleEnforcesCheck() =
        let rule =
            DeterministicConstitutionRule.create
                "no-profanity"
                "No bad words"
                RuleCategory.Safety
                50
                RuleSeverity.Error
                PolicyScope.platform
                ConstitutionRemediation.Reject
                "test-validator"
                "test-v1"
                Map.empty
                (fun content -> content.Contains("badword"))
            |> ConstitutionRule.Deterministic

        let constitution = Constitution.create "test" "test-v1" |> Constitution.addRule rule

        let resultClean =
            Constitution.checkDeterministic constitution (context "this is fine")

        Assert.IsTrue(resultClean.Passed)

        let resultBad =
            Constitution.checkDeterministic constitution (context "this has badword in it")

        Assert.IsFalse(resultBad.Passed)
        Assert.AreEqual(1, resultBad.Violations.Length)
        Assert.AreEqual("no-profanity", resultBad.Violations.[0].RuleId)
        Assert.AreEqual(RuleSeverity.Error, resultBad.Violations.[0].Severity)
        Assert.AreEqual(ConstitutionRemediation.Reject, resultBad.Violations.[0].Remediation)

        let evidence = resultBad.Evaluations |> List.exactlyOne
        Assert.AreEqual(ConstitutionEvaluationOutcome.Violated, evidence.Outcome)
        Assert.AreEqual(PolicyScopeKind.Platform, evidence.Scope)
        Assert.AreEqual(RuleSeverity.Error, evidence.RuleSeverity)
        Assert.AreEqual(ConstitutionRemediation.Reject, evidence.Remediation)
        Assert.AreEqual("test-v1", evidence.ConstitutionVersion)
        Assert.AreEqual("test-validator", evidence.EvaluatorId)
        Assert.AreEqual("test-v1", evidence.EvaluatorVersion)
        Assert.AreEqual(64, evidence.OutputDigest.Length)

    [<TestMethod>]
    member _.ErrorSeverityRequiresBlocking() =
        let hardRule =
            DeterministicConstitutionRule.create
                "hard"
                "Hard"
                RuleCategory.Safety
                100
                RuleSeverity.Error
                PolicyScope.platform
                ConstitutionRemediation.Reject
                "test-validator"
                "test-v1"
                Map.empty
                (fun _ -> true)
            |> ConstitutionRule.Deterministic

        let constitution =
            Constitution.create "test" "test-v1" |> Constitution.addRule hardRule

        let result = Constitution.checkDeterministic constitution (context "anything")
        Assert.IsTrue(Constitution.hasBlockingViolations result)

    [<TestMethod>]
    member _.SoftViolationDoesNotBlockHard() =
        let softRule =
            DeterministicConstitutionRule.create
                "soft"
                "Soft"
                RuleCategory.Behavioral
                10
                RuleSeverity.Warning
                PolicyScope.platform
                ConstitutionRemediation.Observe
                "test-validator"
                "test-v1"
                Map.empty
                (fun _ -> true)
            |> ConstitutionRule.Deterministic

        let constitution =
            Constitution.create "test" "test-v1" |> Constitution.addRule softRule

        let result = Constitution.checkDeterministic constitution (context "anything")
        Assert.IsFalse(result.Passed) // has violations
        Assert.IsFalse(Constitution.hasBlockingViolations result)

    [<TestMethod>]
    member _.NoPrivateDataRuleDetectsEmail() =
        let constitution =
            Constitution.create "test" "test-v1"
            |> Constitution.addRule Constitution.noPrivateDataRule

        let resultWithEmail =
            Constitution.checkDeterministic constitution (context "Contact me at user@example.com")

        Assert.IsFalse(resultWithEmail.Passed)

    [<TestMethod>]
    member _.NoPrivateDataRuleDetectsPhone() =
        let constitution =
            Constitution.create "test" "test-v1"
            |> Constitution.addRule Constitution.noPrivateDataRule

        let resultWithPhone =
            Constitution.checkDeterministic constitution (context "Call me at 555-123-4567")

        Assert.IsFalse(resultWithPhone.Passed)

    [<TestMethod>]
    member _.RenderForPromptIncludesRules() =
        let rule =
            DeterministicConstitutionRule.create
                "r1"
                "Be polite"
                RuleCategory.Behavioral
                50
                RuleSeverity.Warning
                PolicyScope.platform
                ConstitutionRemediation.Observe
                "test-validator"
                "test-v1"
                Map.empty
                (fun _ -> false)
            |> ConstitutionRule.Deterministic

        let constitution =
            Constitution.create "Politeness" "test-v1"
            |> Constitution.withPreamble "Always be kind"
            |> Constitution.addRule rule

        let rendered = Constitution.renderForPrompt constitution
        Assert.IsTrue(rendered.Contains("Politeness"))
        Assert.IsTrue(rendered.Contains("Always be kind"))
        Assert.IsTrue(rendered.Contains("[Warning/Observe] Be polite"))

    [<TestMethod>]
    member _.DeterministicValidationNeverInvokesProbabilisticJudges() =
        let mutable judgeInvoked = false

        let deterministic =
            DeterministicConstitutionRule.create
                "deterministic"
                "Pinned deterministic check"
                RuleCategory.Format
                100
                RuleSeverity.Error
                PolicyScope.platform
                ConstitutionRemediation.Reject
                "test-validator"
                "test-v1"
                (Map [ "validator", "2.1.0" ])
                (fun content -> content <> "valid")
            |> ConstitutionRule.Deterministic

        let probabilistic =
            ProbabilisticConstitutionRule.create
                "probabilistic"
                "Model judge"
                RuleCategory.Safety
                90
                RuleSeverity.Error
                PolicyScope.platform
                ConstitutionRemediation.Reject
                "test-judge"
                "test-v1"
                "safety-judge"
                "prompt-v3"
                0.8
                (fun _ _ ->
                    judgeInvoked <- true

                    Task.FromResult { Score = 1.0; Rationale = "unsafe" })
            |> ConstitutionRule.Probabilistic

        let result =
            Constitution.create "separated" "test-v1"
            |> Constitution.addRule probabilistic
            |> Constitution.addRule deterministic
            |> fun constitution -> Constitution.checkDeterministic constitution (context "invalid")

        Assert.IsFalse(result.Passed)
        Assert.IsFalse(judgeInvoked)
        Assert.AreEqual([ "deterministic" ], result.Violations |> List.map _.RuleId)

        match result.Evaluations.Head.Evaluator with
        | ConstitutionEvaluatorEvidence.Deterministic dependencies ->
            Assert.AreEqual(Some "2.1.0", dependencies |> Map.tryFind "validator")
        | evidence -> Assert.Fail($"Expected deterministic evidence, got {evidence}.")

        let completeResult =
            Constitution.create "separated" "test-v1"
            |> Constitution.addRule probabilistic
            |> Constitution.addRule deterministic
            |> fun constitution ->
                Constitution.evaluateAsync constitution (context "invalid") CancellationToken.None
                |> _.Result

        Assert.IsFalse(completeResult.Passed)
        Assert.IsFalse(judgeInvoked)

    [<TestMethod>]
    member _.DeterministicDecisionIsReproducibleWithPinnedEvidence() =
        let rule =
            DeterministicConstitutionRule.create
                "format"
                "Reject malformed output"
                RuleCategory.Format
                100
                RuleSeverity.Error
                PolicyScope.platform
                ConstitutionRemediation.Reject
                "schema-validator"
                "3"
                (Map [ "json-schema", "2020-12"; "validator-library", "8.4.0" ])
                (fun output -> output <> """{"valid":true}""")
            |> ConstitutionRule.Deterministic

        let constitution =
            Constitution.create "reproducible" "7" |> Constitution.addRule rule

        let first =
            Constitution.checkDeterministic constitution (context """{"valid":false}""")

        let second =
            Constitution.checkDeterministic constitution (context """{"valid":false}""")

        let firstEvidence = first.Evaluations.Head
        let secondEvidence = second.Evaluations.Head

        Assert.AreEqual(first.Passed, second.Passed)
        Assert.AreEqual(firstEvidence.ConstitutionName, secondEvidence.ConstitutionName)
        Assert.AreEqual(firstEvidence.ConstitutionVersion, secondEvidence.ConstitutionVersion)
        Assert.AreEqual(firstEvidence.RuleId, secondEvidence.RuleId)
        Assert.AreEqual(firstEvidence.EvaluatorId, secondEvidence.EvaluatorId)
        Assert.AreEqual(firstEvidence.EvaluatorVersion, secondEvidence.EvaluatorVersion)
        Assert.AreEqual(firstEvidence.Outcome, secondEvidence.Outcome)
        Assert.AreEqual(firstEvidence.OutputDigest, secondEvidence.OutputDigest)
        Assert.AreEqual(firstEvidence.Evaluator, secondEvidence.Evaluator)

    [<TestMethod>]
    member _.ProbabilisticJudgementRecordsPinnedDecisionInputs() =
        let rule =
            ProbabilisticConstitutionRule.create
                "toxicity"
                "Detect toxic content"
                RuleCategory.Safety
                100
                RuleSeverity.Error
                PolicyScope.platform
                ConstitutionRemediation.Reject
                "test-judge"
                "test-v1"
                "judge-model-v2"
                "toxicity-prompt-v4"
                0.75
                (fun content _ ->
                    Assert.AreEqual("unsafe output", content)

                    Task.FromResult
                        { Score = 0.9
                          Rationale = "Threatening language detected." })
            |> ConstitutionRule.Probabilistic

        let result =
            Constitution.create "judged" "test-v1"
            |> Constitution.addRule rule
            |> fun constitution ->
                Constitution.evaluateAsync constitution (context "unsafe output") CancellationToken.None
                |> _.Result

        Assert.IsFalse(result.Passed)

        match result.Evaluations.Head.Evaluator with
        | ConstitutionEvaluatorEvidence.Probabilistic(model, promptVersion, threshold, score, rationaleDigest) ->
            Assert.AreEqual("judge-model-v2", model)
            Assert.AreEqual("toxicity-prompt-v4", promptVersion)
            Assert.AreEqual(0.75, threshold)
            Assert.AreEqual(0.9, score)
            Assert.AreEqual(64, rationaleDigest.Length)
        | evidence -> Assert.Fail($"Expected probabilistic evidence, got {evidence}.")

    [<TestMethod>]
    member _.ProbabilisticJudgementPropagatesCallerCancellation() =
        let rule =
            ProbabilisticConstitutionRule.create
                "slow-judge"
                "Waits for cancellation"
                RuleCategory.Safety
                100
                RuleSeverity.Error
                PolicyScope.platform
                ConstitutionRemediation.Reject
                "test-judge"
                "test-v1"
                "judge-model"
                "prompt-v1"
                0.5
                (fun _ cancellationToken ->
                    task {
                        do! Task.Delay(TimeSpan.FromSeconds 5.0, cancellationToken)

                        return { Score = 0.0; Rationale = "safe" }
                    })
            |> ConstitutionRule.Probabilistic

        let constitution =
            Constitution.create "cancelled" "test-v1" |> Constitution.addRule rule

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        Assert.ThrowsExactly<TaskCanceledException>(fun () ->
            Constitution.evaluateAsync constitution (context "output") cancellation.Token
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.ProbabilisticRuleRejectsInvalidThresholdAndScore() =
        let create threshold score =
            ProbabilisticConstitutionRule.create
                "judge"
                "Judge"
                RuleCategory.Safety
                100
                RuleSeverity.Error
                PolicyScope.platform
                ConstitutionRemediation.Reject
                "test-judge"
                "test-v1"
                "model"
                "prompt-v1"
                threshold
                (fun _ _ ->
                    Task.FromResult
                        { Score = score
                          Rationale = "evidence" })
            |> ConstitutionRule.Probabilistic

        Assert.ThrowsExactly<ArgumentException>(fun () -> create 1.1 0.0 |> ignore)
        |> ignore

        let constitution =
            Constitution.create "invalid-score" "test-v1"
            |> Constitution.addRule (create 0.5 -0.1)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            Constitution.evaluateAsync constitution (context "output") CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.OnlyRulesMatchingAuthenticatedScopeAreEvaluated() =
        let mutable foreignEvaluated = false
        let tenantId = AuthorizationScope.tenantId authorization
        let workspaceId = AuthorizationScope.workspaceId authorization

        let rule id scope onEvaluate =
            DeterministicConstitutionRule.create
                id
                id
                RuleCategory.Safety
                100
                RuleSeverity.Error
                scope
                ConstitutionRemediation.Reject
                "test-validator"
                "test-v1"
                Map.empty
                (fun _ ->
                    onEvaluate ()
                    true)
            |> ConstitutionRule.Deterministic

        let constitution =
            Constitution.create "scoped" "test-v1"
            |> Constitution.addRule (
                rule "matching" (PolicyScope.agent tenantId None workspaceId "constitution-agent") ignore
            )
            |> Constitution.addRule (
                rule "foreign" (PolicyScope.tenant (TenantId.parse "foreign-tenant")) (fun () ->
                    foreignEvaluated <- true)
            )

        let result = Constitution.checkDeterministic constitution (context "output")

        Assert.AreEqual([ "matching" ], result.Violations |> List.map _.RuleId)
        Assert.AreEqual([ "matching" ], result.Evaluations |> List.map _.RuleId)
        Assert.IsFalse(foreignEvaluated)

    [<TestMethod>]
    member _.RemediationResolutionUsesMostRestrictiveOutcome() =
        let violation remediation =
            let evidence =
                { Id = Guid.NewGuid()
                  ConstitutionName = "test"
                  ConstitutionVersion = "1.0"
                  RuleId = string remediation
                  RuleCategory = RuleCategory.Safety
                  RuleSeverity = RuleSeverity.Error
                  Scope = PolicyScopeKind.Platform
                  Remediation = remediation
                  EvaluatorId = "test-validator"
                  EvaluatorVersion = "test-v1"
                  Outcome = ConstitutionEvaluationOutcome.Violated
                  Evaluator = ConstitutionEvaluatorEvidence.Deterministic Map.empty
                  OutputDigest = String.replicate 64 "a"
                  EvaluatedAt = DateTimeOffset.UtcNow }

            { RuleId = evidence.RuleId
              RuleDescription = evidence.RuleId
              Severity = evidence.RuleSeverity
              Remediation = remediation
              Evidence = evidence }

        let selected =
            [ ConstitutionRemediation.Redact
              ConstitutionRemediation.Repair
              ConstitutionRemediation.Reject
              ConstitutionRemediation.Escalate
              ConstitutionRemediation.Quarantine ]
            |> List.map violation
            |> Constitution.requiredRemediation

        Assert.AreEqual(ConstitutionRemediation.Quarantine, selected)

    [<TestMethod>]
    member _.BlockingSeverityCannotUseObserveRemediation() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            DeterministicConstitutionRule.create
                "invalid"
                "invalid"
                RuleCategory.Safety
                1
                RuleSeverity.Critical
                PolicyScope.platform
                ConstitutionRemediation.Observe
                "test-validator"
                "test-v1"
                Map.empty
                (fun _ -> false)
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.ConstitutionAndEvaluatorVersionsAreMandatory() =
        Assert.ThrowsExactly<ArgumentException>(fun () -> Constitution.create "safety" " " |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            DeterministicConstitutionRule.create
                "rule"
                "rule"
                RuleCategory.Safety
                1
                RuleSeverity.Error
                PolicyScope.platform
                ConstitutionRemediation.Reject
                "validator"
                " "
                Map.empty
                (fun _ -> false)
            |> ignore)
        |> ignore

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type AuditLogTests() =

    let agentId = "test"

    let scope =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "audit-user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    [<TestMethod>]
    member _.RecordAndQuery() =
        let audit = InMemory.auditLog ()

        let entry =
            AuditLog.toolInvocation agentId "search" "query" "results" true PermissionDecision.Allow None

        audit.RecordAsync scope entry |> _.Wait()

        let results =
            audit.QueryAsync scope agentId (DateTimeOffset.UtcNow.AddHours(-1.0))
            |> _.Result

        Assert.AreEqual(1, results.Length)
        Assert.AreEqual(agentId, results.[0].AgentId)

    [<TestMethod>]
    member _.QueryByExecutionFiltersCorrectly() =
        let audit = InMemory.auditLog ()
        let execId = ExecutionId.generate ()
        let entry1 = AuditLog.llmCall agentId "gpt-4" (Some execId)
        let entry2 = AuditLog.llmCall agentId "gpt-4" (Some(ExecutionId.generate ()))
        audit.RecordAsync scope entry1 |> _.Wait()
        audit.RecordAsync scope entry2 |> _.Wait()
        let results = audit.QueryByExecutionAsync scope execId |> _.Result
        Assert.AreEqual(1, results.Length)

    [<TestMethod>]
    member _.GetDeniedCountFiltersDenied() =
        let audit = InMemory.auditLog ()

        let permitted =
            AuditLog.toolInvocation agentId "t1" "" "" true PermissionDecision.Allow None

        let denied =
            { AuditLog.toolInvocation agentId "t2" "" "" false PermissionDecision.Deny None with
                Permitted = false }

        audit.RecordAsync scope permitted |> _.Wait()
        audit.RecordAsync scope denied |> _.Wait()

        let count =
            audit.GetDeniedCountAsync scope agentId (DateTimeOffset.UtcNow.AddHours(-1.0))
            |> _.Result

        Assert.AreEqual(1, count)

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type PolicyEngineTests() =

    let agentId = "test"

    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate
                principal
                None
                (WorkspaceId.parse "workspace")
                (Some(SessionId.parse "session"))
            |> Option.get

    let context toolName =
        { Authorization = authorization
          AgentId = agentId
          Stage = PolicyStage.PreAction
          ToolName = toolName
          Action = "execute"
          Input = Some ""
          Facts = PolicyFacts.empty
          ExecutionId = None
          CurrentUsage = None }

    let evaluate (engine: PolicyEngine) policyContext =
        engine.EvaluateAsync(policyContext, CancellationToken.None).Result

    let modifyingPolicy id scope suffix (observedInputs: ResizeArray<string> option) =
        { Id = id
          Version = "test-v1"
          Description = id
          Scope = scope
          Enforcement = PolicyEnforcement.Modify(fun input -> input + suffix)
          EvaluateAsync =
            Policy.evaluateSynchronously (fun (policyContext: PolicyContext) ->
                observedInputs
                |> Option.iter (fun inputs -> inputs.Add policyContext.Input.Value)

                Some id) }

    [<TestMethod>]
    member _.NoPoliciesAllowsAll() =
        let engine = PolicyEngine.create (TimeSpan.FromSeconds 1.0) []

        let ctx =
            { Authorization = authorization
              AgentId = agentId
              Stage = PolicyStage.PreInput
              ToolName = None
              Action = "execute"
              Input = Some "hello"
              Facts = PolicyFacts.empty
              ExecutionId = None
              CurrentUsage = None }

        let result = evaluate engine ctx
        Assert.IsTrue(result.Proceed)
        Assert.AreEqual(None, result.ModifiedInput)
        Assert.AreEqual(0, result.Violations.Length)

    [<TestMethod>]
    member _.CostBudgetPolicyBlocksWhenExceeded() =
        let policy =
            PolicyEngine.costBudgetPolicy "cost-budget" "1" PolicyScope.platform 1.0m

        let engine = PolicyEngine.create (TimeSpan.FromSeconds 1.0) [ policy ]

        let usage =
            { ResourceUsage.Zero with
                EstimatedCostUsd = 1.5m }

        let ctx =
            { Authorization = authorization
              AgentId = agentId
              Stage = PolicyStage.PreInput
              ToolName = None
              Action = "execute"
              Input = None
              Facts = PolicyFacts.empty
              ExecutionId = None
              CurrentUsage = Some usage }

        let result = evaluate engine ctx
        Assert.IsFalse(result.Proceed)
        Assert.AreEqual(1, result.Violations.Length)
        Assert.IsTrue(result.Violations.[0].Message.Contains("Cost budget exceeded"))

    [<TestMethod>]
    member _.CostBudgetPolicyAllowsWithinBudget() =
        let policy =
            PolicyEngine.costBudgetPolicy "cost-budget" "1" PolicyScope.platform 10.0m

        let engine = PolicyEngine.create (TimeSpan.FromSeconds 1.0) [ policy ]

        let usage =
            { ResourceUsage.Zero with
                EstimatedCostUsd = 5.0m }

        let ctx =
            { Authorization = authorization
              AgentId = agentId
              Stage = PolicyStage.PreInput
              ToolName = None
              Action = "execute"
              Input = None
              Facts = PolicyFacts.empty
              ExecutionId = None
              CurrentUsage = Some usage }

        let result = evaluate engine ctx
        Assert.IsTrue(result.Proceed)

    [<TestMethod>]
    member _.TokenAndRatePoliciesEnforceTypedUsageAndActionLimits() =
        let tokenPolicy =
            PolicyEngine.totalTokenBudgetPolicy "tokens" "budget-v2" PolicyScope.platform 100

        let ratePolicy =
            PolicyEngine.rateLimitPolicy "rate" "window-v3" PolicyScope.platform "execute" 1

        let usage =
            { ResourceUsage.Zero with
                TotalTokens = 101 }

        let tokenResult =
            PolicyEngine.create (TimeSpan.FromSeconds 1.0) [ tokenPolicy ]
            |> fun engine ->
                evaluate
                    engine
                    { context None with
                        CurrentUsage = Some usage }

        Assert.IsFalse(tokenResult.Proceed)
        Assert.AreEqual("budget-v2", tokenResult.Evaluations.Head.PolicyVersion)

        let rateEngine = PolicyEngine.create (TimeSpan.FromSeconds 1.0) [ ratePolicy ]
        Assert.IsTrue((evaluate rateEngine (context None)).Proceed)
        Assert.IsFalse((evaluate rateEngine (context None)).Proceed)

    [<TestMethod>]
    member _.ClassificationDestinationAndModelPoliciesUseTypedFacts() =
        let classificationPolicy =
            PolicyEngine.dataClassificationPolicy
                "classification"
                "classification-v1"
                PolicyScope.platform
                DataClassification.Confidential

        let destinationPolicy =
            PolicyEngine.destinationPolicy
                "destination"
                "destination-v1"
                PolicyScope.platform
                [ Uri "https://api.example.com/allowed" ]

        let modelPolicy =
            PolicyEngine.modelEligibilityPolicy "model" "model-v1" PolicyScope.platform [ "approved-model" ]

        let evaluate facts policies =
            PolicyEngine.create (TimeSpan.FromSeconds 1.0) policies
            |> fun engine -> evaluate engine { context None with Facts = facts }

        let approvedFacts =
            PolicyFacts.create
                [ DataClassification.Confidential ]
                [ Uri "https://api.example.com/another-path" ]
                [ "approved-model" ]

        Assert.IsTrue((evaluate approvedFacts [ classificationPolicy; destinationPolicy; modelPolicy ]).Proceed)
        Assert.IsFalse((evaluate PolicyFacts.empty [ classificationPolicy ]).Proceed)

        let deniedFacts =
            PolicyFacts.create
                [ DataClassification.Restricted ]
                [ Uri "https://other.example.com/path" ]
                [ "unapproved-model" ]

        let denied =
            evaluate deniedFacts [ classificationPolicy; destinationPolicy; modelPolicy ]

        Assert.IsFalse(denied.Proceed)

        CollectionAssert.AreEquivalent(
            [| "classification"; "destination"; "model" |],
            denied.Violations |> List.map _.PolicyId |> List.toArray
        )

    [<TestMethod>]
    member _.BuiltInPoliciesRejectInvalidConfiguration() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            PolicyEngine.totalTokenBudgetPolicy "tokens" "1" PolicyScope.platform 0
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            PolicyEngine.rateLimitPolicy "rate" "1" PolicyScope.platform "execute" 0
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            PolicyEngine.destinationPolicy "destination" "1" PolicyScope.platform []
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            PolicyEngine.modelEligibilityPolicy "model" "1" PolicyScope.platform []
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.MaxOutputLengthPolicyModifiesContent() =
        let policy =
            PolicyEngine.maxOutputLengthPolicy "max-output-length" "1" PolicyScope.platform 10

        let engine = PolicyEngine.create (TimeSpan.FromSeconds 1.0) [ policy ]

        let ctx =
            { Authorization = authorization
              AgentId = agentId
              Stage = PolicyStage.PostOutput
              ToolName = None
              Action = "output"
              Input = Some "this is way too long for the limit"
              Facts = PolicyFacts.empty
              ExecutionId = None
              CurrentUsage = None }

        let result = evaluate engine ctx
        // Modify enforcement doesn't block
        Assert.IsTrue(result.Proceed)
        Assert.IsTrue(result.ModifiedInput.IsSome)
        Assert.IsTrue(result.ModifiedInput.Value.Length <= 30) // truncated + "... [truncated]"

        let evidence = result.Evaluations |> List.exactlyOne
        Assert.AreEqual("max-output-length", evidence.PolicyId)
        Assert.AreEqual("1", evidence.PolicyVersion)
        Assert.AreEqual(PolicyEvaluationOutcome.Modified, evidence.Outcome)
        Assert.AreNotEqual(evidence.InputDigest, evidence.EffectiveInputDigest)
        Assert.AreEqual(None, evidence.Confirmation)

    [<TestMethod>]
    member _.MultiplePoliciesEvaluatedInOrder() =
        let warnPolicy =
            { Id = "warn-test"
              Version = "test-v1"
              Description = "Always warns"
              Scope = PolicyScope.platform
              Enforcement = PolicyEnforcement.Warn
              EvaluateAsync = Policy.evaluateSynchronously (fun _ -> Some "just a warning") }

        let blockPolicy =
            { Id = "block-test"
              Version = "test-v1"
              Description = "Always blocks"
              Scope = PolicyScope.platform
              Enforcement = PolicyEnforcement.Block
              EvaluateAsync = Policy.evaluateSynchronously (fun _ -> Some "blocked") }

        let engine =
            PolicyEngine.create (TimeSpan.FromSeconds 1.0) [ warnPolicy; blockPolicy ]

        let ctx =
            { Authorization = authorization
              AgentId = agentId
              Stage = PolicyStage.PreAction
              ToolName = None
              Action = "x"
              Input = None
              Facts = PolicyFacts.empty
              ExecutionId = None
              CurrentUsage = None }

        let result = evaluate engine ctx
        Assert.IsFalse(result.Proceed)
        Assert.AreEqual(2, result.Violations.Length)
        Assert.AreEqual(1, result.Warnings.Length)

    [<TestMethod>]
    member _.MatchingScopesTransformBroadToNarrowAndPreserveDeclarationOrder() =
        let tenantId = AuthorizationScope.tenantId authorization
        let groupId = AuthorizationScope.groupId authorization
        let workspaceId = AuthorizationScope.workspaceId authorization
        let observedInputs = ResizeArray<string>()
        let observed = Some observedInputs

        let policies =
            [ modifyingPolicy "session" (PolicyScope.session authorization) "S" observed
              modifyingPolicy "platform-1" PolicyScope.platform "P1" observed
              modifyingPolicy "tool" (PolicyScope.tool tenantId groupId workspaceId agentId "search") "O" observed
              modifyingPolicy "tenant" (PolicyScope.tenant tenantId) "T" observed
              modifyingPolicy "platform-2" PolicyScope.platform "P2" observed
              modifyingPolicy "agent" (PolicyScope.agent tenantId groupId workspaceId agentId) "A" observed
              modifyingPolicy "workspace" (PolicyScope.workspace tenantId groupId workspaceId) "W" observed ]

        let result =
            PolicyEngine.create (TimeSpan.FromSeconds 1.0) policies
            |> fun engine -> evaluate engine (context (Some "search"))

        Assert.IsTrue(result.Proceed)
        Assert.AreEqual(Some "P1P2TWAOS", result.ModifiedInput)

        CollectionAssert.AreEqual(
            [| ""; "P1"; "P1P2"; "P1P2T"; "P1P2TW"; "P1P2TWA"; "P1P2TWAO" |],
            observedInputs.ToArray()
        )

    [<TestMethod>]
    member _.ForeignAndInapplicableScopesDoNotEvaluate() =
        let tenantId = TenantId.parse "foreign-tenant"
        let workspaceId = WorkspaceId.parse "foreign-workspace"
        let mutable evaluations = 0

        let policy id scope =
            { Id = id
              Version = "test-v1"
              Description = id
              Scope = scope
              Enforcement = PolicyEnforcement.Block
              EvaluateAsync =
                Policy.evaluateSynchronously (fun _ ->
                    evaluations <- evaluations + 1
                    Some id) }

        let foreignAuthorization =
            SecurityPrincipal.create tenantId (UserId.parse "foreign-user") []
            |> fun principal ->
                AuthorizationScope.tryCreate principal None workspaceId (Some(SessionId.parse "foreign-session"))
                |> Option.get

        let policies =
            [ policy "tenant" (PolicyScope.tenant tenantId)
              policy "workspace" (PolicyScope.workspace tenantId None workspaceId)
              policy "agent" (PolicyScope.agent tenantId None workspaceId agentId)
              policy "tool" (PolicyScope.tool tenantId None workspaceId agentId "search")
              policy "session" (PolicyScope.session foreignAuthorization)
              policy
                  "tool-without-tool-context"
                  (PolicyScope.tool
                      (AuthorizationScope.tenantId authorization)
                      (AuthorizationScope.groupId authorization)
                      (AuthorizationScope.workspaceId authorization)
                      agentId
                      "search") ]

        let result =
            PolicyEngine.create (TimeSpan.FromSeconds 1.0) policies
            |> fun engine -> engine.EvaluateAsync(context None, CancellationToken.None).Result

        Assert.IsTrue(result.Proceed)
        Assert.AreEqual(0, evaluations)
        Assert.AreEqual(0, result.Violations.Length)

    [<TestMethod>]
    member _.BlockCannotBeOverriddenAcrossScopeLevels() =
        let tenantId = AuthorizationScope.tenantId authorization
        let groupId = AuthorizationScope.groupId authorization
        let workspaceId = AuthorizationScope.workspaceId authorization

        let block id scope =
            { Id = id
              Version = "test-v1"
              Description = id
              Scope = scope
              Enforcement = PolicyEnforcement.Block
              EvaluateAsync = Policy.evaluateSynchronously (fun _ -> Some id) }

        let narrowerPolicies =
            [ modifyingPolicy "workspace-modify" (PolicyScope.workspace tenantId groupId workspaceId) "W" None
              { Id = "agent-confirm"
                Version = "test-v1"
                Description = "agent-confirm"
                Scope = PolicyScope.agent tenantId groupId workspaceId agentId
                Enforcement = PolicyEnforcement.Confirm
                EvaluateAsync = Policy.evaluateSynchronously (fun _ -> Some "confirm") } ]

        let platformBlocked =
            PolicyEngine.create
                (TimeSpan.FromSeconds 1.0)
                (block "platform-block" PolicyScope.platform :: narrowerPolicies)
            |> fun engine -> evaluate engine (context None)

        let sessionBlocked =
            PolicyEngine.create
                (TimeSpan.FromSeconds 1.0)
                (narrowerPolicies @ [ block "session-block" (PolicyScope.session authorization) ])
            |> fun engine -> evaluate engine (context None)

        Assert.IsFalse(platformBlocked.Proceed)
        Assert.IsTrue(platformBlocked.RequiresConfirmation)
        Assert.AreEqual(Some "W", platformBlocked.ModifiedInput)
        Assert.IsFalse(sessionBlocked.Proceed)
        Assert.IsTrue(sessionBlocked.RequiresConfirmation)
        Assert.AreEqual(Some "W", sessionBlocked.ModifiedInput)

    [<TestMethod>]
    member _.InvalidPolicyScopeAndDuplicateIdentityAreRejected() =
        let tenantId = AuthorizationScope.tenantId authorization
        let groupId = AuthorizationScope.groupId authorization
        let workspaceId = AuthorizationScope.workspaceId authorization
        let duplicate = modifyingPolicy "duplicate" PolicyScope.platform "x" None

        Assert.ThrowsExactly<ArgumentException>(fun () -> PolicyScope.agent tenantId groupId workspaceId " " |> ignore)
        |> ignore

        let sessionlessAuthorization =
            SecurityPrincipal.create tenantId (UserId.parse "user") []
            |> fun principal -> AuthorizationScope.tryCreate principal None workspaceId None |> Option.get

        Assert.ThrowsExactly<ArgumentException>(fun () -> PolicyScope.session sessionlessAuthorization |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            PolicyEngine.create (TimeSpan.FromSeconds 1.0) [ duplicate; duplicate ]
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            PolicyEngine.create
                (TimeSpan.FromSeconds 1.0)
                [ { duplicate with
                      Id = "blank-version"
                      Version = " " } ]
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> PolicyEngine.create TimeSpan.Zero [ duplicate ] |> ignore)
        |> ignore

    [<TestMethod>]
    member _.EvaluationFailuresAlwaysBlockRegardlessOfDeclaredEnforcement() =
        let failing id enforcement =
            { Id = id
              Version = "test-v1"
              Description = id
              Scope = PolicyScope.platform
              Enforcement = enforcement
              EvaluateAsync =
                fun _ _ ->
                    Task.FromResult(
                        Error(PolicyEvaluationFailure.create "backend_unavailable" "Policy backend unavailable.")
                    ) }

        let policies =
            [ failing "warn-failure" PolicyEnforcement.Warn
              failing "confirm-failure" PolicyEnforcement.Confirm
              failing "modify-failure" (PolicyEnforcement.Modify id) ]

        let result =
            PolicyEngine.create (TimeSpan.FromSeconds 1.0) policies
            |> fun engine -> evaluate engine (context None)

        Assert.IsFalse(result.Proceed)
        Assert.AreEqual(3, result.Violations.Length)

        Assert.IsTrue(
            result.Violations
            |> List.forall (fun violation ->
                match violation.Enforcement with
                | PolicyEnforcement.Block -> true
                | _ -> false)
        )

        Assert.IsTrue(
            result.Evaluations
            |> List.forall (fun evidence -> evidence.Outcome = PolicyEvaluationOutcome.Failed)
        )

        Assert.IsTrue(
            result.Evaluations
            |> List.forall (fun evidence -> evidence.FailureCode = Some "backend_unavailable")
        )

    [<TestMethod>]
    member _.EvaluationTimeoutCancelsCallbackAndFailsClosed() : Task =
        task {
            let callbackCancelled =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let policy =
                { Id = "slow-policy"
                  Version = "test-v1"
                  Description = "Never completes within the deadline"
                  Scope = PolicyScope.platform
                  Enforcement = PolicyEnforcement.Warn
                  EvaluateAsync =
                    fun _ cancellationToken ->
                        task {
                            try
                                do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                                return Ok None
                            with :? OperationCanceledException ->
                                callbackCancelled.TrySetResult() |> ignore
                                return raise (OperationCanceledException(cancellationToken))
                        } }

            let engine = PolicyEngine.create (TimeSpan.FromMilliseconds 20.0) [ policy ]
            let! result = engine.EvaluateAsync(context None, CancellationToken.None)
            do! callbackCancelled.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

            Assert.IsFalse(result.Proceed)
            Assert.AreEqual(Some "policy_evaluation_timeout", result.Evaluations.Head.FailureCode)
            Assert.AreEqual(PolicyEvaluationOutcome.Failed, result.Evaluations.Head.Outcome)
        }

    [<TestMethod>]
    member _.EvaluationTimeoutCancelsTokenWithoutWaitingForUncooperativeCallback() : Task =
        task {
            let pending =
                TaskCompletionSource<Result<string option, PolicyEvaluationFailure>>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let mutable callbackToken = CancellationToken.None

            let policy =
                { Id = "uncooperative-policy"
                  Version = "test-v1"
                  Description = "Ignores cancellation and completes after the deadline"
                  Scope = PolicyScope.platform
                  Enforcement = PolicyEnforcement.Warn
                  EvaluateAsync =
                    fun _ cancellationToken ->
                        callbackToken <- cancellationToken
                        pending.Task }

            try
                let engine = PolicyEngine.create (TimeSpan.FromMilliseconds 20.0) [ policy ]

                let! result =
                    engine.EvaluateAsync(context None, CancellationToken.None)
                    |> fun evaluation -> evaluation.WaitAsync(TimeSpan.FromSeconds 5.0)

                Assert.IsTrue(callbackToken.IsCancellationRequested)
                Assert.IsFalse(pending.Task.IsCompleted)
                Assert.IsFalse(result.Proceed)
                Assert.AreEqual(Some "policy_evaluation_timeout", result.Evaluations.Head.FailureCode)
                Assert.AreEqual(PolicyEvaluationOutcome.Failed, result.Evaluations.Head.Outcome)
            finally
                pending.TrySetResult(Ok None) |> ignore
        }

    [<TestMethod>]
    member _.CallbackAndTransformExceptionsFailClosed() =
        let callbackException =
            { Id = "callback-exception"
              Version = "test-v1"
              Description = "Throws during evaluation"
              Scope = PolicyScope.platform
              Enforcement = PolicyEnforcement.Confirm
              EvaluateAsync =
                fun _ _ ->
                    Task.FromException<Result<string option, PolicyEvaluationFailure>>(
                        InvalidOperationException("callback failed")
                    ) }

        let transformException =
            { Id = "transform-exception"
              Version = "test-v1"
              Description = "Throws while modifying input"
              Scope = PolicyScope.platform
              Enforcement = PolicyEnforcement.Modify(fun _ -> invalidOp "transform failed")
              EvaluateAsync = Policy.evaluateSynchronously (fun _ -> Some "modify") }

        let result =
            PolicyEngine.create (TimeSpan.FromSeconds 1.0) [ callbackException; transformException ]
            |> fun engine -> evaluate engine (context None)

        Assert.IsFalse(result.Proceed)
        Assert.AreEqual(2, result.Evaluations.Length)

        Assert.IsTrue(
            result.Evaluations
            |> List.forall (fun evidence -> evidence.Outcome = PolicyEvaluationOutcome.Failed)
        )

        Assert.IsTrue(
            result.Evaluations
            |> List.forall (fun evidence -> evidence.FailureCode = Some "policy_callback_exception")
        )

    [<TestMethod>]
    member _.CallerCancellationPropagatesWithoutFailureEvidence() =
        let policy =
            { Id = "cancelled-policy"
              Version = "test-v1"
              Description = "Waits for cancellation"
              Scope = PolicyScope.platform
              Enforcement = PolicyEnforcement.Block
              EvaluateAsync =
                fun _ cancellationToken ->
                    task {
                        do! Task.Delay(TimeSpan.FromSeconds 5.0, cancellationToken)
                        return Ok None
                    } }

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        let engine = PolicyEngine.create (TimeSpan.FromSeconds 1.0) [ policy ]

        Assert.ThrowsExactly<OperationCanceledException>(fun () ->
            engine.EvaluateAsync(context None, cancellation.Token)
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.ConfirmationIsExplicitAndBlockStillWins() =
        let confirmation =
            { Id = "confirm"
              Version = "test-v1"
              Description = "Requires host confirmation"
              Scope = PolicyScope.platform
              Enforcement = PolicyEnforcement.Confirm
              EvaluateAsync = Policy.evaluateSynchronously (fun _ -> Some "confirm this action") }

        let block =
            { Id = "block"
              Version = "test-v1"
              Description = "Blocks the action"
              Scope = PolicyScope.platform
              Enforcement = PolicyEnforcement.Block
              EvaluateAsync = Policy.evaluateSynchronously (fun _ -> Some "blocked") }

        let context =
            { Authorization = authorization
              AgentId = agentId
              Stage = PolicyStage.PreAction
              ToolName = None
              Action = "dangerous"
              Input = Some "input"
              Facts = PolicyFacts.empty
              ExecutionId = None
              CurrentUsage = None }

        let confirmOnly =
            PolicyEngine.create (TimeSpan.FromSeconds 1.0) [ confirmation ]
            |> fun engine -> engine.EvaluateAsync(context, CancellationToken.None).Result

        Assert.IsTrue(confirmOnly.Proceed)
        Assert.IsTrue(confirmOnly.RequiresConfirmation)

        let blocked =
            PolicyEngine.create (TimeSpan.FromSeconds 1.0) [ confirmation; block ]
            |> fun engine -> engine.EvaluateAsync(context, CancellationToken.None).Result

        Assert.IsFalse(blocked.Proceed)
        Assert.IsTrue(blocked.RequiresConfirmation)

    [<TestMethod>]
    member _.ConfirmationBrokerTimesOutAndPropagatesCallerCancellation() =
        let request =
            { Authorization =
                SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
                |> fun principal ->
                    AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
                    |> Option.get
              Correlation = CorrelationContext.root ()
              Context =
                { Authorization = authorization
                  AgentId = agentId
                  Stage = PolicyStage.PreInput
                  ToolName = None
                  Action = "execute"
                  Input = Some "input"
                  Facts = PolicyFacts.empty
                  ExecutionId = None
                  CurrentUsage = None }
              Violations = []
              EffectiveInput = Some "input"
              RequestedAt = DateTimeOffset.UtcNow }

        let broker =
            PolicyConfirmationBroker.create (TimeSpan.FromMilliseconds 20.0) (fun _ cancellationToken ->
                task {
                    do! Task.Delay(TimeSpan.FromSeconds 5.0, cancellationToken)
                    return PolicyConfirmation.Approved
                })

        Assert.AreEqual(
            PolicyConfirmation.Denied,
            PolicyConfirmationBroker.resolve broker request CancellationToken.None
            |> _.Result
        )

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        Assert.ThrowsExactly<TaskCanceledException>(fun () ->
            PolicyConfirmationBroker.resolve broker request cancellation.Token
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore
