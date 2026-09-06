namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Protocols

/// Configuration for the Orchestrator
type OrchestratorConfig =
    { Id: string
      Name: string
      Description: string
      Priority: int
      Responsibilities: string list
      Contract: AgentContract
      Provider: LlmProvider
      Tools: Tool list
      SubAgents: Agent list
      Prompt: Prompt
      Options: CompletionOptions
      MaxRounds: int
      Bus: EventBus
      Scope: EventScope }

/// Functional constructor for orchestrators.
type OrchestratorFactory = { Create: OrchestratorConfig -> Agent }

/// Prompt and action protocol prepared atomically for one planning round.
type OrchestratorRound =
    { Messages: Conversation
      ResponseProtocol: Nao.Protocols.ResponseProtocol<AgentAction> option
      ParseActions: string -> AgentAction list
      ValidateResponse: string -> string option
      BuildRepairMessage: string -> string }

/// Customizable planning behavior consumed by the generic orchestration loop.
type OrchestratorDefinition =
    { GenerateReasoningPrompt: Conversation -> Task<Conversation>
      PrepareRound: (Conversation -> Task<OrchestratorRound>) option
      ResponseProtocol: Nao.Protocols.ResponseProtocol<AgentAction> option
      ParseActions: string -> AgentAction list
      ValidateResponse: string -> string option
      BuildRepairMessage: string -> string }

module OrchestratorDefinition =
    let create generateReasoningPrompt =
        { GenerateReasoningPrompt = generateReasoningPrompt
          PrepareRound = None
          ResponseProtocol = None
          ParseActions = fun _ -> []
          ValidateResponse = fun _ -> None
          BuildRepairMessage =
            fun error ->
                sprintf "[System]: Your previous response was invalid: %s. Please re-send a corrected response." error }

