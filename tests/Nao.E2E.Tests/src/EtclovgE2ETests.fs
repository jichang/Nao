namespace Nao.E2E.Tests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Persistence
open Nao.Agents

module private EtclovgHarness =
    let runAsync config context agent request =
        Nao.Agents.EtclovgHarness.runAsync config context agent request System.Threading.CancellationToken.None

// =============================================================================
// ETCLOVG Architecture E2E Tests
// Complete examples demonstrating all seven layers working together
// =============================================================================

/// Demo tools with richer metadata for the tool protocol layer
module EtclovgDemoTools =

    let private createText name description execute =
        Tool.create name description 0 [] ToolCodec.text ToolCodec.text (ToolOperation.create execute)

    let stockPrice =
        createText "get_stock_price" "Get the current stock price for a ticker symbol" (fun _ ticker ->
            let price =
                match ticker.Trim().ToUpper() with
                | "AAPL" -> "189.45"
                | "MSFT" -> "420.12"
                | "GOOGL" -> "175.30"
                | value -> sprintf "Unknown ticker: %s" value

            Task.FromResult(Ok(sprintf """{"ticker":"%s","price":%s,"currency":"USD"}""" (ticker.ToUpper()) price)))

    let sendEmail =
        createText
            "send_email"
            "Send an email to a recipient. Input format: 'to@email.com|subject|body'"
            (fun _ input ->
                let parts = input.Split('|')

                let output =
                    if parts.Length >= 3 then
                        sprintf "Email sent to %s with subject '%s'" parts.[0] parts.[1]
                    else
                        "Error: invalid email format"

                Task.FromResult(Ok output))

    let searchDocs =
        createText "search_docs" "Search internal documentation. Returns relevant passages." (fun _ query ->
            Task.FromResult(
                Ok(sprintf "Found 3 results for '%s': [1] Getting Started Guide [2] API Reference [3] FAQ" query)
            ))

    let dangerousDelete =
        createText "delete_all_data" "Permanently delete all data. DANGEROUS - requires confirmation." (fun _ _ ->
            Task.FromResult(Ok "All data deleted permanently"))

    let allTools = [ stockPrice; sendEmail; searchDocs; dangerousDelete ]


/// Mock provider that simulates LLM behavior for ETCLOVG demos
module EtclovgMockProvider =
    let create () =
        let mutable callCount = 0

        LlmProvider.create
            (fun () -> "EtclovgMock")
            (fun _ (conversation: Conversation) (_options: CompletionOptions) ->
                callCount <- callCount + 1

                let lastMsg =
                    conversation
                    |> List.tryFindBack (fun message -> message.Role = User)
                    |> Option.map (fun message -> message.Content)
                    |> Option.defaultValue ""

                let response =
                    if lastMsg.Contains("[Tool Result") || lastMsg.Contains("[Agent Result") then
                        let result = lastMsg.Split("]:") |> Array.last |> (fun value -> value.Trim())
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

                Task.FromResult(CompletionResult.create response "stop" (Some 150) None))

    let createOrchestrator tools =
        let parseActions (response: string) =
            if response.TrimStart().StartsWith("{") then
                use document = JsonDocument.Parse(response)
                let root = document.RootElement
                let action = root.GetProperty("action").GetString()
                let name = root.GetProperty("name").GetString()
                let input = root.GetProperty("input").GetString()

                match action with
                | "tool" -> [ InvokeTool(name, input) ]
                | "delegate" -> [ DelegateToAgent(name, input) ]
                | _ -> []
            else
                [ Respond response ]

        let config =
            { Id = "etclovg-orchestrator"
              Name = "ETCLOVG orchestrator"
              Description = "Exercises Nao orchestration through the ETCLOVG harness."
              Priority = 0
              Responsibilities = []
              Contract = AgentContract.Text
              Provider = create ()
              Tools = tools
              SubAgents = []
              Prompt = Prompt.Empty
              Options = CompletionOptions.Default
              MaxRounds = 5
              Bus = EventBus.none
              Scope = EventScope.CreateEmpty() }

        let definition =
            { OrchestratorDefinition.create Task.FromResult with
                ParseActions = parseActions }

        Orchestrator.create config definition


// =============================================================================
// E: Execution Environment — Resource-bounded agent execution
// =============================================================================

