namespace Nao.Agents

open System
open System.Threading.Tasks

/// Memory management configuration for the orchestrator
type OrchestratorMemoryConfig =
    { /// Strategy for trimming conversation history before each LLM call
      WindowStrategy: WindowStrategy option
      /// Optional summarization config (uses LLM to condense old messages)
      Summarization: SummarizationConfig option
      /// Optional key-value memory store for cross-session facts
      MemoryStore: IMemoryStore option
      /// How many memories to inject into the system prompt context
      MaxMemoriesToInject: int }

    static member None =
        { WindowStrategy = Option.None
          Summarization = Option.None
          MemoryStore = Option.None
          MaxMemoriesToInject = 5 }

    static member WithWindow strategy =
        { OrchestratorMemoryConfig.None with WindowStrategy = Some strategy }

/// Configuration for the Orchestrator
type OrchestratorConfig = { Id: string; Name: string; Description: string; Priority: int; Capabilities: string list; Responsibilities: string list; Signature: ToolSignature; Provider: ILlmProvider; Tools: Tool list; SubAgents: IAgent list; Prompt: Prompt; Options: CompletionOptions; MaxRounds: int; Bus: IEventBus; Scope: EventScope; Memory: OrchestratorMemoryConfig; Context: ToolContext }

/// Factory interface for creating orchestrator instances via DI.
/// Register a custom implementation to control how orchestrators are built from workspace definitions.
type IOrchestratorFactory =
    /// Create an orchestrator (as IAgent) from the given configuration.
    abstract member Create: OrchestratorConfig -> IAgent

/// Runtime event context supplied by the host after a code-defined agent is selected.
/// This keeps workspace registration independent from per-session routing and recording.
type IRuntimeAgentContext =
    abstract member SetEventContext: IEventBus -> EventScope -> unit

