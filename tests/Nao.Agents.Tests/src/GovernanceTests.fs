namespace Nao.Agents.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
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

[<TestClass>]
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

[<TestClass>]
type ConstitutionTests() =

    [<TestMethod>]
    member _.EmptyConstitutionPassesAll() =
        let constitution = Constitution.empty "test"
        let result = Constitution.check constitution "any content"
        Assert.IsTrue(result.Passed)
        Assert.AreEqual(0, result.Violations.Length)

    [<TestMethod>]
    member _.AddRuleEnforcesCheck() =
        let rule =
            { Id = "no-profanity"
              Description = "No bad words"
              Category = RuleCategory.Safety
              Priority = 50
              IsHardConstraint = true
              Check = fun content -> content.Contains("badword") }

        let constitution = Constitution.empty "test" |> Constitution.addRule rule
        let resultClean = Constitution.check constitution "this is fine"
        Assert.IsTrue(resultClean.Passed)
        let resultBad = Constitution.check constitution "this has badword in it"
        Assert.IsFalse(resultBad.Passed)
        Assert.AreEqual(1, resultBad.Violations.Length)
        Assert.AreEqual("no-profanity", resultBad.Violations.[0].RuleId)

    [<TestMethod>]
    member _.HasHardViolationsDetectsHardConstraints() =
        let hardRule =
            { Id = "hard"
              Description = "Hard"
              Category = RuleCategory.Safety
              Priority = 100
              IsHardConstraint = true
              Check = fun _ -> true }

        let constitution = Constitution.empty "test" |> Constitution.addRule hardRule
        let result = Constitution.check constitution "anything"
        Assert.IsTrue(Constitution.hasHardViolations result)

    [<TestMethod>]
    member _.SoftViolationDoesNotBlockHard() =
        let softRule =
            { Id = "soft"
              Description = "Soft"
              Category = RuleCategory.Behavioral
              Priority = 10
              IsHardConstraint = false
              Check = fun _ -> true }

        let constitution = Constitution.empty "test" |> Constitution.addRule softRule
        let result = Constitution.check constitution "anything"
        Assert.IsFalse(result.Passed) // has violations
        Assert.IsFalse(Constitution.hasHardViolations result) // but not hard

    [<TestMethod>]
    member _.NoPrivateDataRuleDetectsEmail() =
        let constitution =
            Constitution.empty "test" |> Constitution.addRule Constitution.noPrivateDataRule

        let resultWithEmail =
            Constitution.check constitution "Contact me at user@example.com"

        Assert.IsFalse(resultWithEmail.Passed)

    [<TestMethod>]
    member _.NoPrivateDataRuleDetectsPhone() =
        let constitution =
            Constitution.empty "test" |> Constitution.addRule Constitution.noPrivateDataRule

        let resultWithPhone = Constitution.check constitution "Call me at 555-123-4567"
        Assert.IsFalse(resultWithPhone.Passed)

    [<TestMethod>]
    member _.RenderForPromptIncludesRules() =
        let rule =
            { Id = "r1"
              Description = "Be polite"
              Category = RuleCategory.Behavioral
              Priority = 50
              IsHardConstraint = false
              Check = fun _ -> false }

        let constitution =
            { Constitution.empty "Politeness" with
                Preamble = Some "Always be kind" }
            |> Constitution.addRule rule

        let rendered = Constitution.renderForPrompt constitution
        Assert.IsTrue(rendered.Contains("Politeness"))
        Assert.IsTrue(rendered.Contains("Always be kind"))
        Assert.IsTrue(rendered.Contains("[SHOULD] Be polite"))

[<TestClass>]
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

[<TestClass>]
type PolicyEngineTests() =

    let agentId = "test"

    [<TestMethod>]
    member _.NoPoliciesAllowsAll() =
        let engine = PolicyEngine.create []

        let ctx =
            { AgentId = agentId
              Action = "execute"
              Input = Some "hello"
              ExecutionId = None
              CurrentUsage = None }

        let result = engine.Evaluate(ctx)
        Assert.IsTrue(result.Proceed)
        Assert.AreEqual(None, result.ModifiedInput)
        Assert.AreEqual(0, result.Violations.Length)

    [<TestMethod>]
    member _.CostBudgetPolicyBlocksWhenExceeded() =
        let policy = PolicyEngine.costBudgetPolicy 1.0m
        let engine = PolicyEngine.create [ policy ]

        let usage =
            { ResourceUsage.Zero with
                EstimatedCostUsd = 1.5m }

        let ctx =
            { AgentId = agentId
              Action = "execute"
              Input = None
              ExecutionId = None
              CurrentUsage = Some usage }

        let result = engine.Evaluate(ctx)
        Assert.IsFalse(result.Proceed)
        Assert.AreEqual(1, result.Violations.Length)
        Assert.IsTrue(result.Violations.[0].Message.Contains("Cost budget exceeded"))

    [<TestMethod>]
    member _.CostBudgetPolicyAllowsWithinBudget() =
        let policy = PolicyEngine.costBudgetPolicy 10.0m
        let engine = PolicyEngine.create [ policy ]

        let usage =
            { ResourceUsage.Zero with
                EstimatedCostUsd = 5.0m }

        let ctx =
            { AgentId = agentId
              Action = "execute"
              Input = None
              ExecutionId = None
              CurrentUsage = Some usage }

        let result = engine.Evaluate(ctx)
        Assert.IsTrue(result.Proceed)

    [<TestMethod>]
    member _.MaxOutputLengthPolicyModifiesContent() =
        let policy = PolicyEngine.maxOutputLengthPolicy 10
        let engine = PolicyEngine.create [ policy ]

        let ctx =
            { AgentId = agentId
              Action = "output"
              Input = Some "this is way too long for the limit"
              ExecutionId = None
              CurrentUsage = None }

        let result = engine.Evaluate(ctx)
        // Modify enforcement doesn't block
        Assert.IsTrue(result.Proceed)
        Assert.IsTrue(result.ModifiedInput.IsSome)
        Assert.IsTrue(result.ModifiedInput.Value.Length <= 30) // truncated + "... [truncated]"

    [<TestMethod>]
    member _.MultiplePoliciesEvaluatedInOrder() =
        let warnPolicy =
            { Id = "warn-test"
              Description = "Always warns"
              Enforcement = PolicyEnforcement.Warn
              Evaluate = fun _ -> Some "just a warning" }

        let blockPolicy =
            { Id = "block-test"
              Description = "Always blocks"
              Enforcement = PolicyEnforcement.Block
              Evaluate = fun _ -> Some "blocked" }

        let engine = PolicyEngine.create [ warnPolicy; blockPolicy ]

        let ctx =
            { AgentId = agentId
              Action = "x"
              Input = None
              ExecutionId = None
              CurrentUsage = None }

        let result = engine.Evaluate(ctx)
        Assert.IsFalse(result.Proceed)
        Assert.AreEqual(2, result.Violations.Length)
        Assert.AreEqual(1, result.Warnings.Length)
