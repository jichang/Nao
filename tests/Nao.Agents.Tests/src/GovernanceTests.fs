namespace Nao.Agents.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

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

    [<TestMethod>]
    member _.RecordAndQuery() =
        let audit = InMemory.auditLog ()

        let entry =
            AuditLog.toolInvocation agentId "search" "query" "results" true PermissionDecision.Allow None

        audit.RecordAsync(entry).Wait()

        let results =
            (audit.QueryAsync agentId (DateTimeOffset.UtcNow.AddHours(-1.0))).Result

        Assert.AreEqual(1, results.Length)
        Assert.AreEqual(agentId, results.[0].AgentId)

    [<TestMethod>]
    member _.QueryByExecutionFiltersCorrectly() =
        let audit = InMemory.auditLog ()
        let execId = Guid.NewGuid()
        let entry1 = AuditLog.llmCall agentId "gpt-4" (Some execId)
        let entry2 = AuditLog.llmCall agentId "gpt-4" (Some(Guid.NewGuid()))
        audit.RecordAsync(entry1).Wait()
        audit.RecordAsync(entry2).Wait()
        let results = (audit.QueryByExecutionAsync execId).Result
        Assert.AreEqual(1, results.Length)

    [<TestMethod>]
    member _.GetDeniedCountFiltersDenied() =
        let audit = InMemory.auditLog ()

        let permitted =
            AuditLog.toolInvocation agentId "t1" "" "" true PermissionDecision.Allow None

        let denied =
            { AuditLog.toolInvocation agentId "t2" "" "" false PermissionDecision.Deny None with
                Permitted = false }

        audit.RecordAsync(permitted).Wait()
        audit.RecordAsync(denied).Wait()

        let count =
            (audit.GetDeniedCountAsync agentId (DateTimeOffset.UtcNow.AddHours(-1.0))).Result

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