/// Runs a functional planning definition inside the bounded orchestration loop.
module Orchestrator =
    let createWithProtocol
        (toolProtocol: ToolProtocol)
        (config: OrchestratorConfig)
        (definition: OrchestratorDefinition)
        : Agent =
        let id = config.Id
        let eventBus = config.Bus
        let eventScope = config.Scope
        let traceContext: (Tracer * Span) option = None

        let report (signal: ProgressSignal) =
            EventBus.publishAsync (NaoEvent.TurnProgress(eventScope, signal)) eventBus
            |> ignore

        // Strip a single surrounding Markdown code fence (```lang ... ```), which models often
        // wrap structured JSON in despite being told not to.
        /// Start a child span for a tool invocation, tagging it with the tool name, its
        /// parameters, the invoking agent, and the current round. Returns None when no tracer
        /// is wired so callers stay allocation-free in the untraced path.
        let startToolSpan (toolName: string) (toolInput: string) (round: int) : (Tracer * Span) option =
            match traceContext with
            | Some(tracer, parent) ->
                let span = tracer.StartSpan parent "tool.invoke"

                let trimmedInput =
                    if String.IsNullOrEmpty toolInput then
                        ""
                    elif toolInput.Length > 1000 then
                        toolInput.Substring(0, 1000) + "…"
                    else
                        toolInput

                tracer.SetAttributes
                    span
                    (Map.ofList
                        [ "tool.name", toolName
                          "tool.input", trimmedInput
                          "agent.name", config.Name
                          "round", string round ])

                Some(tracer, span)
            | None -> None

        /// Close a tool-invocation span with the given status, recording a `tool.result` event
        /// with any extra attributes (e.g. result size). No-op when tracing is disabled.
        let endToolSpan (span: (Tracer * Span) option) (status: SpanStatus) (resultAttrs: Map<string, string>) =
            match span with
            | Some(tracer, s) ->
                if not (Map.isEmpty resultAttrs) then
                    tracer.AddEvent s "tool.result" resultAttrs

                tracer.EndSpan s status
            | None -> ()

        /// Start a child span for one planning round (the subclass's `Orchestrate` call), so the
        /// LLM planning step is always traced even when a custom orchestrator adds no spans of its
        /// own. Returns None when no tracer is wired.
        let startRoundSpan (round: int) : (Tracer * Span) option =
            match traceContext with
            | Some(tracer, parent) ->
                let span = tracer.StartSpan parent "agent.plan"
                tracer.SetAttributes span (Map.ofList [ "agent.name", config.Name; "round", string round ])
                Some(tracer, span)
            | None -> None

        /// Close a planning-round span, recording an `agent.plan.result` event with any extra
        /// attributes (reasoning length, action count). No-op when tracing is disabled.
        let endRoundSpan (span: (Tracer * Span) option) (status: SpanStatus) (attrs: Map<string, string>) =
            match span with
            | Some(tracer, s) ->
                if not (Map.isEmpty attrs) then
                    tracer.AddEvent s "agent.plan.result" attrs

                tracer.EndSpan s status
            | None -> ()

        /// Run one planning round: build the prompt (`GenerateReasoningPrompt`), call the LLM,
        /// repair an invalid response up to a bounded number of times (`ValidateResponse` /
        /// `BuildRepairMessage`), report the reasoning, and trace the round. Centralised here so
        /// every subclass gets identical logging and tracing for free — a custom orchestrator only
        /// supplies the prompt and the parser and can never accidentally drop the round's trace.
        let reasonAsync
            (correlation: CorrelationContext)
            (metricsOwner: string)
            (conversation: Conversation)
            (round: int)
            (successfulToolCalls: Collections.Generic.HashSet<string * string>)
            : Task<string * (string -> AgentAction list)> =
            task {
                let! prepared =
                    match definition.PrepareRound with
                    | Some prepare -> prepare conversation
                    | None ->
                        task {
                            let! messages = definition.GenerateReasoningPrompt conversation

                            return
                                { Messages = messages
                                  ResponseProtocol = definition.ResponseProtocol
                                  ParseActions = definition.ParseActions
                                  ValidateResponse = definition.ValidateResponse
                                  BuildRepairMessage = definition.BuildRepairMessage }
                        }

                let messages = prepared.Messages
                let roundSpan = startRoundSpan round

                let startLlmSpan (attempt: int) (messageCount: int) : (Tracer * Span) option =
                    match traceContext with
                    | Some(tracer, parent) ->
                        let span = tracer.StartSpan parent "llm.call"

                        tracer.SetAttributes
                            span
                            (Map.ofList
                                [ "agent.name", config.Name
                                  "round", string round
                                  "attempt", string attempt
                                  "messages.count", string messageCount ])

                        Some(tracer, span)
                    | None -> None

                let endLlmSpan
                    (span: (Tracer * Span) option)
                    (status: SpanStatus)
                    (outputLength: int)
                    (elapsedMs: int64)
                    =
                    match span with
                    | Some(tracer, child) ->
                        tracer.AddEvent
                            child
                            "llm.response"
                            (Map.ofList [ "output.length", string outputLength; "latency.ms", string elapsedMs ])

                        tracer.EndSpan child status
                    | None -> ()

                let recordMetrics (usage: TokenUsage option) (elapsedMs: int64) =
                    let inputTokens, outputTokens =
                        usage
                        |> Option.map (fun usage -> usage.InputTokens, usage.OutputTokens)
                        |> Option.defaultValue (0, 0)

                    RuntimeMetrics.get ()
                    |> Option.iter (fun metrics ->
                        metrics.Record(
                            MetricRecord.llmCall
                                correlation
                                metricsOwner
                                DateTimeOffset.UtcNow
                                inputTokens
                                outputTokens
                                elapsedMs
                        ))

                let invokeProvider (attempt: int) (streaming: bool) (prompt: Conversation) =
                    task {
                        let started = Diagnostics.Stopwatch.StartNew()
                        let span = startLlmSpan attempt prompt.Length

                        try
                            match RuntimeExecutionBudget.beginLlmCall () with
                            | Some limit -> raise (ExecutionLimitExceededException limit)
                            | None -> ()

                            let! result =
                                if streaming then
                                    LlmProvider.streamAsync config.Provider correlation prompt config.Options (fun _ ->
                                        ())
                                else
                                    config.Provider.CompleteAsync correlation prompt config.Options

                            match RuntimeExecutionBudget.recordLlmUsage result.Usage with
                            | Some limit -> raise (ExecutionLimitExceededException limit)
                            | None -> ()

                            started.Stop()
                            endLlmSpan span SpanStatus.Ok result.Content.Length started.ElapsedMilliseconds
                            recordMetrics result.Usage started.ElapsedMilliseconds
                            return result
                        with ex ->
                            started.Stop()
                            endLlmSpan span (SpanStatus.Error ex.Message) 0 started.ElapsedMilliseconds
                            recordMetrics None started.ElapsedMilliseconds
                            return raise ex
                    }

                let recordExchange (attempt: int) (isRepair: bool) (prompt: Conversation) (result: string) =
                    let messagesForStorage =
                        prompt |> List.map (fun message -> (sprintf "%A" message.Role, message.Content))

                    EventBus.publishAsync
                        (NaoEvent.LlmExchangeRecorded(
                            eventScope,
                            { Round = round
                              Attempt = attempt
                              IsRepair = isRepair
                              Messages = messagesForStorage
                              Response = result }
                        ))
                        eventBus
                    |> ignore

                try
                    let! result = invokeProvider 1 true messages
                    recordExchange 1 false messages result.Content
                    let mutable working = result.Content
                    // Validate-and-repair: ask the model to correct an invalid response (bounded)
                    // before it is parsed. A response protocol owns structured diagnostics and
                    // repair guidance; legacy overrides remain supported when no protocol exists.
                    let maxRepairAttempts = 2
                    let protocol = prepared.ResponseProtocol

                    let validate response =
                        match protocol with
                        | Some value ->
                            match value.Parse response with
                            | Ok _ -> None, None
                            | Error error -> Some error, Some(ResponseParseError.format error)
                        | None -> None, prepared.ValidateResponse response

                    let mutable protocolError, validationError = validate working
                    let mutable repairAttempts = 0
                    let mutable convo = messages

                    while validationError.IsSome && repairAttempts < maxRepairAttempts do
                        repairAttempts <- repairAttempts + 1

                        let repairMessage =
                            match protocol, protocolError with
                            | Some value, Some error -> value.BuildRepairMessage error
                            | _ -> prepared.BuildRepairMessage validationError.Value

                        let completedToolGuidance =
                            if successfulToolCalls.Count = 0 then
                                ""
                            else
                                let names = successfulToolCalls |> Seq.map fst |> Seq.distinct |> String.concat ", "

                                sprintf
                                    " Successful tool calls from earlier rounds: %s. Do not repeat equivalent successful calls. If they completed the request, reply only with respond <completed summary>. Otherwise emit only the missing tool actions."
                                    names

                        let fixMsg =
                            { Role = User
                              Content = repairMessage + completedToolGuidance }

                        convo <- convo @ [ { Role = Assistant; Content = working }; fixMsg ]
                        let! fixResult = invokeProvider (repairAttempts + 1) false convo
                        recordExchange (repairAttempts + 1) true convo fixResult.Content
                        working <- fixResult.Content
                        let nextProtocolError, nextValidationError = validate working
                        protocolError <- nextProtocolError
                        validationError <- nextValidationError

                    match validationError with
                    | Some error ->
                        raise (
                            InvalidOperationException(
                                sprintf
                                    "LLM response remained invalid after %d repair attempts: %s"
                                    repairAttempts
                                    error
                            )
                        )
                    | None -> ()
                    // Guaranteed logging: the round's reasoning always reaches the event bus here.
                    if not (String.IsNullOrWhiteSpace working) then
                        report (ReasoningAdded working)

                    endRoundSpan
                        roundSpan
                        SpanStatus.Ok
                        (Map.ofList [ "reasoning.length", string working.Length; "repairs", string repairAttempts ])

                    return working, prepared.ParseActions
                with ex ->
                    endRoundSpan roundSpan (SpanStatus.Error ex.Message) Map.empty
                    return raise ex
            }

        let runCore (agentContext: AgentContext) (input: string) : Task<string> =
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
                    let! response, parseActions =
                        reasonAsync
                            agentContext.Correlation
                            agentContext.SessionKey
                            conversation
                            (rounds + 1)
                            successfulToolCalls
                    // The assistant's own message joins the running conversation — like before —
                    // so the next round's prompt can include what the model already said.
                    conversation <- conversation @ [ { Role = Assistant; Content = response } ]

                    let produced = parseActions response
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
                                | InvokeTool(toolName, toolInput) ->
                                    let! tool = toolProtocol.GetTool toolName

                                    match tool with
                                    | Some _ when successfulToolCalls.Contains((toolName, toolInput)) ->
                                        conversation <-
                                            conversation
                                            @ [ { Role = User
                                                  Content =
                                                    sprintf
                                                        "[Tool Result from %s]: This equivalent successful tool call was already completed. Do not repeat it; respond with the completed result."
                                                        toolName } ]
                                    | Some _ ->
                                        report (ToolInvoked(toolName, toolInput))
                                        // O: record the prepared invocation (name + normalized parameters + context) as a span.
                                        let toolSpan = startToolSpan toolName toolInput (rounds + 1)
                                        let! toolExecution = toolProtocol.InvokeAsync agentContext toolName toolInput

                                        match RuntimeMetrics.get () with
                                        | Some metrics ->
                                            metrics.Record(
                                                MetricRecord.toolCall
                                                    agentContext.Correlation
                                                    agentContext.SessionKey
                                                    DateTimeOffset.UtcNow
                                                    toolName
                                                    toolExecution.DurationMs
                                                    toolExecution.Success
                                            )
                                        | None -> ()

                                        if toolExecution.Success then
                                            let toolResult = toolExecution.Output
                                            report (ToolCompleted(toolName, toolResult))
                                            let resultContent = sprintf "[Tool Result from %s]: %s" toolName toolResult
                                            conversation <- conversation @ [ { Role = User; Content = resultContent } ]
                                            successfulToolCalls.Add((toolName, toolInput)) |> ignore

                                            let durationMs =
                                                toolExecution.DurationMs.ToString(
                                                    Globalization.CultureInfo.InvariantCulture
                                                )

                                            match RuntimeExecutionJournal.get () with
                                            | Some journal ->
                                                if String.IsNullOrWhiteSpace agentContext.SessionKey then
                                                    invalidOp "Execution journaling requires a nonblank session key."

                                                if String.IsNullOrWhiteSpace agentContext.TurnId then
                                                    invalidOp "Execution journaling requires a nonblank turn ID."

                                                let record: ExecutionRecord =
                                                    { Id = Guid.NewGuid()
                                                      Correlation = agentContext.Correlation
                                                      Owner = agentContext.SessionKey
                                                      TurnId = agentContext.TurnId
                                                      ToolName = toolName
                                                      Input = toolInput
                                                      Output = toolResult
                                                      ExecutedAt = DateTimeOffset.UtcNow
                                                      Reverted = false
                                                      Metadata = Map.ofList [ "duration.ms", durationMs ] }

                                                do! journal.RecordAsync record
                                            | None -> ()

                                            endToolSpan
                                                toolSpan
                                                SpanStatus.Ok
                                                (Map.ofList [ "result.length", string toolResult.Length ])
                                        else
                                            let failure =
                                                toolExecution.Failure
                                                |> Option.defaultValue
                                                    { Kind = ToolFailureKind.Execution
                                                      Message =
                                                        toolExecution.Error
                                                        |> Option.defaultValue "Tool invocation failed."
                                                      Retryable = false }

                                            let guidance =
                                                if failure.Retryable then
                                                    "Correct the input or choose a different approach, then retry."
                                                else
                                                    "Do not repeat the same call; choose a different approach or explain the failure."

                                            let failMsg =
                                                sprintf
                                                    "[Tool %A Failed for %s]: %s. %s"
                                                    failure.Kind
                                                    toolName
                                                    failure.Message
                                                    guidance

                                            conversation <- conversation @ [ { Role = User; Content = failMsg } ]

                                            endToolSpan
                                                toolSpan
                                                (SpanStatus.Error(sprintf "%A failed for %s" failure.Kind toolName))
                                                Map.empty
                                    | None ->
                                        let err =
                                            sprintf
                                                "Tool '%s' not found. Available tools: %s"
                                                toolName
                                                (config.Tools |> List.map (fun t -> t.Name) |> String.concat ", ")

                                        conversation <-
                                            conversation
                                            @ [ { Role = User
                                                  Content = sprintf "[Error]: %s" err } ]

                                | DelegateToAgent(agentId, agentInput) ->
                                    let effectiveAgentInput =
                                        if String.IsNullOrWhiteSpace agentInput then
                                            input
                                        else
                                            agentInput

                                    if String.Equals(agentId, config.Id, StringComparison.OrdinalIgnoreCase) then
                                        let err = sprintf "Agent '%s' cannot delegate to itself." agentId

                                        conversation <-
                                            conversation
                                            @ [ { Role = User
                                                  Content = sprintf "[Error]: %s" err } ]
                                    else
                                        match config.SubAgents |> List.tryFind (fun a -> a.Metadata.Id = agentId) with
                                        | Some agent ->
                                            report (SubAgentInvoked(agent.Metadata.Name, effectiveAgentInput))

                                            let! agentResult =
                                                ExecutionRuntime.runAgent agentContext agent effectiveAgentInput

                                            match agentResult with
                                            | Ok response ->
                                                report (SubAgentCompleted(agent.Metadata.Name, response))
                                                finalAnswer <- response
                                                finished <- true
                                            | Error failure ->
                                                conversation <-
                                                    conversation
                                                    @ [ { Role = User
                                                          Content =
                                                            sprintf
                                                                "[Sub-agent %A Failed for %s]: %s"
                                                                failure.Category
                                                                agent.Metadata.Name
                                                                failure.Message } ]
                                        | None ->
                                            let err =
                                                sprintf
                                                    "Agent '%s' not found. Available agent identifiers: %s"
                                                    agentId
                                                    (config.SubAgents
                                                     |> List.map (fun a -> a.Metadata.Id)
                                                     |> String.concat ", ")

                                            conversation <-
                                                conversation
                                                @ [ { Role = User
                                                      Content = sprintf "[Error]: %s" err } ]

                                | Respond response ->
                                    finalAnswer <- response
                                    finished <- true
                                | RequestUserInput prompt ->
                                    finalAnswer <- prompt
                                    finished <- true
                                | Think _ -> ()

                    rounds <- rounds + 1

                if not finished then
                    let forceMsg =
                        { Role = User
                          Content = "[System]: Maximum rounds reached. Please provide your final answer now." }

                    conversation <- conversation @ [ forceMsg ]

                    let! response, parseActions =
                        reasonAsync
                            agentContext.Correlation
                            agentContext.SessionKey
                            conversation
                            (rounds + 1)
                            successfulToolCalls

                    finalAnswer <-
                        parseActions response
                        |> List.tryPick (function
                            | Respond answer
                            | RequestUserInput answer -> Some answer
                            | _ -> None)
                        |> Option.defaultValue response

                report (AnswerProduced finalAnswer)
                return finalAnswer
            }

        Agent.create id config.Name config.Description config.Priority config.Responsibilities config.Contract runCore

    /// Create an orchestrator over its configured local tools.
    let create (config: OrchestratorConfig) (definition: OrchestratorDefinition) : Agent =
        createWithProtocol (ToolProtocol.fromTools config.Tools) config definition
