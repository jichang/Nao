namespace Nao.E2E.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Assistant
open Nao.Persistence
open Nao.Agents

// =============================================================================
// ETCLOVG Architecture E2E Tests
// Complete examples demonstrating all seven layers working together
// =============================================================================

/// Demo tools with richer metadata for the tool protocol layer
module EtclovgDemoTools =

    let stockPrice: Tool =
        Tool.Create("get_stock_price", "Get the current stock price for a ticker symbol",
            fun ticker ->
                let price =
                    match ticker.Trim().ToUpper() with
                    | "AAPL" -> "189.45"
                    | "MSFT" -> "420.12"
                    | "GOOGL" -> "175.30"
                    | t -> sprintf "Unknown ticker: %s" t
                Task.FromResult(sprintf """{"ticker":"%s","price":%s,"currency":"USD"}""" (ticker.ToUpper()) price))

    let sendEmail: Tool =
        Tool.Create("send_email", "Send an email to a recipient. Input format: 'to@email.com|subject|body'",
            fun input ->
                let parts = input.Split('|')
                if parts.Length >= 3 then
                    Task.FromResult(sprintf "Email sent to %s with subject '%s'" parts.[0] parts.[1])
                else
                    Task.FromResult("Error: invalid email format"))

    let searchDocs: Tool =
        Tool.Create("search_docs", "Search internal documentation. Returns relevant passages.",
            fun query ->
                Task.FromResult(sprintf "Found 3 results for '%s': [1] Getting Started Guide [2] API Reference [3] FAQ" query))

    let dangerousDelete: Tool =
        Tool.Create("delete_all_data", "Permanently delete all data. DANGEROUS - requires confirmation.",
            fun _ -> Task.FromResult("All data deleted permanently"))

    let allTools = [ stockPrice; sendEmail; searchDocs; dangerousDelete ]


/// Mock provider that simulates LLM behavior for ETCLOVG demos
type EtclovgMockProvider() =
    let mutable callCount = 0

    member _.CallCount = callCount

    interface ILlmProvider with
        member _.Name = "EtclovgMock"
        member _.CompleteAsync (conversation: Conversation) (_options: CompletionOptions) =
            callCount <- callCount + 1
            let lastMsg =
                conversation
                |> List.tryFindBack (fun m -> m.Role = User)
                |> Option.map (fun m -> m.Content)
                |> Option.defaultValue ""

            let response =
                if lastMsg.Contains("[Tool Result") || lastMsg.Contains("[Agent Result") then
                    let result = lastMsg.Split("]:") |> Array.last |> fun s -> s.Trim()
                    sprintf "Based on the data: %s" result
                elif lastMsg.Contains("stock") || lastMsg.Contains("price") then
                    """{"action":"tool","name":"get_stock_price","input":"AAPL"}"""
                elif lastMsg.Contains("email") || lastMsg.Contains("send") then
                    """{"action":"tool","name":"send_email","input":"team@company.com|Update|Project is on track"}"""
                elif lastMsg.Contains("search") || lastMsg.Contains("docs") then
                    """{"action":"tool","name":"search_docs","input":"deployment guide"}"""
                elif lastMsg.Contains("delete") then
                    """{"action":"tool","name":"delete_all_data","input":"confirm"}"""
                elif lastMsg.Contains("delegate") || lastMsg.Contains("specialist") then
                    """{"action":"delegate","name":"research-agent","input":"find latest trends"}"""
                else
                    sprintf "I understand your request: %s. Here's my response." lastMsg

            Task.FromResult({ Content = response; FinishReason = "stop"; TokensUsed = Some 150 })


// =============================================================================
// E: Execution Environment — Resource-bounded agent execution
// =============================================================================