[<TestClass>]
type EtclovgExecutionTests() =

    let makeAgent response =
        Agent.create
            "bounded-agent"
            "bounded-agent"
            "Agent with resource bounds"
            0
            []
            AgentContract.Text
            (fun _context _input -> Task.FromResult response)

    [<TestMethod>]
    member _.AgentRunsWithinResourceBudget() =
        // Configure a sandbox with generous limits
        let limits = ResourceLimits.Constrained 300 100 50000

        let sandbox =
            { SandboxConfig.Default with
                Limits = limits }

        let ctx = ExecutionContext.Create sandbox
        let agent = makeAgent "resource-bounded output"
        let env = ExecutionEnvironment.local ()

        let result =
            (env.ExecuteAsync ctx (AgentContext.unrestrictedForTests ()) agent "process this").Result

        match result with
        | Ok response -> Assert.AreEqual("resource-bounded output", response)
        | Error exceeded -> Assert.Fail(sprintf "Unexpected limit exceeded: %A" exceeded)

    [<TestMethod>]
    member _.AgentBlockedWhenLlmCallsExceedLimit() =
        // Configure strict limits: only 1 LLM call allowed
        let limits =
            { ResourceLimits.Unlimited with
                MaxLlmCalls = 1 }

        let sandbox =
            { SandboxConfig.Default with
                Limits = limits }

        let ctx = ExecutionContext.Create sandbox
        // Simulate that 2 LLM calls were already made
        ctx.RecordLlmCall(500, 0.01m)
        ctx.RecordLlmCall(500, 0.01m)

        let agent = makeAgent "should not reach"
        let env = ExecutionEnvironment.local ()

        let result =
            (env.ExecuteAsync ctx (AgentContext.unrestrictedForTests ()) agent "query").Result

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
        let stockTool = (protocol.GetTool "get_stock_price").Result
        Assert.IsTrue(stockTool.IsSome)
        Assert.AreEqual("Get the current stock price for a ticker symbol", stockTool.Value.Description)

        // Invoke tool through protocol
        let result =
            (protocol.InvokeAsync (AgentContext.unrestrictedForTests ()) "get_stock_price" "MSFT").Result

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
            let result =
                (protocol.InvokeAsync (AgentContext.unrestrictedForTests ()) "get_stock_price" "AAPL").Result

            Assert.IsTrue(result.Success)

        // 6th call should be blocked
        let blocked =
            (protocol.InvokeAsync (AgentContext.unrestrictedForTests ()) "get_stock_price" "AAPL").Result

        Assert.IsFalse(blocked.Success)
        Assert.IsTrue(blocked.Error.Value.Contains("Rate limit"))

// =============================================================================
// C: Context & Memory — Tiered memory and context compaction
// =============================================================================

[<TestClass>]
type EtclovgContextMemoryTests() =

    [<TestMethod>]
    member _.ContextCompactionKeepsRecentMessages() =
        // Simulate a long conversation that exceeds token budget
        let conversation =
            [ for i in 1..50 ->
                  { Role = (if i % 2 = 0 then Assistant else User)
                    Content =
                      sprintf "Message number %d with some additional content to take up space in the context window" i } ]

        let totalTokens = ContextCompaction.estimateConversationTokens conversation
        Assert.IsTrue(totalTokens > 100) // ensure it's over budget

        // Apply drop-oldest strategy with tight budget
        let result =
            (ContextCompaction.applyAsync (CorrelationContext.root ()) CompactionStrategy.DropOldest 200 conversation)
                .Result

        Assert.IsTrue(result.MessagesRemoved > 0)
        Assert.IsTrue(result.TokensSaved > 0)
        // Recent messages should be preserved
        let lastKept = result.Compacted |> List.last
        Assert.AreEqual(conversation |> List.last, lastKept)

    [<TestMethod>]
    member _.TieredMemoryOrganizesData() =
        // Create memories at different tiers
        let shortTerm: TieredMemoryEntry =
            { Owner = "tiered-memory-e2e"
              Key = "current-task"
              Value = "answering user question about stocks"
              Tier = MemoryTier.ShortTerm
              Timestamp = DateTimeOffset.UtcNow
              AccessCount = 1
              Relevance = 1.0
              Tags = [ "context" ] }

        let midTerm: TieredMemoryEntry =
            { Owner = "tiered-memory-e2e"
              Key = "user-preference"
              Value = "prefers brief responses"
              Tier = MemoryTier.MidTerm
              Timestamp = DateTimeOffset.UtcNow.AddMinutes(-30.0)
              AccessCount = 5
              Relevance = 0.8
              Tags = [ "preference" ] }

        let longTerm: TieredMemoryEntry =
            { Owner = "tiered-memory-e2e"
              Key = "user-name"
              Value = "Alice"
              Tier = MemoryTier.LongTerm
              Timestamp = DateTimeOffset.UtcNow.AddDays(-30.0)
              AccessCount = 50
              Relevance = 0.9
              Tags = [ "identity" ] }

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

    let agentId = "lifecycle-demo"

    [<TestMethod>]
    member _.FullLifecycleTransitions() =
        // Demonstrate the complete lifecycle of an agent execution
        let lifecycle =
            AgentLifecycle.create ()
            |> AgentLifecycle.withHooks [ LifecycleHook.passthrough ]

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
        let completed =
            (AgentLifecycle.completeAsync agentId "task done successfully" resumed).Result

        Assert.AreEqual(LifecycleState.Completed, completed.State)

        // Verify full event history
        Assert.AreEqual(5, completed.Events.Length)

    [<TestMethod>]
    member _.OrchestratorWithToolProtocolIntegration() =
        // Show the Orchestrator using ToolProtocol for structured tool management
        let tools = [ EtclovgDemoTools.stockPrice; EtclovgDemoTools.searchDocs ]
        let protocol = ToolProtocol.fromTools tools

        // Verify tools are discoverable
        let schemas = protocol.ListTools().Result
        Assert.AreEqual(2, schemas.Length)

        // Create orchestrator with these tools
        let orchestrator = EtclovgMockProvider.createOrchestrator tools

        let result =
            (Agent.runAsync (AgentContext.unrestrictedForTests ()) "What is the stock price of AAPL?" orchestrator)
                .Result

        match result with
        | Ok output -> Assert.IsTrue(output.Contains("189.45") || output.Contains("AAPL"))
        | Error failure -> Assert.Fail(failure.Message)