/// The fundamental orchestrator agent.
/// Accepts user input, uses an LLM to decide which tool or sub-agent to invoke,
/// executes the action, feeds results back, and produces a final response.
/// Subclass and override virtual members to customize behavior.
[<AbstractClass>]
type OrchestratorBase(config: OrchestratorConfig) =
    let id = config.Id
    let mutable toolContext = config.Context
    let mutable eventBus = config.Bus
    let mutable eventScope = config.Scope

    let report (signal: ProgressSignal) =
        eventBus.PublishAsync(NaoEvent.TurnProgress(eventScope, signal)) |> ignore

    let getMemoryContext () : Task<string> =
        task {
            match config.Memory.MemoryStore with
            | Some store ->
                let! memories = store.RecallAllAsync id
                if memories.IsEmpty then return ""
                else
                    let relevant =
                        memories
                        |> List.sortByDescending (fun m -> m.Timestamp)
                        |> List.truncate config.Memory.MaxMemoriesToInject
                        |> List.map (fun m -> sprintf "  - [%s]: %s" m.Key m.Value)
                        |> String.concat "\n"
                    return sprintf "\n\n# Agent Memories\n%s" relevant
            | Option.None -> return ""
        }

    // Strip a single surrounding Markdown code fence (```lang ... ```), which models often
    // wrap structured JSON in despite being told not to.
    /// Publish a turn-progress signal (reasoning, tool/agent activity) onto the event bus.
    /// Subclasses call this from `Orchestrate` to surface their own reasoning to the UI.
    member _.Report(signal: ProgressSignal) = report signal

    /// The system-prompt memory context assembled from the configured memory store, or an
    /// empty string when no store is configured. Subclasses append this to their prompt.
    member _.GetMemoryContextAsync() : Task<string> = getMemoryContext ()

    /// The orchestrator configuration.
    member _.Config = config

    /// Tracing context (tracer + parent span) optionally injected by the harness so each
    /// tool invocation is recorded as a child span carrying the tool name, parameters, and
    /// round. `None` disables per-tool tracing (e.g. when no tracer is configured).
    member val TraceContext : (ITracer * Span) option = None with get, set

    /// Start a child span for a tool invocation, tagging it with the tool name, its
    /// parameters, the invoking agent, and the current round. Returns None when no tracer
    /// is wired so callers stay allocation-free in the untraced path.
    member private this.StartToolSpan (toolName: string) (toolInput: string) (round: int) : (ITracer * Span) option =
        match this.TraceContext with
        | Some (tracer, parent) ->
            let span = tracer.StartSpan parent "tool.invoke"
            let trimmedInput =
                if String.IsNullOrEmpty toolInput then ""
                elif toolInput.Length > 1000 then toolInput.Substring(0, 1000) + "…"
                else toolInput
            tracer.SetAttributes span (Map.ofList
                [ "tool.name", toolName
                  "tool.input", trimmedInput
                  "agent.name", config.Name
                  "round", string round ])
            Some (tracer, span)
        | None -> None

    /// Close a tool-invocation span with the given status, recording a `tool.result` event
    /// with any extra attributes (e.g. result size). No-op when tracing is disabled.
    member private _.EndToolSpan (span: (ITracer * Span) option) (status: SpanStatus) (resultAttrs: Map<string, string>) =
        match span with
        | Some (tracer, s) ->
            if not (Map.isEmpty resultAttrs) then tracer.AddEvent s "tool.result" resultAttrs
            tracer.EndSpan s status
        | None -> ()

    /// Start a child span for one planning round (the subclass's `Orchestrate` call), so the
    /// LLM planning step is always traced even when a custom orchestrator adds no spans of its
    /// own. Returns None when no tracer is wired.
    member private this.StartRoundSpan (round: int) : (ITracer * Span) option =
        match this.TraceContext with
        | Some (tracer, parent) ->
            let span = tracer.StartSpan parent "agent.plan"
            tracer.SetAttributes span (Map.ofList [ "agent.name", config.Name; "round", string round ])
            Some (tracer, span)
        | None -> None

    /// Close a planning-round span, recording an `agent.plan.result` event with any extra
    /// attributes (reasoning length, action count). No-op when tracing is disabled.
    member private _.EndRoundSpan (span: (ITracer * Span) option) (status: SpanStatus) (attrs: Map<string, string>) =
        match span with
        | Some (tracer, s) ->
            if not (Map.isEmpty attrs) then tracer.AddEvent s "agent.plan.result" attrs
            tracer.EndSpan s status
        | None -> ()

    /// Build the full message list to send to the LLM for this round. The base passes the
    /// running `conversation` (the user input, the model's own prior messages, and tool/agent
    /// results), so an implementation can prepend its system prompt and inject anything it
    /// needs. The base calls the LLM with the returned messages, so the call is always logged
    /// and traced regardless of the implementation.
    abstract member GenerateReasoningPrompt: conversation: Conversation -> Task<Conversation>

    /// Parse the LLM's raw response into the actions to execute. Return an empty list to treat
    /// the response as a plain final answer; return a single `Respond` to end the turn.
    abstract member ParseActions: response: string -> AgentAction list

    /// Return a validation error for the raw response, or None when it is acceptable. When it
    /// is Some, the base asks the model to repair the response (bounded) before parsing it.
    /// Defaults to accepting every response (no repair).
    abstract member ValidateResponse: response: string -> string option
    default _.ValidateResponse(_) = None

    /// Build the corrective instruction sent to the model when `ValidateResponse` returns an
    /// error. Defaults to a generic request to resend a corrected response; override to add
    /// format-specific guidance.
    abstract member BuildRepairMessage: error: string -> string
    default _.BuildRepairMessage(error) =
        sprintf "[System]: Your previous response was invalid: %s. Please re-send a corrected response." error

    /// Override to add custom logic after a tool executes.
    abstract member OnToolResult: toolName: string * input: string * result: string -> unit
    default _.OnToolResult(_, _, _) = ()

    /// Override to add custom logic after an agent round completes.
    abstract member OnRoundComplete: round: int * content: string -> unit
    default _.OnRoundComplete(_, _) = ()

    /// Run one planning round: build the prompt (`GenerateReasoningPrompt`), call the LLM,
    /// repair an invalid response up to a bounded number of times (`ValidateResponse` /
    /// `BuildRepairMessage`), report the reasoning, and trace the round. Centralised here so
    /// every subclass gets identical logging and tracing for free — a custom orchestrator only
    /// supplies the prompt and the parser and can never accidentally drop the round's trace.
    member private this.ReasonAsync (conversation: Conversation) (round: int) : Task<string> =
        task {
            let! messages = this.GenerateReasoningPrompt(conversation)
            let roundSpan = this.StartRoundSpan round
            let startLlmSpan (attempt: int) : (ITracer * Span) option =
                match this.TraceContext with
                | Some (tracer, parent) ->
                    let span = tracer.StartSpan parent "llm.call"
                    tracer.SetAttributes span (Map.ofList [ "agent.name", config.Name; "round", string round; "attempt", string attempt; "messages.count", string messages.Length ])
                    Some (tracer, span)
                | None -> None
            let endLlmSpan (span: (ITracer * Span) option) (status: SpanStatus) (outputLength: int) (elapsedMs: int64) =
                match span with
                | Some (tracer, child) ->
                    tracer.AddEvent child "llm.response" (Map.ofList [ "output.length", string outputLength; "latency.ms", string elapsedMs ])
                    tracer.EndSpan child status
                | None -> ()
            let recordExchange (attempt: int) (isRepair: bool) (prompt: Conversation) (result: string) =
                let messagesForStorage =
                    prompt
                    |> List.map (fun message -> (sprintf "%A" message.Role, message.Content))
                eventBus.PublishAsync(
                    NaoEvent.LlmExchangeRecorded(
                        eventScope,
                        { Round = round
                          Attempt = attempt
                          IsRepair = isRepair
                          Messages = messagesForStorage
                          Response = result }))
                |> ignore
            try
                let llmStarted = Diagnostics.Stopwatch.StartNew()
                let llmSpan = startLlmSpan 1
                let! result = LlmProvider.streamAsync config.Provider messages config.Options (fun _ -> ())
                llmStarted.Stop()
                endLlmSpan llmSpan SpanStatus.Ok result.Content.Length llmStarted.ElapsedMilliseconds
                recordExchange 1 false messages result.Content
                let mutable working = result.Content
                // Validate-and-repair: ask the model to correct an invalid response (bounded)
                // before it is parsed. `ValidateResponse` defaults to accepting everything.
                let maxRepairAttempts = 2
                let mutable validationError = this.ValidateResponse working
                let mutable repairAttempts = 0
                let mutable convo = messages
                while validationError.IsSome && repairAttempts < maxRepairAttempts do
                    repairAttempts <- repairAttempts + 1
                    let fixMsg = { Role = User; Content = this.BuildRepairMessage validationError.Value }
                    convo <- convo @ [ { Role = Assistant; Content = working }; fixMsg ]
                    let repairStarted = Diagnostics.Stopwatch.StartNew()
                    let repairSpan = startLlmSpan (repairAttempts + 1)
                    let! fixResult = config.Provider.CompleteAsync convo config.Options
                    repairStarted.Stop()
                    endLlmSpan repairSpan SpanStatus.Ok fixResult.Content.Length repairStarted.ElapsedMilliseconds
                    recordExchange (repairAttempts + 1) true convo fixResult.Content
                    working <- fixResult.Content
                    validationError <- this.ValidateResponse working
                match validationError with
                | Some error ->
                    raise (InvalidOperationException(sprintf "LLM response remained invalid after %d repair attempts: %s" repairAttempts error))
                | None -> ()
                // Guaranteed logging: the round's reasoning always reaches the event bus here.
                if not (String.IsNullOrWhiteSpace working) then report (ReasoningAdded working)
                this.EndRoundSpan roundSpan SpanStatus.Ok
                    (Map.ofList [ "reasoning.length", string working.Length; "repairs", string repairAttempts ])
                return working
            with ex ->
                this.EndRoundSpan roundSpan (SpanStatus.Error ex.Message) Map.empty
                return raise ex
        }

    member private this.RunCore(input: string) : Task<string> =
        task {
            // The running conversation the concrete `Orchestrate` sees each round: the user
            // input followed by any tool/agent results appended below. The subclass prepends
            // its own system prompt when it calls the LLM.
            let mutable conversation = [ { Role = User; Content = input } ]
            let mutable rounds = 0
            let mutable finalAnswer = ""
            let mutable finished = false
            let successfulToolCalls = Collections.Generic.HashSet<string * string>()

            while not finished && rounds < config.MaxRounds do
                // The base performs the LLM call (with repair + reasoning logging + tracing),
                // so logs/traces are captured no matter how a subclass builds the prompt or
                // parses the response.
                let! response = this.ReasonAsync conversation (rounds + 1)
                // The assistant's own message joins the running conversation — like before —
                // so the next round's prompt can include what the model already said.
                conversation <- conversation @ [ { Role = Assistant; Content = response } ]

                let produced = this.ParseActions response
                let parsedActions = produced
                if List.isEmpty parsedActions then
                    // No actions and no fallback: the response text IS the final answer.
                    finalAnswer <- response
                    finished <- true
                else
                    // A single planning step may request MULTIPLE tool calls / delegations.
                    // Execute them in order, feeding each result back into the conversation,
                    // and stop early if a delegation hands the turn off (async token answer).
                    for action in parsedActions do
                        if not finished then
                            match action with
                            | InvokeTool (toolName, toolInput) ->
                                if successfulToolCalls.Contains((toolName, toolInput)) then
                                    conversation <- conversation @ [ { Role = User; Content = sprintf "[Tool Result from %s]: This exact successful tool call was already completed. Do not repeat it; respond with the completed result." toolName } ]
                                else
                                  report (ToolInvoked (toolName, toolInput))
                                // O: record the tool invocation (name + parameters + context) as a span.
                                  let toolSpan = this.StartToolSpan toolName toolInput (rounds + 1)
                                  match config.Tools |> List.tryFind (fun t -> t.Name = toolName) with
                                  | Some tool ->
                                      let! toolResult = tool.InvokeAsync(toolContext, toolInput)
                                      report (ToolCompleted (toolName, toolResult))
                                      this.OnToolResult(toolName, toolInput, toolResult)
                                      let! verifyMsg =
                                          match tool.Verify with
                                          | Some verify ->
                                              task {
                                                  let! vr = verify toolInput toolResult
                                                  match vr with
                                                  | Ok () -> return None
                                                  | Error reason ->
                                                      return Some (sprintf "[Verification Failed for %s]: %s. Please retry or choose a different approach." toolName reason)
                                              }
                                          | None -> Task.FromResult None
                                      let resultContent = sprintf "[Tool Result from %s]: %s" toolName toolResult
                                      conversation <- conversation @ [ { Role = User; Content = resultContent } ]
                                      let resultAttrs = Map.ofList [ "result.length", string toolResult.Length ]
                                      match verifyMsg with
                                      | Some failMsg ->
                                          conversation <- conversation @ [ { Role = User; Content = failMsg } ]
                                          this.EndToolSpan toolSpan (SpanStatus.Error (sprintf "verification failed for %s" toolName)) resultAttrs
                                      | None ->
                                          successfulToolCalls.Add((toolName, toolInput)) |> ignore
                                          this.EndToolSpan toolSpan SpanStatus.Ok resultAttrs
                                  | None ->
                                      let err = sprintf "Tool '%s' not found. Available tools: %s" toolName (config.Tools |> List.map (fun t -> t.Name) |> String.concat ", ")
                                      conversation <- conversation @ [ { Role = User; Content = sprintf "[Error]: %s" err } ]
                                      this.EndToolSpan toolSpan (SpanStatus.Error err) Map.empty

                            | DelegateToAgent (agentId, agentInput) ->
                                let effectiveAgentInput =
                                    if String.IsNullOrWhiteSpace agentInput then input else agentInput
                                if String.Equals(agentId, config.Id, StringComparison.OrdinalIgnoreCase) then
                                    let err = sprintf "Agent '%s' cannot delegate to itself." agentId
                                    conversation <- conversation @ [ { Role = User; Content = sprintf "[Error]: %s" err } ]
                                else
                                    match config.SubAgents |> List.tryFind (fun a -> a.Id = agentId) with
                                    | Some agent ->
                                        report (SubAgentInvoked (agent.Name, effectiveAgentInput))
                                        match agent with
                                        | :? IRuntimeAgentContext as contextual -> contextual.SetEventContext eventBus eventScope
                                        | _ -> ()
                                        match agent with
                                        | :? IContextualAgent as contextual -> contextual.SetToolContext toolContext
                                        | _ -> ()
                                        match agent, this.TraceContext with
                                        | (:? OrchestratorBase as child), Some traceContext -> child.TraceContext <- Some traceContext
                                        | _ -> ()
                                        let! agentResult = agent.RunAsync effectiveAgentInput
                                        report (SubAgentCompleted (agent.Name, agentResult))
                                        finalAnswer <- agentResult
                                        finished <- true
                                    | None ->
                                        let err = sprintf "Agent '%s' not found. Available agent identifiers: %s" agentId (config.SubAgents |> List.map (fun a -> a.Id) |> String.concat ", ")
                                        conversation <- conversation @ [ { Role = User; Content = sprintf "[Error]: %s" err } ]

                            | Respond response ->
                                finalAnswer <- response
                                finished <- true
                            | Think _ -> ()

                this.OnRoundComplete(rounds + 1, if finished then finalAnswer else "")
                rounds <- rounds + 1

            if not finished then
                let forceMsg = { Role = User; Content = "[System]: Maximum rounds reached. Please provide your final answer now." }
                conversation <- conversation @ [ forceMsg ]
                let! result = config.Provider.CompleteAsync conversation config.Options
                finalAnswer <- result.Content

            report (AnswerProduced finalAnswer)
            return finalAnswer
        }

    interface IAgent with
        member _.Id = id
        member _.Name = config.Name
        member _.Description = config.Description
        member _.Priority = config.Priority
        member _.Capabilities = config.Capabilities
        member _.Responsibilities = config.Responsibilities
        member _.Signature = config.Signature
        member this.RunAsync(input: string) = this.RunCore(input)
        member this.HandleMessageAsync(msg: AgentMessage) =
            task {
                let! response = this.RunCore(msg.Content)
                return Some (AgentMessage.create id msg.From response)
            }

    interface IContextualAgent with
        member _.SetToolContext(context: ToolContext) = toolContext <- context

    interface IRuntimeAgentContext with
        member _.SetEventContext(bus: IEventBus) (scope: EventScope) =
            eventBus <- bus
            eventScope <- scope