[<TestClass>]
type EtclovgExecutionTests() =

    let makeAgent response : IAgent =
        { new IAgent with
            member _.Id = { Name = "bounded-agent"; Description = "Agent with resource bounds" }
            member _.RunAsync(_) = Task.FromResult response
            member _.HandleMessageAsync(_) = Task.FromResult None }

    [<TestMethod>]
    member _.AgentRunsWithinResourceBudget() =
        // Configure a sandbox with generous limits
        let limits = ResourceLimits.Constrained 300 100 50000
        let sandbox = { SandboxConfig.Default with Limits = limits }
        let ctx = ExecutionContext.Create sandbox
        let agent = makeAgent "resource-bounded output"
        let env = ExecutionEnvironment.local ()

        let result = (env.ExecuteAsync ctx agent "process this").Result
        match result with
        | Ok response -> Assert.AreEqual("resource-bounded output", response)
        | Error exceeded -> Assert.Fail(sprintf "Unexpected limit exceeded: %A" exceeded)

    [<TestMethod>]
    member _.AgentBlockedWhenLlmCallsExceedLimit() =
        // Configure strict limits: only 1 LLM call allowed
        let limits = { ResourceLimits.Unlimited with MaxLlmCalls = 1 }
        let sandbox = { SandboxConfig.Default with Limits = limits }
        let ctx = ExecutionContext.Create sandbox
        // Simulate that 2 LLM calls were already made
        ctx.RecordLlmCall(500, 0.01m)
        ctx.RecordLlmCall(500, 0.01m)

        let agent = makeAgent "should not reach"
        let env = ExecutionEnvironment.local ()
        let result = (env.ExecuteAsync ctx agent "query").Result
        match result with
        | Error LimitExceeded.LlmCalls -> Assert.IsTrue(true)
        | _ -> Assert.Fail("Expected LlmCalls limit exceeded")

    [<TestMethod>]
    member _.ExecutionContextTracksCumulativeCost() =
        let sandbox = SandboxConfig.Default
        let ctx = ExecutionContext.Create sandbox
        ctx.RecordLlmCall(1000, 0.003m)
        ctx.RecordLlmCall(2000, 0.006m)
        ctx.RecordToolCall()
        ctx.RecordToolCall()
        ctx.RecordToolCall()

        Assert.AreEqual(2, ctx.Usage.LlmCalls)
        Assert.AreEqual(3000, ctx.Usage.TotalTokens)
        Assert.AreEqual(0.009m, ctx.Usage.EstimatedCostUsd)
        Assert.AreEqual(3, ctx.Usage.ToolCalls)


// =============================================================================
// T: Tool Interface & Protocol — Structured tool discovery and invocation
// =============================================================================

[<TestClass>]
type EtclovgToolProtocolTests() =

    [<TestMethod>]
    member _.ToolProtocolDiscoveryAndInvocation() =
        // Create a protocol from tools
        let protocol = ToolProtocol.fromTools EtclovgDemoTools.allTools

        // Discovery: list all available tools
        let schemas = protocol.ListTools().Result
        Assert.AreEqual(4, schemas.Length)
        Assert.IsTrue(schemas |> List.exists (fun s -> s.Name = "get_stock_price"))
        Assert.IsTrue(schemas |> List.exists (fun s -> s.Name = "send_email"))

        // Get specific tool
        let stockTool = (protocol.GetTool "get_stock_price@1.0").Result
        Assert.IsTrue(stockTool.IsSome)
        Assert.AreEqual("Get the current stock price for a ticker symbol", stockTool.Value.Description)

        // Invoke tool through protocol
        let result = (protocol.InvokeAsync "get_stock_price@1.0" "MSFT").Result
        Assert.IsTrue(result.Success)
        Assert.IsTrue(result.Output.Contains("420.12"))
        Assert.IsTrue(result.DurationMs >= 0L)

    [<TestMethod>]
    member _.ToolProtocolWithRateLimitMiddleware() =
        let middleware = ToolProtocol.rateLimitMiddleware 5
        let protocol =
            ToolProtocol.fromTools EtclovgDemoTools.allTools
            |> ToolProtocol.withMiddleware middleware

        // Should work within the rate limit
        for _ in 1..5 do
            let result = (protocol.InvokeAsync "get_stock_price@1.0" "AAPL").Result
            Assert.IsTrue(result.Success)

        // 6th call should be blocked
        let blocked = (protocol.InvokeAsync "get_stock_price@1.0" "AAPL").Result
        Assert.IsFalse(blocked.Success)
        Assert.IsTrue(blocked.Error.Value.Contains("Rate limit"))

    [<TestMethod>]
    member _.ToolRouterSelectsByKeyword() =
        let schemas = EtclovgDemoTools.allTools |> List.map ToolSchema.fromTool
        let patterns = Map.ofList [
            "get_stock_price", ["stock"; "price"; "ticker"]
            "send_email", ["email"; "send"; "mail"]
            "search_docs", ["search"; "find"; "docs"; "documentation"]
        ]
        let result = ToolRouter.selectByPattern patterns "look up stock price for AAPL" schemas
        Assert.IsTrue(result.IsSome)
        Assert.AreEqual("get_stock_price", result.Value.Tool.Name)
        Assert.IsTrue(result.Value.Confidence > 0.0)


// =============================================================================
// C: Context & Memory — Tiered memory and context compaction
// =============================================================================