// =============================================================================
// O: Observability — Tracing, metrics, and resilience
// =============================================================================

[<TestClass>]
type EtclovgObservabilityTests() =

    [<TestMethod>]
    member _.DistributedTracingAcrossAgentCalls() =
        let tracer = InMemory.tracer ()
        let correlation = CorrelationContext.root ()

        // Root span: user request arrives
        let rootSpan = tracer.StartTrace correlation "user-request"
        tracer.SetAttributes rootSpan (Map.ofList [ "user.id", "alice"; "request.type", "stock-query" ])

        // Child span: orchestrator processing
        let orchestratorSpan = tracer.StartSpan rootSpan "orchestrator.process"
        tracer.AddEvent orchestratorSpan "routing-decision" (Map.ofList [ "selected-tool", "get_stock_price" ])

        // Grandchild span: tool invocation
        let toolSpan = tracer.StartSpan orchestratorSpan "tool.invoke.get_stock_price"
        tracer.SetAttributes toolSpan (Map.ofList [ "tool.input", "AAPL" ])
        // Simulate tool execution
        let toolResult =
            match
                EtclovgDemoTools.stockPrice.RunAsync (AgentContext.unrestrictedForTests ()) "AAPL"
                |> fun task -> task.Result
            with
            | Ok output -> output
            | Error failure ->
                Assert.Fail(failure.Message)
                ""

        tracer.AddEvent toolSpan "tool-result" (Map.ofList [ "output", toolResult ])
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
        let toolSpanResult =
            allSpans |> List.find (fun s -> s.OperationName.Contains("tool.invoke"))

        Assert.AreEqual(Some orchestratorSpan.Id, toolSpanResult.ParentSpanId)

    [<TestMethod>]
    member _.MetricsTrackCostAndLatency() =
        let metrics = InMemory.metrics ()
        let owner = "e2e/metrics"
        let startedAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        let correlation = CorrelationContext.root ()

        // Simulate a multi-step agent execution
        metrics.Record(MetricRecord.llmCall correlation owner startedAt 500 200 150L) // First LLM call: routing decision
        metrics.Record(MetricRecord.toolCall correlation owner (startedAt.AddSeconds 1) "get_stock_price" 25L true)
        metrics.Record(MetricRecord.llmCall correlation owner (startedAt.AddSeconds 2) 800 300 200L) // Second LLM call: format response
        metrics.Record(MetricRecord.llmCall correlation owner (startedAt.AddSeconds 3) 200 100 100L) // Third: summarize

        let summary = metrics.GetMetrics owner
        Assert.AreEqual(3, summary.TotalLlmCalls)
        Assert.AreEqual(1500, summary.TotalInputTokens)
        Assert.AreEqual(600, summary.TotalOutputTokens)
        Assert.AreEqual(1, summary.TotalToolCalls)
        Assert.AreEqual(150.0, summary.AvgLatencyMs)

        let costModel: CostModel =
            { InputCostPer1K = 0.0025m
              OutputCostPer1K = 0.01m }

        let cost = metrics.EstimateCost owner costModel
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
                RetryPolicy = RetryPolicy.Fixed(3, 50)
                Fallback = FallbackStrategy.None }

        let result =
            (Resilience.executeAsync config None unreliableService "get data").Result

        match result with
        | Ok value ->
            Assert.AreEqual("Success: get data", value)
            Assert.AreEqual(3, callCount) // 2 failures + 1 success
        | Error msg -> Assert.Fail(sprintf "Expected success after retries, got: %s" msg)

    [<TestMethod>]
    member _.CircuitBreakerProtectsFromCascadingFailure() =
        let cbConfig =
            { FailureThreshold = 3
              OpenDuration = TimeSpan.FromMilliseconds(100.0)
              SuccessThreshold = 1 }

        let cb = CircuitBreaker.create cbConfig

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

        let result =
            (Resilience.executeAsync config (Some cb) (fun _ -> failwith "unreachable") "query").Result

        match result with
        | Ok value -> Assert.AreEqual("cached result", value)
        | Error _ -> Assert.Fail("Expected fallback value")


