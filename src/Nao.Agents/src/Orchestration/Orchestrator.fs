namespace Nao.Agents

open System
open System.Text.Json
open System.Threading.Tasks
open Nao.Core

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
type OrchestratorConfig =
    { /// The LLM provider used for reasoning
      Provider: ILlmProvider
      /// Available tools the orchestrator can invoke
      Tools: Tool list
      /// Available sub-agents the orchestrator can delegate to
      SubAgents: IAgent list
      /// The system prompt for the orchestrator
      Prompt: Prompt
      /// Completion options
      Options: CompletionOptions
      /// Maximum tool/agent invocation rounds before forcing a response
      MaxRounds: int
      /// Event sink for logging, progress, and conversation tracking
      EventSink: IAgentEventSink
      /// Memory management configuration
      Memory: OrchestratorMemoryConfig
      /// Custom instructions appended to the system prompt (replaces default action format instructions when set)
      Instructions: string option }

/// Factory interface for creating orchestrator instances via DI.
/// Register a custom implementation to control how orchestrators are built from workspace definitions.
type IOrchestratorFactory =
    /// Create an orchestrator (as IAgent) from the given configuration.
    abstract member Create: OrchestratorConfig -> IAgent

/// The fundamental orchestrator agent.
/// Accepts user input, uses an LLM to decide which tool or sub-agent to invoke,
/// executes the action, feeds results back, and produces a final response.
/// Subclass and override virtual members to customize behavior.
[<AbstractClass>]
type OrchestratorBase(config: OrchestratorConfig) =
    let id = { Name = "orchestrator"; Description = "Routes requests to tools and sub-agents" }
    let mutable state = AgentState.Empty

    let emit event = config.EventSink.Emit event

    let applyWindowAsync (conversation: Conversation) : Task<Conversation> =
        task {
            let! afterSummary =
                match config.Memory.Summarization with
                | Some summarizationConfig -> Summarizer.applyAsync summarizationConfig conversation
                | Option.None -> Task.FromResult conversation
            return
                match config.Memory.WindowStrategy with
                | Some strategy -> ConversationWindow.apply strategy afterSummary
                | Option.None -> afterSummary
        }

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
    let stripCodeFence (text: string) : string =
        let t = text.Trim()
        if t.StartsWith("```") then
            let firstNl = t.IndexOf('\n')
            if firstNl >= 0 then
                let body = t.Substring(firstNl + 1)
                let endFence = body.LastIndexOf("```")
                if endFence >= 0 then body.Substring(0, endFence).Trim() else body.Trim()
            else t
        else t

    // Extract each top-level brace-balanced JSON object from arbitrary text, ignoring
    // braces inside string literals. Tolerates surrounding prose and stray characters
    // (e.g. a model emitting an extra closing brace between objects), so the planner's
    // JSON object is recovered even when surrounded by stray text.
    let extractJsonObjects (text: string) : string list =
        let results = ResizeArray<string>()
        let mutable depth = 0
        let mutable start = -1
        let mutable inString = false
        let mutable escaped = false
        for i in 0 .. text.Length - 1 do
            let c = text.[i]
            if inString then
                if escaped then escaped <- false
                elif c = '\\' then escaped <- true
                elif c = '"' then inString <- false
            else
                match c with
                | '"' -> inString <- true
                | '{' ->
                    if depth = 0 then start <- i
                    depth <- depth + 1
                | '}' ->
                    if depth > 0 then
                        depth <- depth - 1
                        if depth = 0 && start >= 0 then
                            results.Add(text.Substring(start, i - start + 1))
                            start <- -1
                | _ -> ()
        List.ofSeq results

    // Parse a single planner step (one element of the "actions" array, or a legacy
    // single-action object) into an AgentAction. The planner schema uses "type"/"params";
    // the legacy schema used "action"/"input" — both are accepted for robustness.
    let parseActionElement (root: JsonElement) : AgentAction option =
        let getValue (key: string) =
            match root.TryGetProperty(key) with
            | true, elem when elem.ValueKind = JsonValueKind.String -> Some (elem.GetString())
            | _ -> None

        // The tool/agent input. Tools take a JSON-object input, so "params" is normally an
        // object — passed through as its raw JSON text. A plain string is also accepted (for
        // single-value inputs and the legacy schema).
        let getArgs (key: string) =
            match root.TryGetProperty(key) with
            | true, elem ->
                match elem.ValueKind with
                | JsonValueKind.String -> Some (elem.GetString())
                | JsonValueKind.Object | JsonValueKind.Array -> Some (elem.GetRawText())
                | JsonValueKind.Null | JsonValueKind.Undefined -> None
                | _ -> Some (elem.GetRawText())
            | _ -> None

        let name = getValue "name"
        let args = getArgs "params" |> Option.orElse (getArgs "input") |> Option.defaultValue ""
        let isKnownTool n = config.Tools |> List.exists (fun t -> t.Name = n)
        let isKnownAgent n = config.SubAgents |> List.exists (fun a -> a.Id.Name = n)

        match getValue "type" |> Option.orElse (getValue "action") with
        | Some kind ->
            match kind.ToLowerInvariant() with
            | "tool" | "tool-invoke" | "invoke-tool" -> name |> Option.map (fun n -> InvokeTool (n, args))
            | "delegate" | "agent-delegate" | "delegate-agent" -> name |> Option.map (fun n -> DelegateToAgent (n, args))
            | _ when isKnownTool kind -> Some (InvokeTool (kind, args))
            | _ when isKnownAgent kind -> Some (DelegateToAgent (kind, args))
            | _ ->
                match name with
                | Some n when isKnownTool n -> Some (InvokeTool (n, args))
                | Some n when isKnownAgent n -> Some (DelegateToAgent (n, args))
                | _ -> None
        | None -> None

    // Extract the JSON payload of the planner's fenced action block, identified by the
    // explicit info string ```application/json+nao. Some means "the model asked to act";
    // None means "this is a normal answer". Tolerates the JSON on the same line as the
    // info string and a missing closing fence.
    let extractActionBlock (text: string) : string option =
        let m =
            System.Text.RegularExpressions.Regex.Match(
                text,
                "```[ \\t]*application/json\\+nao[ \\t]*\\r?\\n?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        if not m.Success then None
        else
            let startIdx = m.Index + m.Length
            let closeIdx = text.IndexOf("```", startIdx, System.StringComparison.Ordinal)
            if closeIdx < 0 then Some (text.Substring(startIdx).Trim())
            else Some (text.Substring(startIdx, closeIdx - startIdx).Trim())

    // Parse a planner JSON payload (either { "actions": [...] } or a bare action object)
    // into actions, tolerating surrounding prose/stray braces. Returns [] when nothing
    // valid is found — used both to extract actions and to detect a malformed block.
    let parseActionsFromJson (json: string) : AgentAction list =
        extractJsonObjects json
        |> List.collect (fun obj ->
            try
                use doc = JsonDocument.Parse(obj)
                let root = doc.RootElement
                match root.TryGetProperty("actions") with
                | true, arr when arr.ValueKind = JsonValueKind.Array ->
                    [ for el in arr.EnumerateArray() -> parseActionElement el ] |> List.choose (fun a -> a)
                | _ -> parseActionElement root |> Option.toList
            with _ -> [])

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
                  "agent.name", id.Name
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

    /// Parse a planner response into the actions to execute. The planner emits a single JSON
    /// object: { "actions": [ {type,name,params}, ... ] }; every step is returned in order.
    /// Tolerates a leading label or prose (e.g. "PLAN:") before the JSON and a bare legacy
    /// action object. Returns an empty list when the content is a normal final answer.
    member _.DefaultTryParseActions(content: string) : AgentAction list =
        match extractActionBlock content with
        | Some inner ->
            // The model explicitly tagged an action block — parse only its payload.
            parseActionsFromJson inner
        | None ->
            // Backward-compatible fallback: accept a bare JSON action object/array even
            // without the fence (legacy planners and scripted tests).
            let trimmed = stripCodeFence content
            if not (trimmed.Contains("\"actions\"") || trimmed.TrimStart().StartsWith("{")) then []
            else parseActionsFromJson trimmed

    /// Default single-action parsing logic. Returns the FIRST recognised action, if any.
    /// Can be called by subclasses that cannot use base in task CEs.
    member this.DefaultTryParseAction(content: string) : AgentAction option =
        this.DefaultTryParseActions(content) |> List.tryHead

    /// True when the response contains a tagged `application/json+nao` action block whose
    /// payload is NOT well-formed (or yields no valid action). Lets the run loop ask the
    /// model to repair the block instead of silently dropping the action or echoing the
    /// broken JSON to the user. A response with no action block is never "malformed".
    member _.HasMalformedActionBlock(content: string) : bool =
        match extractActionBlock content with
        | Some inner -> List.isEmpty (parseActionsFromJson inner)
        | None -> false

    /// Override to customize the system prompt generation.
    abstract member BuildSystemPrompt: unit -> string
    default _.BuildSystemPrompt() =
        let toolDescriptions =
            config.Tools
            |> List.map (fun t -> sprintf "  - %s: %s" t.Name t.Description)
            |> String.concat "\n"

        let agentDescriptions =
            config.SubAgents
            |> List.map (fun a -> sprintf "  - %s: %s" a.Id.Name a.Id.Description)
            |> String.concat "\n"

        let basePrompt = Prompt.render config.Prompt

        let capabilities =
            [ if config.Tools.Length > 0 then
                yield sprintf "# Available Tools\n%s" toolDescriptions
              if config.SubAgents.Length > 0 then
                yield sprintf "# Available Agents\n%s" agentDescriptions ]
            |> String.concat "\n\n"

        let instructions =
            match config.Instructions with
            | Some custom -> custom
            | None -> """
# Action Format
To use a tool or sub-agent, reply with EXACTLY ONE fenced code block whose info string is `application/json+nao`. The block holds a single JSON object listing every step, in order. Put the action JSON ONLY inside this block:

```application/json+nao
{"actions":[{"type":"tool","name":"<tool_name>","params":{ ...tool arguments... }},{"type":"delegate","name":"<agent_name>","params":"<input_string>"}]}
```

Rules:
- The action JSON goes ONLY inside the ```application/json+nao fenced block, and you open at most ONE such block.
- "actions" is a single array; include EVERY step in it, in order.
- Each element has "type" ("tool" or "delegate"), "name" (the exact tool/agent name) and "params".
- For a "tool", "params" is a JSON object with the tool's named arguments (see each tool's description for its fields), e.g. {"path":"notes.txt","content":"hello"}. For a "delegate", "params" is the input string for the sub-agent.
- The block's JSON must be strictly valid: every brace and bracket balanced, no trailing text. If told it was malformed, re-send a corrected block.
- The steps execute in order and their results are fed back to you for the next round.
- Prefer delegating to a specialist sub-agent over invoking a tool when both could accomplish the task.

Example — write a file then convert it:
```application/json+nao
{"actions":[{"type":"tool","name":"write_file","params":{"path":"README.md","content":"# Title"}},{"type":"tool","name":"convert_document","params":{"source":"README.md","target":"pdf"}}]}
```

Example — for "Convert README.md to PDF and HTML" (delegate ONCE; the specialist produces both):
```application/json+nao
{"actions":[{"type":"delegate","name":"converter","params":"Convert README.md to PDF and HTML"}]}
```

When you can answer the user directly, reply in plain text with NO action block. Only emit the block to invoke a tool or delegate. If the user requests a specific output format (JSON, XML, CSV, Markdown, YAML), encode your final answer in that format as plain text (still no action block)."""

        sprintf "%s\n\n%s\n%s" basePrompt capabilities instructions

    /// Override to customize how LLM output is parsed into an AgentAction.
    /// Return Some to invoke a tool/agent, None to treat the response as final answer.
    abstract member TryParseActionAsync: string -> Task<AgentAction option>
    default this.TryParseActionAsync(content: string) =
        this.DefaultTryParseAction(content) |> Task.FromResult

    /// Override to customize how LLM output is parsed into MULTIPLE actions. Return the
    /// actions to execute in order; an empty list treats the response as a final answer.
    abstract member TryParseActionsAsync: string -> Task<AgentAction list>
    default this.TryParseActionsAsync(content: string) =
        this.DefaultTryParseActions(content) |> Task.FromResult

    /// Override to add custom logic after a tool executes.
    abstract member OnToolResult: toolName: string * input: string * result: string -> unit
    default _.OnToolResult(_, _, _) = ()

    /// Override to intercept delegation to a sub-agent before the default in-process call.
    /// Return Some finalAnswer to finish the turn immediately with that answer — e.g. when
    /// the delegation was handed off to a background task and the orchestrator should reply
    /// with a token instead of blocking. Return None to fall back to in-process delegation.
    abstract member TryHandleDelegationAsync: agentName: string * input: string -> Task<string option>
    default _.TryHandleDelegationAsync(_, _) = Task.FromResult None

    /// Override to add custom logic after an agent round completes.
    abstract member OnRoundComplete: round: int * content: string -> unit
    default _.OnRoundComplete(_, _) = ()

    member private this.RunCore(input: string) : Task<string> =
        task {
            let! memoryContext = getMemoryContext ()
            let systemContent = this.BuildSystemPrompt() + memoryContext
            let systemMsg = { Role = System; Content = systemContent }
            let userMsg = { Role = User; Content = input }
            emit (AgentEvent.MessageAdded (User, input))

            let! windowedHistory = applyWindowAsync state.Conversation
            let mutable conversation = windowedHistory @ [ systemMsg; userMsg ]
            let mutable rounds = 0
            let mutable finalAnswer = ""
            let mutable finished = false
            let mutable repairAttempts = 0
            let maxRepairAttempts = 2

            while not finished && rounds < config.MaxRounds do
                emit (AgentEvent.Thinking (rounds + 1))
                let! result = config.Provider.CompleteAsync conversation config.Options
                let mutable working = result.Content
                conversation <- conversation @ [ { Role = Assistant; Content = working } ]
                emit (AgentEvent.MessageAdded (Assistant, working))

                // Validate-and-repair: if the model emitted a tagged action block that is
                // not well-formed JSON, ask it to re-send a corrected block (bounded) before
                // we decide. This keeps a broken action from being silently dropped or shown
                // to the user as the final answer, without consuming the tool/round budget.
                while this.HasMalformedActionBlock(working) && repairAttempts < maxRepairAttempts do
                    repairAttempts <- repairAttempts + 1
                    emit (AgentEvent.RoundError (sprintf "Malformed action block; requesting correction (%d/%d)." repairAttempts maxRepairAttempts))
                    let fixMsg =
                        { Role = User
                          Content = "[System]: Your previous message contained an ```application/json+nao``` action block, but its JSON was not well-formed. Re-send ONLY that block as a single strictly-valid JSON object — every brace and bracket balanced, no trailing text — e.g. ```application/json+nao\n{\"actions\":[{\"type\":\"tool\",\"name\":\"<tool>\",\"params\":\"<input>\"}]}\n```. If you no longer need a tool, reply in plain text with no block." }
                    conversation <- conversation @ [ fixMsg ]
                    let! fixResult = config.Provider.CompleteAsync conversation config.Options
                    working <- fixResult.Content
                    conversation <- conversation @ [ { Role = Assistant; Content = working } ]
                    emit (AgentEvent.MessageAdded (Assistant, working))

                let! parsedActions = this.TryParseActionsAsync(working)
                if List.isEmpty parsedActions then
                    finalAnswer <- working
                    finished <- true
                else
                    // A single LLM response may request MULTIPLE tool calls / delegations.
                    // Execute them in order, feeding each result back into the conversation,
                    // and stop early if a delegation hands the turn off (async token answer).
                    for action in parsedActions do
                        if not finished then
                            match action with
                            | InvokeTool (toolName, toolInput) ->
                                emit (AgentEvent.InvokingTool (toolName, toolInput))
                                // O: record the tool invocation (name + parameters + context) as a span.
                                let toolSpan = this.StartToolSpan toolName toolInput (rounds + 1)
                                match config.Tools |> List.tryFind (fun t -> t.Name = toolName) with
                                | Some tool ->
                                    let! toolResult = tool.InvokeAsync(ToolContext.current (), toolInput)
                                    emit (AgentEvent.ToolResult (toolName, toolResult))
                                    this.OnToolResult(toolName, toolInput, toolResult)
                                    let! verifyMsg =
                                        match tool.Verify with
                                        | Some verify ->
                                            task {
                                                let! vr = verify toolInput toolResult
                                                match vr with
                                                | Ok () -> return None
                                                | Error reason ->
                                                    emit (AgentEvent.ToolVerifyFailed (toolName, reason))
                                                    return Some (sprintf "[Verification Failed for %s]: %s. Please retry or choose a different approach." toolName reason)
                                            }
                                        | None -> Task.FromResult None
                                    let resultContent = sprintf "[Tool Result from %s]: %s" toolName toolResult
                                    let resultMsg = { Role = User; Content = resultContent }
                                    conversation <- conversation @ [ resultMsg ]
                                    let resultAttrs = Map.ofList [ "result.length", string toolResult.Length ]
                                    match verifyMsg with
                                    | Some failMsg ->
                                        let failMsgEntry = { Role = User; Content = failMsg }
                                        conversation <- conversation @ [ failMsgEntry ]
                                        this.EndToolSpan toolSpan (SpanStatus.Error (sprintf "verification failed for %s" toolName)) resultAttrs
                                    | None ->
                                        this.EndToolSpan toolSpan SpanStatus.Ok resultAttrs
                                | None ->
                                    let err = sprintf "Tool '%s' not found. Available tools: %s" toolName (config.Tools |> List.map (fun t -> t.Name) |> String.concat ", ")
                                    emit (AgentEvent.RoundError err)
                                    let errMsg = { Role = User; Content = sprintf "[Error]: %s" err }
                                    conversation <- conversation @ [ errMsg ]
                                    this.EndToolSpan toolSpan (SpanStatus.Error err) Map.empty

                            | DelegateToAgent (agentName, agentInput) ->
                                emit (AgentEvent.DelegatingToAgent (agentName, agentInput))
                                let! handled = this.TryHandleDelegationAsync(agentName, agentInput)
                                match handled with
                                | Some tokenAnswer ->
                                    // The delegation was handed off (e.g. to a background task); reply with
                                    // the token immediately instead of running the sub-agent inline.
                                    emit (AgentEvent.AgentResult (agentName, tokenAnswer))
                                    finalAnswer <- tokenAnswer
                                    finished <- true
                                | None ->
                                    match config.SubAgents |> List.tryFind (fun a -> a.Id.Name = agentName) with
                                    | Some agent ->
                                        let! agentResult = agent.RunAsync agentInput
                                        emit (AgentEvent.AgentResult (agentName, agentResult))
                                        let resultMsg = { Role = User; Content = sprintf "[Agent Result from %s]: %s" agentName agentResult }
                                        conversation <- conversation @ [ resultMsg ]
                                    | None ->
                                        let err = sprintf "Agent '%s' not found. Available agents: %s" agentName (config.SubAgents |> List.map (fun a -> a.Id.Name) |> String.concat ", ")
                                        emit (AgentEvent.RoundError err)
                                        let errMsg = { Role = User; Content = sprintf "[Error]: %s" err }
                                        conversation <- conversation @ [ errMsg ]

                            | Think _ | Respond _ -> ()

                this.OnRoundComplete(rounds + 1, if finished then finalAnswer else "")
                rounds <- rounds + 1

            if not finished then
                emit (AgentEvent.MaxRoundsReached config.MaxRounds)
                let forceMsg = { Role = User; Content = "[System]: Maximum rounds reached. Please provide your final answer now." }
                conversation <- conversation @ [ forceMsg ]
                let! result = config.Provider.CompleteAsync conversation config.Options
                finalAnswer <- result.Content
                conversation <- conversation @ [ { Role = Assistant; Content = result.Content } ]

            emit (AgentEvent.Completed finalAnswer)
            let historyMessages =
                conversation
                |> List.filter (fun m -> m.Role <> System)
            state <- { state with Conversation = historyMessages }
            return finalAnswer
        }

    interface IAgent with
        member _.Id = id
        member _.State = state
        member this.RunAsync(input: string) = this.RunCore(input)
        member this.HandleMessageAsync(msg: AgentMessage) =
            task {
                let! response = this.RunCore(msg.Content)
                return Some (AgentMessage.create id msg.From response)
            }


/// Default orchestrator implementation using the base class with no overrides.
type Orchestrator(config: OrchestratorConfig) =
    inherit OrchestratorBase(config)

module Orchestrator =

    /// Create an orchestrator with a default prompt
    let create (provider: ILlmProvider) (tools: Tool list) (subAgents: IAgent list) =
        let prompt =
            { Prompt.Empty with
                Role = "You are an intelligent orchestrator agent. You accept user requests and decide the best way to fulfill them using available tools and sub-agents."
                Objective = "Analyze the user's request, determine which tool or agent is best suited, invoke it, and provide a clear final answer based on the results."
                Constraints =
                    [ "Use tools when the user needs factual data, calculations, or external lookups."
                      "Delegate to sub-agents when the task requires specialized expertise."
                      "If you can answer directly without tools, do so."
                      "If the user requests a specific output format (JSON, XML, CSV, etc.), return the final answer in that format."
                      "Do not wrap the final answer in action JSON unless invoking a tool or delegating." ] }

        let config =
            { Provider = provider
              Tools = tools
              SubAgents = subAgents
              Prompt = prompt
              Options = { CompletionOptions.Default with Temperature = 0.1 }
              MaxRounds = 5
              EventSink = AgentEventSink.none
              Memory = OrchestratorMemoryConfig.None
              Instructions = None }

        Orchestrator(config) :> IAgent

    /// Create an orchestrator with a custom configuration
    let createWithConfig (config: OrchestratorConfig) =
        Orchestrator(config) :> IAgent


/// Default factory that creates standard Orchestrator instances.
/// Replace via DI to use a custom subclass of OrchestratorBase.
type DefaultOrchestratorFactory() =
    interface IOrchestratorFactory with
        member _.Create(config) = Orchestrator(config) :> IAgent