[<TestClass>]
type EtclovgContextMemoryTests() =

    [<TestMethod>]
    member _.ContextCompactionKeepsRecentMessages() =
        // Simulate a long conversation that exceeds token budget
        let conversation = [
            for i in 1..50 ->
                { Role = (if i % 2 = 0 then Assistant else User)
                  Content = sprintf "Message number %d with some additional content to take up space in the context window" i }
        ]
        let totalTokens = ContextCompaction.estimateConversationTokens conversation
        Assert.IsTrue(totalTokens > 100) // ensure it's over budget

        // Apply drop-oldest strategy with tight budget
        let result = (ContextCompaction.applyAsync CompactionStrategy.DropOldest 200 conversation).Result
        Assert.IsTrue(result.MessagesRemoved > 0)
        Assert.IsTrue(result.TokensSaved > 0)
        // Recent messages should be preserved
        let lastKept = result.Compacted |> List.last
        Assert.AreEqual(conversation |> List.last, lastKept)

    [<TestMethod>]
    member _.TieredMemoryOrganizesData() =
        // Create memories at different tiers
        let shortTerm =
            { Key = "current-task"; Value = "answering user question about stocks"
              Tier = MemoryTier.ShortTerm; Timestamp = DateTimeOffset.UtcNow
              AccessCount = 1; Relevance = 1.0; Tags = ["context"] }
        let midTerm =
            { Key = "user-preference"; Value = "prefers brief responses"
              Tier = MemoryTier.MidTerm; Timestamp = DateTimeOffset.UtcNow.AddMinutes(-30.0)
              AccessCount = 5; Relevance = 0.8; Tags = ["preference"] }
        let longTerm =
            { Key = "user-name"; Value = "Alice"
              Tier = MemoryTier.LongTerm; Timestamp = DateTimeOffset.UtcNow.AddDays(-30.0)
              AccessCount = 50; Relevance = 0.9; Tags = ["identity"] }

        Assert.AreEqual(MemoryTier.ShortTerm, shortTerm.Tier)
        Assert.AreEqual(MemoryTier.MidTerm, midTerm.Tier)
        Assert.AreEqual(MemoryTier.LongTerm, longTerm.Tier)
        // Long-term has highest access count (promoted over time)
        Assert.IsTrue(longTerm.AccessCount > midTerm.AccessCount)


// =============================================================================
// L: Lifecycle & Orchestration — Full agent lifecycle management
// =============================================================================

[<TestClass>]
type EtclovgLifecycleTests() =

    let agentId = { Name = "lifecycle-demo"; Description = "Demonstrates lifecycle" }

    [<TestMethod>]
    member _.FullLifecycleTransitions() =
        // Demonstrate the complete lifecycle of an agent execution
        let lifecycle = AgentLifecycle.create () |> AgentLifecycle.withHooks [ PassthroughHook() :> ILifecycleHook ]

        // Created -> Ready
        let readyResult = (AgentLifecycle.initializeAsync agentId lifecycle).Result
        let ready = readyResult |> Result.defaultWith (fun msg -> failwith msg)
        Assert.AreEqual(LifecycleState.Ready, ready.State)

        // Ready -> Running
        let running = (AgentLifecycle.startAsync agentId "user request" ready).Result
        Assert.AreEqual(LifecycleState.Running, running.State)

        // Running -> Suspended (e.g., waiting for human approval)
        let suspended = AgentLifecycle.suspend agentId "awaiting human review" running
        Assert.AreEqual(LifecycleState.Suspended, suspended.State)

        // Suspended -> Running (resumed after approval)
        let resumed = AgentLifecycle.resume agentId suspended
        Assert.AreEqual(LifecycleState.Running, resumed.State)

        // Running -> Completed
        let completed = (AgentLifecycle.completeAsync agentId "task done successfully" resumed).Result
        Assert.AreEqual(LifecycleState.Completed, completed.State)

        // Verify full event history
        Assert.AreEqual(5, completed.Events.Length)

    [<TestMethod>]
    member _.LifecyclePipelineWithMultipleStages() =
        // Simulate an issue-to-deployment pipeline
        let planStage : PipelineStage =
            { Name = "plan"
              Description = "Analyze the task and create a plan"
              Execute = fun input -> Task.FromResult(sprintf "Plan: Break '%s' into 3 subtasks" input)
              Validate = fun output -> Task.FromResult(if output.Contains("Plan:") then Ok () else Error "No plan generated")
              Retry = RetryPolicy.None }

        let implementStage : PipelineStage =
            { Name = "implement"
              Description = "Execute the plan"
              Execute = fun plan -> Task.FromResult(sprintf "Implementation complete based on: %s" plan)
              Validate = fun output -> Task.FromResult(if output.Contains("complete") then Ok () else Error "Implementation incomplete")
              Retry = RetryPolicy.Fixed (2, 0) }

        let verifyStage : PipelineStage =
            { Name = "verify"
              Description = "Verify the implementation"
              Execute = fun impl -> Task.FromResult(sprintf "Verified: %s - All checks passed" impl)
              Validate = fun output -> Task.FromResult(if output.Contains("passed") then Ok () else Error "Verification failed")
              Retry = RetryPolicy.None }

        let result = (LifecyclePipeline.executeAsync [ planStage; implementStage; verifyStage ] "add user authentication").Result

        Assert.IsTrue(result.Success)
        Assert.AreEqual(3, result.Stages.Length)
        Assert.IsTrue(result.FinalOutput.Value.Contains("Verified"))
        Assert.IsTrue(result.FinalOutput.Value.Contains("passed"))
        Assert.IsTrue(result.Stages |> List.forall (fun s -> s.Success))

    [<TestMethod>]
    member _.OrchestratorWithToolProtocolIntegration() =
        // Show the Orchestrator using ToolProtocol for structured tool management
        let provider = EtclovgMockProvider() :> ILlmProvider
        let tools = [ EtclovgDemoTools.stockPrice; EtclovgDemoTools.searchDocs ]
        let protocol = ToolProtocol.fromTools tools

        // Verify tools are discoverable
        let schemas = protocol.ListTools().Result
        Assert.AreEqual(2, schemas.Length)

        // Create orchestrator with these tools
        let orchestrator = NaoOrchestrator.create provider tools []
        let result = (orchestrator.RunAsync "What is the stock price of AAPL?").Result
        Assert.IsTrue(result.Contains("189.45") || result.Contains("AAPL"))