// =============================================================================
// V: Verification & Evaluation — Readiness, tracing, regression
// =============================================================================

[<TestClass>]
type EtclovgVerificationTests() =

    let agentId = "verified-agent"

    [<TestMethod>]
    member _.ReadinessChecksValidatePrerequisites() =
        // Define readiness checks that validate the agent's environment
        let toolCheck =
            ReadinessCheck.create "required-tools" (fun _agentId _input ->
                // Check that required tools are available
                let protocol = ToolProtocol.fromTools EtclovgDemoTools.allTools

                task {
                    let! available = protocol.IsAvailable "get_stock_price"

                    if available then
                        return ReadinessResult.Ready
                    else
                        return ReadinessResult.NotReady [ "get_stock_price tool not available" ]
                })

        let budgetCheck =
            ReadinessCheck.create "cost-budget" (fun _agentId _input ->
                // Verify cost budget hasn't been exhausted
                Task.FromResult ReadinessResult.Ready)

        let result =
            (Verification.checkReadiness [ toolCheck; budgetCheck ] agentId "check stocks").Result

        Assert.AreEqual(ReadinessResult.Ready, result)

    [<TestMethod>]
    member _.ExecutionTraceCapturesFullHistory() =
        // Start a trace for an execution
        let trace =
            Verification.startTrace (CorrelationContext.root ()) agentId "What is AAPL stock price?"

        // Record each step
        let trace =
            trace
            |> Verification.addStep
                (TraceAction.LlmCall "gpt-4o")
                "user query"
                """{"action":"tool","name":"get_stock_price","input":"AAPL"}"""
                150L

        let trace =
            trace
            |> Verification.addStep
                (TraceAction.ToolInvocation "get_stock_price")
                "AAPL"
                """{"ticker":"AAPL","price":189.45}"""
                25L

        let trace =
            trace
            |> Verification.addStep
                (TraceAction.LlmCall "gpt-4o")
                "tool result"
                "The current price of AAPL is $189.45"
                120L

        let trace = trace |> Verification.complete "The current price of AAPL is $189.45"

        Assert.IsTrue(trace.Success)
        Assert.AreEqual(3, trace.Steps.Length)
        Assert.AreEqual(Some "The current price of AAPL is $189.45", trace.Output)
        // Total duration across steps
        let totalDuration = trace.Steps |> List.sumBy (fun s -> s.DurationMs)
        Assert.AreEqual(295L, totalDuration)

    [<TestMethod>]
    member _.RegressionDetectionComparesBaselines() =
        let store = InMemoryTraceStore.create ()

        // Save a baseline trace (fast, 2 steps)
        let baseline =
            Verification.startTrace (CorrelationContext.root ()) agentId "get AAPL price"
            |> Verification.addStep (TraceAction.LlmCall "model") "" "" 100L
            |> Verification.addStep (TraceAction.ToolInvocation "get_stock_price") "" "" 20L
            |> Verification.complete "$189.45"

        let baseline =
            { baseline with
                StartedAt = DateTimeOffset.UtcNow.AddHours(-1.0)
                CompletedAt = Some(DateTimeOffset.UtcNow.AddHours(-1.0).AddMilliseconds(120.0)) }

        store.SaveAsync(baseline).Wait()

        // New execution is much slower with more steps
        let current =
            Verification.startTrace (CorrelationContext.root ()) agentId "get AAPL price"
            |> Verification.addStep (TraceAction.LlmCall "model") "" "" 500L
            |> Verification.addStep (TraceAction.LlmCall "model") "" "" 300L
            |> Verification.addStep (TraceAction.LlmCall "model") "" "" 400L
            |> Verification.addStep (TraceAction.ToolInvocation "get_stock_price") "" "" 20L
            |> Verification.addStep (TraceAction.LlmCall "model") "" "" 600L
            |> Verification.complete "$189.45"

        let current =
            { current with
                StartedAt = DateTimeOffset.UtcNow
                CompletedAt = Some(DateTimeOffset.UtcNow.AddMilliseconds(1820.0)) }

        // Detect regression
        let regression = Regression.detect baseline current
        Assert.IsTrue(regression.IsRegression)

        Assert.IsTrue(
            regression.Regressions
            |> List.exists (fun r -> r.Category = RegressionCategory.Latency)
        )


// =============================================================================
// G: Governance & Security — Permissions, constitution, audit, policies
// =============================================================================

[<TestClass>]
type EtclovgGovernanceTests() =

    let agentId = "governed-agent"

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
                  Check = fun content -> content.Contains("you should buy") || content.Contains("sell immediately") }
            |> Constitution.addRule
                { Id = "professional-tone"
                  Description = "Maintain professional tone in all communications"
                  Category = RuleCategory.Behavioral
                  Priority = 30
                  IsHardConstraint = false
                  Check = fun content -> content.Contains("lol") || content.Contains("lmao") }

        // Safe output passes
        let safeResult =
            Constitution.check
                constitution
                "The current price of AAPL is $189.45. Past performance does not guarantee future results."

        Assert.IsTrue(safeResult.Passed)

        // Financial advice blocked
        let adviceResult =
            Constitution.check constitution "Based on the trend, you should buy AAPL immediately."

        Assert.IsFalse(adviceResult.Passed)
        Assert.IsTrue(Constitution.hasHardViolations adviceResult)

        Assert.IsTrue(
            adviceResult.Violations
            |> List.exists (fun v -> v.RuleId = "no-financial-advice")
        )

        // PII blocked
        let piiResult =
            Constitution.check constitution "The user's email is alice@company.com"

        Assert.IsFalse(piiResult.Passed)
        Assert.IsTrue(Constitution.hasHardViolations piiResult)

        // Informal tone is soft violation (doesn't block)
        let informalResult = Constitution.check constitution "lol that's a good price"
        Assert.IsFalse(informalResult.Passed)
        Assert.IsFalse(Constitution.hasHardViolations informalResult) // soft constraint

    [<TestMethod>]
    member _.AuditLogTracksAllActions() =
        let audit = InMemory.auditLog ()
        let execId = ExecutionId.generate ()

        // Record a sequence of actions
        audit.RecordAsync(AuditLog.llmCall agentId "gpt-4o" (Some execId)).Wait()

        audit
            .RecordAsync(
                AuditLog.toolInvocation
                    agentId
                    "get_stock_price"
                    "AAPL"
                    """{"price":189.45}"""
                    true
                    PermissionDecision.Allow
                    (Some execId)
            )
            .Wait()

        audit
            .RecordAsync(
                AuditLog.toolInvocation
                    agentId
                    "delete_all_data"
                    "confirm"
                    ""
                    false
                    PermissionDecision.Deny
                    (Some execId)
            )
            .Wait()

        // Query all entries for this execution
        let entries = (audit.QueryByExecutionAsync execId).Result
        Assert.AreEqual(3, entries.Length)

        // Check denied count
        let deniedCount =
            (audit.GetDeniedCountAsync agentId (DateTimeOffset.UtcNow.AddMinutes(-1.0))).Result

        Assert.AreEqual(1, deniedCount)

    [<TestMethod>]
    member _.PolicyEngineEnforcesBudgetAndRateLimits() =
        let policies =
            [ PolicyEngine.costBudgetPolicy 5.0m
              PolicyEngine.rateLimitPolicy "tool_call" 10 ]

        let engine = PolicyEngine.create policies

        // Within budget — passes
        let usage =
            { ResourceUsage.Zero with
                EstimatedCostUsd = 2.0m }

        let ctx =
            { AgentId = agentId
              Action = "execute"
              Input = None
              ExecutionId = None
              CurrentUsage = Some usage }

        let result = engine.Evaluate(ctx)
        Assert.IsTrue(result.Proceed)

        // Over budget — blocked
        let overBudget =
            { ResourceUsage.Zero with
                EstimatedCostUsd = 6.0m }

        let ctx2 =
            { AgentId = agentId
              Action = "execute"
              Input = None
              ExecutionId = None
              CurrentUsage = Some overBudget }

        let result2 = engine.Evaluate(ctx2)
        Assert.IsFalse(result2.Proceed)
        Assert.IsTrue(result2.Violations |> List.exists (fun v -> v.PolicyId = "cost-budget"))