// =============================================================================
// O: Observability — Tracing, metrics, and resilience
// =============================================================================

[<TestClass>]
type EtclovgObservabilityTests() =

    [<TestMethod>]
    member _.DistributedTracingAcrossAgentCalls() =
        let tracer = InMemory.tracer ()

        // Root span: user request arrives
        let rootSpan = tracer.StartTrace("user-request")
        tracer.SetAttributes rootSpan (Map.ofList ["user.id", "alice"; "request.type", "stock-query"])

        // Child span: orchestrator processing
        let orchestratorSpan = tracer.StartSpan rootSpan "orchestrator.process"
        tracer.AddEvent orchestratorSpan "routing-decision" (Map.ofList ["selected-tool", "get_stock_price"])

        // Grandchild span: tool invocation
        let toolSpan = tracer.StartSpan orchestratorSpan "tool.invoke.get_stock_price"
        tracer.SetAttributes toolSpan (Map.ofList ["tool.input", "AAPL"])
        // Simulate tool execution
        let toolResult = (EtclovgDemoTools.stockPrice.Execute ToolContext.allowAll "AAPL").Result
        tracer.AddEvent toolSpan "tool-result" (Map.ofList ["output", toolResult])
        tracer.EndSpan toolSpan SpanStatus.Ok

        // End orchestrator
        tracer.EndSpan orchestratorSpan SpanStatus.Ok
        tracer.EndSpan rootSpan SpanStatus.Ok

        // Verify trace structure
        let allSpans = tracer.GetTrace(rootSpan.TraceId)
        Assert.AreEqual(3, allSpans.Length)
        // All spans share the same trace ID
        Assert.IsTrue(allSpans |> List.forall (fun s -> s.TraceId = rootSpan.TraceId))
        // Tool span is child of orchestrator
        let toolSpanResult = allSpans |> List.find (fun s -> s.OperationName.Contains("tool.invoke"))
        Assert.AreEqual(Some orchestratorSpan.Id, toolSpanResult.ParentSpanId)

    [<TestMethod>]
    member _.MetricsTrackCostAndLatency() =
        let metrics = InMemory.metrics ()

        // Simulate a multi-step agent execution
        metrics.RecordLlmCall 500 200 150L    // First LLM call: routing decision
        metrics.RecordToolCall "get_stock_price" 25L true
        metrics.RecordLlmCall 800 300 200L    // Second LLM call: format response
        metrics.RecordLlmCall 200 100 100L    // Third: summarize

        let summary = metrics.GetMetrics()
        Assert.AreEqual(3, summary.TotalLlmCalls)
        Assert.AreEqual(1500, summary.TotalInputTokens)
        Assert.AreEqual(600, summary.TotalOutputTokens)
        Assert.AreEqual(1, summary.TotalToolCalls)
        Assert.AreEqual(150.0, summary.AvgLatencyMs)

        // Estimate cost with GPT-4o pricing
        let cost = metrics.EstimateCost MetricsCollector.gpt4o
        // 1500 input * 0.0025/1K + 600 output * 0.01/1K = 0.00375 + 0.006 = 0.00975
        Assert.IsTrue(cost > 0m)
        Assert.AreEqual(0.00975m, cost)

    [<TestMethod>]
    member _.ResilienceWithRetryAndFallback() =
        let mutable callCount = 0
        let unreliableService (input: string) : Task<string> =
            task {
                callCount <- callCount + 1
                if callCount <= 2 then
                    return failwith "Service temporarily unavailable"
                else
                    return sprintf "Success: %s" input
            }

        let config =
            { ResilienceConfig.Default with
                RetryPolicy = RetryPolicy.Fixed (3, 50)
                Fallback = FallbackStrategy.None }

        let result = (Resilience.executeAsync config None unreliableService "get data").Result
        match result with
        | Ok value ->
            Assert.AreEqual("Success: get data", value)
            Assert.AreEqual(3, callCount) // 2 failures + 1 success
        | Error msg -> Assert.Fail(sprintf "Expected success after retries, got: %s" msg)

    [<TestMethod>]
    member _.CircuitBreakerProtectsFromCascadingFailure() =
        let cbConfig = { FailureThreshold = 3; OpenDuration = TimeSpan.FromMilliseconds(100.0); SuccessThreshold = 1 }
        let cb = CircuitBreaker(cbConfig)

        // Record failures to open the circuit
        cb.RecordFailure()
        cb.RecordFailure()
        cb.RecordFailure()

        // Circuit is now open
        Assert.IsFalse(cb.CanExecute())

        // Try to execute with open circuit — should use fallback
        let config =
            { ResilienceConfig.NoResilience with
                Fallback = FallbackStrategy.DefaultValue "cached result" }
        let result = (Resilience.executeAsync config (Some cb) (fun _ -> failwith "unreachable") "query").Result
        match result with
        | Ok value -> Assert.AreEqual("cached result", value)
        | Error _ -> Assert.Fail("Expected fallback value")


// =============================================================================
// V: Verification & Evaluation — Readiness, tracing, regression
// =============================================================================

[<TestClass>]
type EtclovgVerificationTests() =

    let agentId = { Name = "verified-agent"; Description = "Agent with verification" }

    [<TestMethod>]
    member _.ReadinessChecksValidatePrerequisites() =
        // Define readiness checks that validate the agent's environment
        let toolCheck =
            { new IReadinessCheck with
                member _.Name = "required-tools"
                member _.CheckAsync _agentId _input =
                    // Check that required tools are available
                    let protocol = ToolProtocol.fromTools EtclovgDemoTools.allTools
                    task {
                        let! available = protocol.IsAvailable "get_stock_price@1.0"
                        if available then return ReadinessResult.Ready
                        else return ReadinessResult.NotReady ["get_stock_price tool not available"]
                    } }

        let budgetCheck =
            { new IReadinessCheck with
                member _.Name = "cost-budget"
                member _.CheckAsync _agentId _input =
                    // Verify cost budget hasn't been exhausted
                    Task.FromResult ReadinessResult.Ready }

        let result = (Verification.checkReadiness [ toolCheck; budgetCheck ] agentId "check stocks").Result
        Assert.AreEqual(ReadinessResult.Ready, result)

    [<TestMethod>]
    member _.ExecutionTraceCapturesFullHistory() =
        // Start a trace for an execution
        let trace = Verification.startTrace agentId "What is AAPL stock price?"

        // Record each step
        let trace = trace |> Verification.addStep (TraceAction.LlmCall "gpt-4o") "user query" """{"action":"tool","name":"get_stock_price","input":"AAPL"}""" 150L
        let trace = trace |> Verification.addStep (TraceAction.ToolInvocation "get_stock_price") "AAPL" """{"ticker":"AAPL","price":189.45}""" 25L
        let trace = trace |> Verification.addStep (TraceAction.LlmCall "gpt-4o") "tool result" "The current price of AAPL is $189.45" 120L
        let trace = trace |> Verification.complete "The current price of AAPL is $189.45"

        Assert.IsTrue(trace.Success)
        Assert.AreEqual(3, trace.Steps.Length)
        Assert.AreEqual(Some "The current price of AAPL is $189.45", trace.Output)
        // Total duration across steps
        let totalDuration = trace.Steps |> List.sumBy (fun s -> s.DurationMs)
        Assert.AreEqual(295L, totalDuration)

    [<TestMethod>]
    member _.RegressionDetectionComparesBaselines() =
        let store = InMemoryTraceStore() :> ITraceStore

        // Save a baseline trace (fast, 2 steps)
        let baseline =
            Verification.startTrace agentId "get AAPL price"
            |> Verification.addStep (TraceAction.LlmCall "model") "" "" 100L
            |> Verification.addStep (TraceAction.ToolInvocation "get_stock_price") "" "" 20L
            |> Verification.complete "$189.45"
        let baseline = { baseline with StartedAt = DateTimeOffset.UtcNow.AddHours(-1.0); CompletedAt = Some (DateTimeOffset.UtcNow.AddHours(-1.0).AddMilliseconds(120.0)) }
        store.SaveAsync(baseline).Wait()

        // New execution is much slower with more steps
        let current =
            Verification.startTrace agentId "get AAPL price"
            |> Verification.addStep (TraceAction.LlmCall "model") "" "" 500L
            |> Verification.addStep (TraceAction.LlmCall "model") "" "" 300L
            |> Verification.addStep (TraceAction.LlmCall "model") "" "" 400L
            |> Verification.addStep (TraceAction.ToolInvocation "get_stock_price") "" "" 20L
            |> Verification.addStep (TraceAction.LlmCall "model") "" "" 600L
            |> Verification.complete "$189.45"
        let current = { current with StartedAt = DateTimeOffset.UtcNow; CompletedAt = Some (DateTimeOffset.UtcNow.AddMilliseconds(1820.0)) }

        // Detect regression
        let regression = Regression.detect baseline current
        Assert.IsTrue(regression.IsRegression)
        Assert.IsTrue(regression.Regressions |> List.exists (fun r -> r.Category = RegressionCategory.Latency))