// =============================================================================
// Full ETCLOVG Harness Integration — All layers working together
// =============================================================================

[<TestClass>]
type EtclovgFullIntegrationTests() =

    let request (sandbox: SandboxConfig) (context: AgentContext) (agent: Agent) input =
        let userId, sessionId =
            match context.SessionKey.Split('/', 2) with
            | [| userId; sessionId |] -> userId, sessionId
            | _ -> "e2e", "session"

        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse userId) []

        let authorization =
            AuthorizationScope.tryCreate
                principal
                None
                (WorkspaceId.parse "workspace")
                (Some(SessionId.parse sessionId))
            |> Option.get

        ExecutionRequest.create
            authorization
            (context.TurnId |> TurnId.tryParse |> Option.defaultWith TurnId.generate)
            "default"
            agent.Metadata.Id
            input
            sandbox
            Map.empty
            Map.empty
            context.Correlation

    let makeAgent response =
        Agent.create
            "full-demo-agent"
            "full-demo-agent"
            "Full ETCLOVG demo"
            0
            []
            AgentContract.Text
            (fun _context _input -> Task.FromResult response)

    [<TestMethod>]
    member _.CompleteHarnessExecution_AllLayersActive() =
        // This test demonstrates ALL seven ETCLOVG layers working together
        let agentId = "full-demo-agent"

        // E: Execution environment with resource bounds
        let sandbox =
            { SandboxConfig.Default with
                Limits = ResourceLimits.Constrained 60 50 100000 }

        // T: Tool protocol
        let _protocol =
            ToolProtocol.fromTools [ EtclovgDemoTools.stockPrice; EtclovgDemoTools.searchDocs ]

        // O: Observability
        let tracer = InMemory.tracer ()
        let metrics = InMemory.metrics ()

        // V: Verification
        let traceStore = InMemoryTraceStore.create ()

        let readinessCheck =
            ReadinessCheck.create "system-health" (fun _ _ -> Task.FromResult ReadinessResult.Ready)

        // G: Governance
        let constitution =
            Constitution.empty "safety"
            |> Constitution.addRule Constitution.noPrivateDataRule

        let audit = InMemory.auditLog ()
        let policyEngine = PolicyEngine.create [ PolicyEngine.costBudgetPolicy 10.0m ]

        // L: Lifecycle hooks
        let lifecycleHook = LifecycleHook.passthrough

        // Assemble the full ETCLOVG configuration
        let config: EtclovgConfig =
            { EtclovgConfig.Default with
                Lifecycle = [ lifecycleHook ]
                Tracer = Some tracer
                Metrics = Some metrics
                Resilience = ResilienceConfig.Default
                ReadinessChecks = [ readinessCheck ]
                TraceStore = Some traceStore
                Constitution = Some constitution
                AuditLog = Some audit
                PolicyEngine = Some policyEngine }

        // Execute
        let agent =
            makeAgent "The current AAPL price is $189.45 based on latest market data."

        let context =
            { (AgentContext.unrestrictedForTests ()) with
                SessionKey = "e2e/full-harness" }

        let result =
            (EtclovgHarness.runAsync
                config
                context
                agent
                (request sandbox context agent "What is the AAPL stock price?"))
                .Result

        // Verify success
        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)

        Assert.AreEqual(Some "The current AAPL price is $189.45 based on latest market data.", result.Outputs.Response)

        // E: Resource usage tracked
        Assert.IsTrue(result.Usage.ElapsedTime > TimeSpan.Zero)

        // O: Metrics collected
        Assert.IsTrue(result.Evidence.Metrics.IsSome)
        Assert.AreEqual(0, result.Evidence.Metrics.Value.TotalLlmCalls)

        // V: Trace stored
        Assert.IsTrue(result.Evidence.Trace.IsSome)
        Assert.IsTrue(result.Evidence.Trace.Value.Success)
        let storedTraces = (traceStore.GetTracesAsync agentId 10).Result
        Assert.AreEqual(1, storedTraces.Length)

        // G: Audit recorded
        Assert.IsTrue(result.Evidence.AuditEntries > 0)

        let auditEntries =
            (audit.QueryAsync agentId (DateTimeOffset.UtcNow.AddMinutes(-1.0))).Result

        Assert.IsTrue(auditEntries.Length > 0)

        // G: No policy/constitution violations
        Assert.AreEqual(0, result.PolicyDecisions.PolicyViolations.Length)
        Assert.AreEqual(0, result.PolicyDecisions.ConstitutionViolations.Length)

    [<TestMethod>]
    member _.HarnessBlocksDangerousOutput() =
        // Agent produces output containing PII — constitution should block it
        let agent = makeAgent "Please contact support at admin@internal.corp for help."
        let agentId = "full-demo-agent"

        let config =
            { EtclovgConfig.Default with
                Constitution =
                    Some(
                        Constitution.empty "safety"
                        |> Constitution.addRule Constitution.noPrivateDataRule
                    )
                AuditLog = Some(InMemory.auditLog ())
                Lifecycle = [ LifecycleHook.passthrough ] }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync
                config
                context
                agent
                (request SandboxConfig.Default context agent "How do I get help?"))
                .Result

        match result.Status with
        | ExecutionTerminalStatus.Denied(HarnessError.ConstitutionViolation _) -> ()
        | status -> Assert.Fail(sprintf "Expected constitution denial, got %A" status)

        Assert.IsTrue(result.PolicyDecisions.ConstitutionViolations.Length > 0)

        Assert.IsTrue(
            result.PolicyDecisions.ConstitutionViolations
            |> List.exists (fun v -> v.RuleId = "privacy-no-pii")
        )

    [<TestMethod>]
    member _.HarnessEnforcesCostBudget() =
        let agent = makeAgent "response"
        let agentId = "full-demo-agent"

        // Zero budget policy — should block immediately
        let config =
            { EtclovgConfig.Default with
                PolicyEngine =
                    Some(
                        PolicyEngine.create
                            [ { Id = "zero-budget"
                                Description = "No budget remaining"
                                Enforcement = PolicyEnforcement.Block
                                Evaluate = fun _ -> Some "Budget exhausted" } ]
                    ) }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context agent (request SandboxConfig.Default context agent "do something"))
                .Result

        Assert.AreEqual(
            ExecutionTerminalStatus.Denied(HarnessError.PolicyBlocked [ "Budget exhausted" ]),
            result.Status
        )

        Assert.AreEqual(1, result.PolicyDecisions.PolicyViolations.Length)

    [<TestMethod>]
    member _.HarnessWithReadinessGate() =
        // Readiness check fails — execution should not proceed
        let agent = makeAgent "should not reach"

        let failedCheck =
            ReadinessCheck.create "required-model" (fun _ _ ->
                Task.FromResult(
                    ReadinessResult.NotReady [ "LLM endpoint unavailable"; "Vector store not initialized" ]
                ))

        let config =
            { EtclovgConfig.Default with
                ReadinessChecks = [ failedCheck ]
                Lifecycle = [ LifecycleHook.passthrough ] }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context agent (request SandboxConfig.Default context agent "query")).Result

        Assert.AreEqual(
            ExecutionTerminalStatus.Failed(
                HarnessError.NotReady [ "LLM endpoint unavailable"; "Vector store not initialized" ]
            ),
            result.Status
        )

    [<TestMethod>]
    member _.EndToEndOrchestratorThroughHarness() =
        // Complete E2E: Orchestrator agent routed through ETCLOVG harness
        let tools = [ EtclovgDemoTools.stockPrice; EtclovgDemoTools.searchDocs ]
        let orchestrator = EtclovgMockProvider.createOrchestrator tools
        let agentId = orchestrator.Metadata.Id

        let tracer = InMemory.tracer ()
        let metrics = InMemory.metrics ()
        let traceStore = InMemoryTraceStore.create ()
        let audit = InMemory.auditLog ()

        let sandbox =
            { SandboxConfig.Default with
                Limits = ResourceLimits.Constrained 30 20 50000 }

        let config =
            { EtclovgConfig.Default with
                Tracer = Some tracer
                Metrics = Some metrics
                TraceStore = Some traceStore
                AuditLog = Some audit
                Constitution = Some(Constitution.empty "basic" |> Constitution.addRule Constitution.noHarmRule)
                PolicyEngine = Some(PolicyEngine.create [ PolicyEngine.costBudgetPolicy 100.0m ])
                Lifecycle = [ LifecycleHook.passthrough ] }

        let context =
            { (AgentContext.unrestrictedForTests ()) with
                SessionKey = "e2e/orchestrator" }

        let result =
            (EtclovgHarness.runAsync
                config
                context
                orchestrator
                (request sandbox context orchestrator "What is the stock price of AAPL?"))
                .Result

        // The orchestrator should have: called LLM -> invoked tool -> called LLM -> produced response
        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.IsTrue(result.Outputs.Response.IsSome)

        Assert.IsTrue(
            result.Outputs.Response.Value.Contains("189.45")
            || result.Outputs.Response.Value.Contains("AAPL"),
            sprintf "Expected stock data in response: %s" result.Outputs.Response.Value
        )

        // Observability captured
        Assert.IsTrue(result.Evidence.Metrics.IsSome)
        Assert.IsTrue(result.Evidence.Metrics.Value.TotalLlmCalls >= 1)

        // Trace stored for future regression detection
        let traces = (traceStore.GetTracesAsync agentId 10).Result
        Assert.AreEqual(1, traces.Length)
        Assert.IsTrue(traces.[0].Success)

    [<TestMethod>]
    member _.OneExecutionReconstructsAcrossParticipatingStores() =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-correlation-" + Guid.NewGuid().ToString("N"))

        try
            let tools = [ EtclovgDemoTools.stockPrice; EtclovgDemoTools.searchDocs ]
            let orchestrator = EtclovgMockProvider.createOrchestrator tools
            let correlation = CorrelationContext.root ()
            let sessionKey = "e2e/correlation"
            let turnId = "turn-correlation"
            let tracer = Tracers.file root
            let metrics = MetricsCollectors.file root
            let journal = ExecutionJournals.file root
            let traceStore = TraceStores.file root
            let audit = AuditLogs.file root

            let sandbox =
                { SandboxConfig.Default with
                    Limits = ResourceLimits.Constrained 30 20 50000 }

            let config =
                { EtclovgConfig.Default with
                    Tracer = Some tracer
                    Metrics = Some metrics
                    ExecutionJournal = Some journal
                    TraceStore = Some traceStore
                    AuditLog = Some audit
                    Lifecycle = [ LifecycleHook.passthrough ] }

            let context =
                { (AgentContext.unrestrictedForTests ()) with
                    Correlation = correlation
                    SessionKey = sessionKey
                    TurnId = turnId }

            let result =
                EtclovgHarness.runAsync
                    config
                    context
                    orchestrator
                    (request sandbox context orchestrator "What is the stock price of AAPL?")
                |> _.GetAwaiter().GetResult()

            Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
            let executionId = result.Correlation.ExecutionId
            Assert.AreEqual(correlation.ExecutionId, executionId)

            let spans = (Tracers.file root).GetByExecution executionId
            let metricRecords = (MetricsCollectors.file root).GetByExecution executionId

            let journalRecords =
                (ExecutionJournals.file root).GetByExecutionAsync executionId |> _.Result

            let traces = (TraceStores.file root).GetByExecutionAsync executionId |> _.Result

            let auditEntries =
                (AuditLogs.file root).QueryByExecutionAsync executionId |> _.Result

            Assert.IsTrue(spans.Length > 0)
            Assert.IsTrue(metricRecords.Length > 0)
            Assert.IsTrue(journalRecords.Length > 0)
            Assert.IsTrue(traces.Length > 0)
            Assert.IsTrue(auditEntries.Length > 0)
            Assert.IsTrue(spans |> List.forall (fun span -> span.Correlation.ExecutionId = executionId))

            Assert.IsTrue(
                metricRecords
                |> List.forall (fun metric -> metric.Correlation.ExecutionId = executionId)
            )

            Assert.IsTrue(
                journalRecords
                |> List.forall (fun record -> record.Correlation.ExecutionId = executionId)
            )

            Assert.IsTrue(traces |> List.forall (fun trace -> trace.Correlation.ExecutionId = executionId))
            Assert.IsTrue(auditEntries |> List.forall (fun entry -> entry.ExecutionId = Some executionId))
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