// =============================================================================
// G: Governance & Security — Permissions, constitution, audit, policies
// =============================================================================

[<TestClass>]
type EtclovgGovernanceTests() =

    let agentId = { Name = "governed-agent"; Description = "Agent with governance" }

    [<TestMethod>]
    member _.PermissionModelRestrictsDangerousTools() =
        // Create a permission model that blocks dangerous operations
        let perms =
            PermissionModel.Permissive agentId
            |> PermissionModel.grant "tool:get_stock_price" PermissionLevel.Allow
            |> PermissionModel.grant "tool:search_docs" PermissionLevel.Allow
            |> PermissionModel.grant "tool:send_email" PermissionLevel.AllowWithAudit
            |> PermissionModel.grant "tool:delete_all_data" PermissionLevel.Deny

        // Safe tools are allowed
        Assert.AreEqual(PermissionLevel.Allow, PermissionModel.canUseTool perms "get_stock_price")
        Assert.AreEqual(PermissionLevel.Allow, PermissionModel.canUseTool perms "search_docs")
        // Email requires audit trail
        Assert.AreEqual(PermissionLevel.AllowWithAudit, PermissionModel.canUseTool perms "send_email")
        // Dangerous tool is blocked
        Assert.AreEqual(PermissionLevel.Deny, PermissionModel.canUseTool perms "delete_all_data")

    [<TestMethod>]
    member _.ConstitutionEnforcesOutputSafety() =
        let constitution =
            Constitution.empty "corporate-safety"
            |> Constitution.addRule Constitution.noPrivateDataRule
            |> Constitution.addRule
                { Id = "no-financial-advice"
                  Description = "Do not provide specific buy/sell recommendations"
                  Category = RuleCategory.Domain "finance"
                  Priority = 80
                  IsHardConstraint = true
                  Check = fun content ->
                      content.Contains("you should buy") || content.Contains("sell immediately") }
            |> Constitution.addRule
                { Id = "professional-tone"
                  Description = "Maintain professional tone in all communications"
                  Category = RuleCategory.Behavioral
                  Priority = 30
                  IsHardConstraint = false
                  Check = fun content -> content.Contains("lol") || content.Contains("lmao") }

        // Safe output passes
        let safeResult = Constitution.check constitution "The current price of AAPL is $189.45. Past performance does not guarantee future results."
        Assert.IsTrue(safeResult.Passed)

        // Financial advice blocked
        let adviceResult = Constitution.check constitution "Based on the trend, you should buy AAPL immediately."
        Assert.IsFalse(adviceResult.Passed)
        Assert.IsTrue(Constitution.hasHardViolations adviceResult)
        Assert.IsTrue(adviceResult.Violations |> List.exists (fun v -> v.RuleId = "no-financial-advice"))

        // PII blocked
        let piiResult = Constitution.check constitution "The user's email is alice@company.com"
        Assert.IsFalse(piiResult.Passed)
        Assert.IsTrue(Constitution.hasHardViolations piiResult)

        // Informal tone is soft violation (doesn't block)
        let informalResult = Constitution.check constitution "lol that's a good price"
        Assert.IsFalse(informalResult.Passed)
        Assert.IsFalse(Constitution.hasHardViolations informalResult) // soft constraint

    [<TestMethod>]
    member _.AuditLogTracksAllActions() =
        let audit = InMemory.auditLog ()
        let execId = Guid.NewGuid()

        // Record a sequence of actions
        audit.RecordAsync(AuditLog.llmCall agentId "gpt-4o" (Some execId)).Wait()
        audit.RecordAsync(AuditLog.toolInvocation agentId "get_stock_price" "AAPL" """{"price":189.45}""" true PermissionLevel.Allow (Some execId)).Wait()
        audit.RecordAsync(AuditLog.toolInvocation agentId "delete_all_data" "confirm" "" false PermissionLevel.Deny (Some execId)).Wait()

        // Query all entries for this execution
        let entries = (audit.QueryByExecutionAsync execId).Result
        Assert.AreEqual(3, entries.Length)

        // Check denied count
        let deniedCount = (audit.GetDeniedCountAsync agentId (DateTimeOffset.UtcNow.AddMinutes(-1.0))).Result
        Assert.AreEqual(1, deniedCount)

    [<TestMethod>]
    member _.PolicyEngineEnforcesBudgetAndRateLimits() =
        let policies = [
            PolicyEngine.costBudgetPolicy 5.0m
            PolicyEngine.rateLimitPolicy "tool_call" 10
        ]
        let engine = PolicyEngine.create policies

        // Within budget — passes
        let usage = { ResourceUsage.Zero with EstimatedCostUsd = 2.0m }
        let ctx = { AgentId = agentId; Action = "execute"; Input = None; ExecutionId = None; CurrentUsage = Some usage }
        let result = engine.Evaluate(ctx)
        Assert.IsTrue(result.Proceed)

        // Over budget — blocked
        let overBudget = { ResourceUsage.Zero with EstimatedCostUsd = 6.0m }
        let ctx2 = { AgentId = agentId; Action = "execute"; Input = None; ExecutionId = None; CurrentUsage = Some overBudget }
        let result2 = engine.Evaluate(ctx2)
        Assert.IsFalse(result2.Proceed)
        Assert.IsTrue(result2.Violations |> List.exists (fun v -> v.PolicyId = "cost-budget"))


// =============================================================================
// Full ETCLOVG Harness Integration — All layers working together
// =============================================================================

[<TestClass>]
type EtclovgFullIntegrationTests() =

    let makeAgent response : IAgent =
        { new IAgent with
            member _.Id = { Name = "full-demo-agent"; Description = "Full ETCLOVG demo" }
            member _.RunAsync(_) = Task.FromResult response
            member _.HandleMessageAsync(_) = Task.FromResult None }

    [<TestMethod>]
    member _.CompleteHarnessExecution_AllLayersActive() =
        // This test demonstrates ALL seven ETCLOVG layers working together
        let agentId = { Name = "full-demo-agent"; Description = "Full ETCLOVG demo" }

        // E: Execution environment with resource bounds
        let sandbox =
            { SandboxConfig.Default with
                Limits = ResourceLimits.Constrained 60 50 100000 }

        // T: Tool protocol
        let _protocol = ToolProtocol.fromTools [ EtclovgDemoTools.stockPrice; EtclovgDemoTools.searchDocs ]

        // O: Observability
        let tracer = InMemory.tracer ()
        let metrics = InMemory.metrics ()

        // V: Verification
        let traceStore = InMemoryTraceStore() :> ITraceStore
        let readinessCheck =
            { new IReadinessCheck with
                member _.Name = "system-health"
                member _.CheckAsync _ _ = Task.FromResult ReadinessResult.Ready }

        // G: Governance
        let perms = PermissionModel.Permissive agentId
        let constitution =
            Constitution.empty "safety"
            |> Constitution.addRule Constitution.noPrivateDataRule
        let audit = InMemory.auditLog ()
        let policyEngine = PolicyEngine.create [ PolicyEngine.costBudgetPolicy 10.0m ]

        // L: Lifecycle hooks
        let lifecycleHook = PassthroughHook() :> ILifecycleHook

        // Assemble the full ETCLOVG configuration
        let config : EtclovgConfig =
            { Execution = sandbox
              ToolProtocol = None
              ExecutionJournal = None
              MemoryConfig = OrchestratorMemoryConfig.None
              Lifecycle = [ lifecycleHook ]
              Tracer = Some tracer
              Metrics = Some metrics
              Resilience = ResilienceConfig.Default
              ReadinessChecks = [ readinessCheck ]
              TraceStore = Some traceStore
              Judge = None
              Permissions = Some perms
              Constitution = Some constitution
              AuditLog = Some audit
              PolicyEngine = Some policyEngine
              Bus = EventBus.none
              Scope = EventScope.Empty }

        // Execute
        let agent = makeAgent "The current AAPL price is $189.45 based on latest market data."
        let result = (EtclovgHarness.runAsync config agent "What is the AAPL stock price?").Result

        // Verify success
        Assert.IsTrue(result.Success, sprintf "Expected success but got error: %A" result.HarnessError)
        Assert.AreEqual(Some "The current AAPL price is $189.45 based on latest market data.", result.Response)

        // E: Resource usage tracked
        Assert.IsTrue(result.Usage.ElapsedTime > TimeSpan.Zero)

        // O: Metrics collected
        Assert.IsTrue(result.Metrics.IsSome)
        Assert.AreEqual(1, result.Metrics.Value.TotalLlmCalls)

        // V: Trace stored
        Assert.IsTrue(result.Trace.IsSome)
        Assert.IsTrue(result.Trace.Value.Success)
        let storedTraces = (traceStore.GetTracesAsync agentId 10).Result
        Assert.AreEqual(1, storedTraces.Length)

        // G: Audit recorded
        Assert.IsTrue(result.AuditEntries > 0)
        let auditEntries = (audit.QueryAsync agentId (DateTimeOffset.UtcNow.AddMinutes(-1.0))).Result
        Assert.IsTrue(auditEntries.Length > 0)

        // G: No policy/constitution violations
        Assert.AreEqual(0, result.PolicyViolations.Length)
        Assert.AreEqual(0, result.ConstitutionViolations.Length)

    [<TestMethod>]
    member _.HarnessBlocksDangerousOutput() =
        // Agent produces output containing PII — constitution should block it
        let agent = makeAgent "Please contact support at admin@internal.corp for help."
        let agentId = { Name = "full-demo-agent"; Description = "Full ETCLOVG demo" }

        let config =
            { EtclovgConfig.Default with
                Constitution = Some (
                    Constitution.empty "safety"
                    |> Constitution.addRule Constitution.noPrivateDataRule)
                AuditLog = Some (InMemory.auditLog ())
                Lifecycle = [ PassthroughHook() :> ILifecycleHook ] }

        let result = (EtclovgHarness.runAsync config agent "How do I get help?").Result

        Assert.IsFalse(result.Success)
        Assert.IsTrue(result.HarnessError.Value.Message.Contains("Output violates constitution"))
        Assert.IsTrue(result.ConstitutionViolations.Length > 0)
        Assert.IsTrue(result.ConstitutionViolations |> List.exists (fun v -> v.RuleId = "privacy-no-pii"))

    [<TestMethod>]
    member _.HarnessEnforcesCostBudget() =
        let agent = makeAgent "response"
        let agentId = { Name = "full-demo-agent"; Description = "Full ETCLOVG demo" }

        // Zero budget policy — should block immediately
        let config =
            { EtclovgConfig.Default with
                PolicyEngine = Some (PolicyEngine.create [
                    { Id = "zero-budget"
                      Description = "No budget remaining"
                      Enforcement = PolicyEnforcement.Block
                      Evaluate = fun _ -> Some "Budget exhausted" }
                ]) }

        let result = (EtclovgHarness.runAsync config agent "do something").Result

        Assert.IsFalse(result.Success)
        Assert.IsTrue(result.HarnessError.Value.Message.Contains("Blocked by policy"))
        Assert.AreEqual(1, result.PolicyViolations.Length)

    [<TestMethod>]
    member _.HarnessWithReadinessGate() =
        // Readiness check fails — execution should not proceed
        let agent = makeAgent "should not reach"

        let failedCheck =
            { new IReadinessCheck with
                member _.Name = "required-model"
                member _.CheckAsync _ _ =
                    Task.FromResult(ReadinessResult.NotReady ["LLM endpoint unavailable"; "Vector store not initialized"]) }

        let config =
            { EtclovgConfig.Default with
                ReadinessChecks = [ failedCheck ]
                Lifecycle = [ PassthroughHook() :> ILifecycleHook ] }

        let result = (EtclovgHarness.runAsync config agent "query").Result

        Assert.IsFalse(result.Success)
        Assert.IsTrue(result.HarnessError.Value.Message.Contains("LLM endpoint unavailable"))
        Assert.IsTrue(result.HarnessError.Value.Message.Contains("Vector store not initialized"))

    [<TestMethod>]
    member _.EndToEndOrchestratorThroughHarness() =
        // Complete E2E: Orchestrator agent routed through ETCLOVG harness
        let provider = EtclovgMockProvider()
        let tools = [ EtclovgDemoTools.stockPrice; EtclovgDemoTools.searchDocs ]
        let orchestrator = NaoOrchestrator.create (provider :> ILlmProvider) tools []
        let agentId = orchestrator.Id

        let tracer = InMemory.tracer ()
        let metrics = InMemory.metrics ()
        let traceStore = InMemoryTraceStore() :> ITraceStore
        let audit = InMemory.auditLog ()

        let config =
            { EtclovgConfig.Default with
                Execution = { SandboxConfig.Default with Limits = ResourceLimits.Constrained 30 20 50000 }
                Tracer = Some tracer
                Metrics = Some metrics
                TraceStore = Some traceStore
                AuditLog = Some audit
                Permissions = Some (PermissionModel.Permissive agentId)
                Constitution = Some (Constitution.empty "basic" |> Constitution.addRule Constitution.noHarmRule)
                PolicyEngine = Some (PolicyEngine.create [ PolicyEngine.costBudgetPolicy 100.0m ])
                Lifecycle = [ PassthroughHook() :> ILifecycleHook ] }

        let result = (EtclovgHarness.runAsync config orchestrator "What is the stock price of AAPL?").Result

        // The orchestrator should have: called LLM -> invoked tool -> called LLM -> produced response
        Assert.IsTrue(result.Success, sprintf "Failed: %A" result.HarnessError)
        Assert.IsTrue(result.Response.IsSome)
        Assert.IsTrue(result.Response.Value.Contains("189.45") || result.Response.Value.Contains("AAPL"),
            sprintf "Expected stock data in response: %s" result.Response.Value)

        // Observability captured
        Assert.IsTrue(result.Metrics.IsSome)
        Assert.IsTrue(result.Metrics.Value.TotalLlmCalls >= 1)

        // Trace stored for future regression detection
        let traces = (traceStore.GetTracesAsync agentId 10).Result
        Assert.AreEqual(1, traces.Length)
        Assert.IsTrue(traces.[0].Success)
