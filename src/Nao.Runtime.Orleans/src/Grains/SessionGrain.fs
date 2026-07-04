namespace Nao.Runtime.Orleans.Grains

open System
open System.Text.Json
open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Nao.Agents
open Nao.Agents
open Nao.Loader
open Nao.Agents
open Nao.Agents
open Nao.Runtime.Orleans

// Allow the Orleans C# codegen project to access internal F# DU backing fields.
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Nao.Runtime.Orleans.Codegen")>]
do ()

/// Metadata about a session
[<GenerateSerializer>]
type SessionInfo() =
    [<Id(0u)>] member val AgentName: string = "" with get, set
    [<Id(1u)>] member val SessionId: string = "" with get, set
    [<Id(2u)>] member val UserId: string = "" with get, set
    [<Id(3u)>] member val GroupId: string = "" with get, set
    [<Id(4u)>] member val WorkspaceKey: string = "default" with get, set
    [<Id(5u)>] member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(6u)>] member val LastActiveAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(7u)>] member val IsActive: bool = true with get, set
    [<Id(8u)>] member val ToolNames: ResizeArray<string> = ResizeArray() with get, set
    [<Id(9u)>] member val ActiveConversation: string = "default" with get, set
    /// Optional pinned agent version ("" = unversioned / latest).
    [<Id(10u)>] member val AgentVersion: string = "" with get, set
    /// Id of the most recently processed turn (used to attach feedback).
    [<Id(11u)>] member val LastTurnId: string = "" with get, set
    /// Runtime policy for launching tools ("" = host default; "docker"/"docker:&lt;image&gt;"
    /// to containerize; a runtime name like "deno" to force it for all tools).
    [<Id(12u)>] member val RuntimeMode: string = "" with get, set
    /// Session kind: "primary" (a user session) or "task" (a sub-session driven by a task).
    [<Id(13u)>] member val Kind: string = "primary" with get, set
    /// Owning session key for a task sub-session ("" for a primary session).
    [<Id(14u)>] member val ParentKey: string = "" with get, set
    /// Task that spawned this sub-session ("" for a primary session).
    [<Id(15u)>] member val OriginTaskId: string = "" with get, set

/// A named conversation context within a session
[<GenerateSerializer>]
type ConversationContext() =
    [<Id(0u)>] member val Name: string = "default" with get, set
    [<Id(1u)>] member val Messages: ResizeArray<MessageRecord> = ResizeArray() with get, set
    [<Id(2u)>] member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set
    [<Id(3u)>] member val AgentName: string = "" with get, set

/// A resource-access grant the user approved for the lifetime of this session. Stored in the
/// session grain's own state (Orleans-persisted) so each session tracks its own permissions
/// and never re-prompts for something it already approved.
[<GenerateSerializer>]
type PermissionGrantRecord() =
    /// Resource class: "file", "web", or "tool".
    [<Id(0u)>] member val Kind: string = "" with get, set
    /// Match pattern: a file path, a web host, or a tool name.
    [<Id(1u)>] member val Pattern: string = "" with get, set

/// Persistent state for a session grain
[<GenerateSerializer>]
type SessionGrainState() =
    [<Id(0u)>] member val Info: SessionInfo = SessionInfo() with get, set
    [<Id(1u)>] member val Conversations: ResizeArray<ConversationContext> = ResizeArray() with get, set
    [<Id(2u)>] member val Memories: ResizeArray<MemoryRecord> = ResizeArray() with get, set
    /// Async tasks launched by this session (snapshots pushed from each task grain).
    [<Id(3u)>] member val Tasks: ResizeArray<TaskRef> = ResizeArray() with get, set
    /// Resource-access grants the user approved for this session ("remember for session").
    [<Id(4u)>] member val GrantedPermissions: ResizeArray<PermissionGrantRecord> = ResizeArray() with get, set

/// Options for starting or reconfiguring a session
[<GenerateSerializer>]
type SessionStartOptions() =
    [<Id(0u)>] member val AgentName: string = "" with get, set
    [<Id(1u)>] member val ToolNames: ResizeArray<string> = ResizeArray() with get, set
    [<Id(2u)>] member val WorkspaceKey: string = "default" with get, set
    [<Id(3u)>] member val GroupId: string = "" with get, set
    /// Optional pinned agent version ("" = unversioned / latest).
    [<Id(4u)>] member val AgentVersion: string = "" with get, set
    /// Runtime policy for launching tools ("" = host default; "docker"/"docker:&lt;image&gt;"
    /// to containerize; a runtime name like "deno" to force it for all tools).
    [<Id(5u)>] member val RuntimeMode: string = "" with get, set
    /// Session kind: "primary" (default) or "task" for a task sub-session.
    [<Id(6u)>] member val Kind: string = "primary" with get, set
    /// Owning session key when starting a task sub-session.
    [<Id(7u)>] member val ParentKey: string = "" with get, set

/// Orleans grain interface for a user session.
/// Grain key format: "userId/sessionId"
/// The grain resolves agents/tools from the workspace registry.
type ISessionGrain =
    inherit IGrainWithStringKey

    /// Initialize the session with a specific agent, workspace, and optional tool overrides.
    abstract member StartAsync: options: SessionStartOptions -> Task<bool>

    /// Process user input — grain resolves workspace, builds agent, runs ETCLOVG harness.
    abstract member ProcessAsync: input: string -> Task<string>

    /// Process user input where the LLM prompt and the persisted/display text differ:
    /// `llmInput` (with embedded attachment content) is what the agent sees, while only
    /// `displayText` plus `attachmentNames` are stored in the transcript.
    abstract member ProcessWithContextAsync: llmInput: string * displayText: string * attachmentNames: string[] -> Task<string>

    /// Id of the most recently processed turn (empty if none). Use to attach feedback.
    abstract member GetLastTurnIdAsync: unit -> Task<string>

    /// Snapshot of the steps of the turn currently being processed, for live progress
    /// UIs. Returns an empty array when no turn is running. Marked reentrant
    /// (`AlwaysInterleave`) so it can be polled while a turn is still in flight.
    [<Orleans.Concurrency.AlwaysInterleave>]
    abstract member GetLiveStepsAsync: unit -> Task<TurnStepRecord[]>

    /// Submit feedback for the most recently processed turn. `sentiment` is
    /// "positive" / "negative" / "neutral". Returns the rationales of any tool
    /// adjustments that were proposed and stored.
    abstract member SubmitFeedbackAsync: sentiment: string * comment: string -> Task<string array>

    /// Switch to a different workspace (re-validates agent exists in new workspace)
    abstract member SwitchWorkspaceAsync: workspaceKey: string -> Task<bool>

    /// Switch to a different agent within the current workspace
    abstract member SwitchAgentAsync: agentName: string -> Task<bool>

    /// Create or switch to a named conversation context
    abstract member SwitchConversationAsync: conversationName: string -> Task

    /// List all conversation contexts in this session
    abstract member ListConversationsAsync: unit -> Task<string array>

    /// Get the session metadata
    abstract member GetInfoAsync: unit -> Task<SessionInfo>

    /// Get the current conversation history
    abstract member GetHistoryAsync: unit -> Task<MessageRecord array>

    /// Clear the current conversation context
    abstract member ClearHistoryAsync: unit -> Task

    /// Save a key-value memory entry within this session
    abstract member SaveMemoryAsync: key: string * value: string -> Task

    /// Recall memories by key prefix
    abstract member RecallMemoryAsync: key: string -> Task<MemoryEntry array>

    /// Clear all memories
    abstract member ClearMemoriesAsync: unit -> Task

    /// Pause the session (marks inactive, keeps state)
    abstract member PauseAsync: unit -> Task

    /// Resume a paused session
    abstract member ResumeAsync: unit -> Task

    /// Permanently destroy the session and all its data
    abstract member DestroyAsync: unit -> Task

    /// Upsert an async-task snapshot into this session's tracked task list. Called by the
    /// owning task grain on each status transition (push model).
    abstract member UpdateTaskStatusAsync: task: TaskRef -> Task

    /// List all async tasks tracked by this session (newest first). Reentrant so a live UI
    /// can poll it while a turn is still running.
    [<Orleans.Concurrency.AlwaysInterleave>]
    abstract member ListTasksAsync: unit -> Task<TaskRef array>

/// Self-contained session grain. Resolves workspaces from IWorkspaceRegistry,
/// manages multiple conversation contexts and memory through Orleans persistence.
///
/// Dependencies (injected via Orleans DI at silo startup):
/// - IWorkspaceRegistry: multi-tenant workspace registry (multiple workspaces per silo)
/// - ILlmProvider: the LLM backend used to power agents
///
/// On each ProcessAsync call:
/// 1. Resolves the workspace from registry by stored key
/// 2. Finds the agent definition + tools from that workspace
/// 3. Builds a fresh agent instance (no prior state)
/// 4. Runs the agent through the full ETCLOVG harness (governance, verification, etc.)
/// 5. Persists the updated conversation in the active context
type SessionGrain
    (
        [<PersistentState("sessionState", "sessionStore")>] persistentState: IPersistentState<SessionGrainState>,
        registry: IWorkspaceRegistry,
        provider: ILlmProvider,
        orchestratorFactory: IOrchestratorFactory,
        conversationStore: IConversationStore,
        harnessServicesFactory: Func<string, string, IHarnessServices>,
        feedbackFactory: Func<string, FeedbackService>,
        eventBus: IEventBus,
        grainFactory: IGrainFactory
    ) =
    inherit Grain()

    /// The recorder for the turn currently being processed (None when idle). Held so the
    /// reentrant `GetLiveStepsAsync` can surface in-progress steps for live UIs. Reads go
    /// through `recorder.Steps`, which is internally lock-protected.
    let mutable currentRecorder : TurnRecorder option = None

    /// Per-session feedback store, resolved once from the grain key in OnActivateAsync so
    /// this session's annotations are written under sessions/<key>/. Observability services
    /// are built per turn (so each signal carries its turn id) in ProcessWithContextAsync.
    let mutable feedback : FeedbackService = Unchecked.defaultof<FeedbackService>

    // ─── Workspace resolution ───

    let getWorkspace () : WorkspaceDefinitions option =
        let key = WorkspaceId.create persistentState.State.Info.WorkspaceKey
        registry.TryGet key

    /// Build the identity envelope carried by every emitted event. `actionId` is the turn
    /// the event is about. Producers never decide where data lands — a subscribed storage
    /// strategy routes it (per session, per category, ...) from this scope.
    let makeScope (actionId: string) : EventScope =
        let info = persistentState.State.Info
        let sessionKey = sprintf "%s/%s" info.UserId info.SessionId
        EventScope.Create(
            info.UserId, info.SessionId, info.ActiveConversation, info.WorkspaceKey,
            actionId, sessionKey,
            ?parentKey = (if String.IsNullOrEmpty info.ParentKey then None else Some info.ParentKey))

    let buildHarnessConfig (workspace: WorkspaceDefinitions) (tools: Tool list) (scope: EventScope) (services: IHarnessServices) : EtclovgConfig =
        let constitution = DefinitionBuilder.buildMergedConstitution workspace.ConstitutionDefs
        let toolProtocol =
            if tools.Length > 0 then
                Some (ToolProtocol.fromTools tools)
            else
                None
        let baseConfig =
            { EtclovgConfig.Default with
                Constitution = constitution
                ToolProtocol = toolProtocol
                Bus = eventBus
                Scope = scope }
        baseConfig.WithServices(services)

    // ─── Key parsing ───

    let parseKey (key: string) =
        match key.IndexOf('/') with
        | -1 -> (key, "default")
        | idx -> (key.Substring(0, idx), key.Substring(idx + 1))

    // ─── Conversation context management ───

    let getOrCreateConversation (name: string) : ConversationContext =
        let existing =
            persistentState.State.Conversations
            |> Seq.tryFind (fun c -> c.Name = name)
        match existing with
        | Some ctx -> ctx
        | None ->
            let ctx = ConversationContext()
            ctx.Name <- name
            ctx.CreatedAt <- DateTimeOffset.UtcNow
            ctx.AgentName <- persistentState.State.Info.AgentName
            persistentState.State.Conversations.Add(ctx)
            ctx

    let activeConversation () : ConversationContext =
        getOrCreateConversation persistentState.State.Info.ActiveConversation

    /// Map a recorded process step onto its serializable storage form.
    let toStepRecord (s: TurnStep) : TurnStepRecord =
        TurnStepRecord(Kind = s.Kind, Title = s.Title, Input = s.Input, Output = s.Output)

    /// Append a completed turn — the user prompt plus a single assistant message that
    /// carries the whole process (ordered tool/sub-agent steps) and the final answer —
    /// to both in-memory grain state and the external append-only store.
    ///
    /// The orchestrator's internal LLM conversation (where tool results are themselves
    /// `User` messages and intermediate action-JSON is an `Assistant` message) is an
    /// implementation detail and is intentionally NOT persisted as the transcript.
    let appendTurnAsync (userInput: string) (attachmentNames: string[]) (response: string) (turnId: string) (steps: TurnStep list) : Task =
        task {
            let ctx = activeConversation ()
            let stepRecords = steps |> List.map toStepRecord

            let userRecord = MessageRecord(Role = "User", Content = userInput, TurnId = turnId,
                                           Attachments = ResizeArray(attachmentNames))
            let assistantRecord =
                MessageRecord(Role = "Assistant", Content = response, TurnId = turnId,
                              Steps = ResizeArray(stepRecords))
            ctx.Messages.Add userRecord
            ctx.Messages.Add assistantRecord

            let sessionId = persistentState.State.Info.SessionId
            let userId = persistentState.State.Info.UserId
            let convName = persistentState.State.Info.ActiveConversation
            if not (String.IsNullOrEmpty sessionId) then
                let grainKey = sprintf "%s/%s" userId sessionId
                let now = DateTimeOffset.UtcNow
                let persisted =
                    [| { PersistedMessage.Role = "User"; Content = userInput
                         Timestamp = now; TurnId = turnId; Steps = [||]; Attachments = attachmentNames }
                       { PersistedMessage.Role = "Assistant"; Content = response
                         Timestamp = now; TurnId = turnId
                         Steps = stepRecords |> List.toArray; Attachments = [||] } |]
                do! conversationStore.AppendAsync grainKey convName persisted

                // Persist each sub-agent delegation as a nested child conversation, so the
                // delegated work is browsable beneath its parent rather than only inlined as a
                // step. One child conversation per delegation (uniquely keyed by turn + index).
                let agentSteps = steps |> List.filter (fun s -> s.Kind = "agent")
                let mutable idx = 0
                for s in agentSteps do
                    idx <- idx + 1
                    let title = if String.IsNullOrWhiteSpace s.Title then "agent" else s.Title
                    let childName = sprintf "%s/%s-%s-%d" convName title turnId idx
                    let childMsgs =
                        [| { PersistedMessage.Role = "User"; Content = s.Input
                             Timestamp = now; TurnId = turnId; Steps = [||]; Attachments = [||] }
                           { PersistedMessage.Role = "Assistant"; Content = s.Output
                             Timestamp = now; TurnId = turnId; Steps = [||]; Attachments = [||] } |]
                    do! conversationStore.AppendAsync grainKey childName childMsgs
        }

    // ─── Async task tracking ───

    /// Insert or replace a task snapshot in the session's tracked task list (by id).
    let upsertTaskRef (ref: TaskRef) =
        match persistentState.State.Tasks |> Seq.tryFindIndex (fun t -> t.TaskId = ref.TaskId) with
        | Some i -> persistentState.State.Tasks.[i] <- ref
        | None -> persistentState.State.Tasks.Insert(0, ref)

    /// Create + start a task grain (key "userId/sessionId/taskId"), track it locally, and
    /// return the registered snapshot. The task runs in the background on its own grain.
    let spawnTaskAsync (kind: string) (title: string) (paramz: (string * string) list) (turnId: string) : Task<TaskRef> =
        task {
            let info = persistentState.State.Info
            let parentKey = sprintf "%s/%s" info.UserId info.SessionId
            let taskId = Guid.NewGuid().ToString("N").[..11]
            let subKey = sprintf "%s/%s" parentKey taskId
            let paramsJson = JsonSerializer.Serialize(dict paramz)
            let now = DateTimeOffset.UtcNow
            let ref =
                TaskRef(TaskId = taskId, Kind = kind, Title = title, Status = "pending",
                        SubSessionKey = subKey, TurnId = turnId, CreatedAt = now, UpdatedAt = now)
            upsertTaskRef ref
            let grain = grainFactory.GetGrain<ISessionTaskGrain>(subKey)
            let spec =
                TaskStartSpec(TaskId = taskId, ParentKey = parentKey, Kind = kind,
                              Title = title, ParamsJson = paramsJson, TurnId = turnId)
            let! started = grain.StartAsync(spec)
            upsertTaskRef started
            return started
        }

    // ─── Tool resolution ───

    let resolveTool (workspace: WorkspaceDefinitions) (policy: RuntimePolicy) (name: string) : Tool option =
        // Tool references may be version-qualified ("name@version").
        let (n, ver) = VersionRef.parse name
        workspace.Tools
        |> List.tryFind (fun t -> t.Name = n && VersionRef.matches ver t.Version)
        |> Option.orElseWith (fun () ->
            workspace.ToolDefs
            |> List.tryFind (fun d -> d.Name = n && VersionRef.matches ver d.Version)
            |> Option.map (fun def ->
                match def.Kind with
                | PromptTool prompt ->
                    // Prompt-defined tool: complete the prompt against this session's
                    // provider, prefixing recent conversation history so the prompt has the
                    // context the user is referring to (mirrors async-agent input handling).
                    let basePromptTool = DefinitionBuilder.buildPromptTool provider def prompt
                    let execute (ctx: ToolContext) (input: string) =
                        let contextual =
                            ConversationContextRender.withHistory 8 (activeConversation().Messages) input
                        basePromptTool.Execute ctx contextual
                    { basePromptTool with Execute = execute }
                | ExecutableTool (_, _, isAsync) ->
                    // Executable tool: build it inline; async tools additionally spawn a
                    // background "tool" task on invocation and return a tracking token.
                    let inlineTool = DefinitionBuilder.buildToolWith policy def
                    if isAsync then DefinitionBuilder.wrapAsyncTool def inlineTool else inlineTool))

    let resolveTools (workspace: WorkspaceDefinitions) (policy: RuntimePolicy) (names: string list) : Tool list =
        names |> List.choose (resolveTool workspace policy)

    // ─── Agent resolution ───

    /// Convert a stored version string ("" = none) into an optional version.
    let versionOpt (version: string) : string option =
        if String.IsNullOrEmpty version then None else Some version

    let findAgentDef (workspace: WorkspaceDefinitions) (name: string) (version: string option) : AgentDef option =
        workspace.AgentDefs
        |> List.tryFind (fun d -> d.Name = name && VersionRef.matches version d.Version)

    let findBuiltAgent (workspace: WorkspaceDefinitions) (name: string) (version: string option) : IAgent option =
        // Pre-built agents are unversioned (None); only resolvable when no version is requested.
        match version with
        | Some _ -> None
        | None -> workspace.Agents |> List.tryFind (fun a -> a.Id.Name = name)

    let agentExists (workspace: WorkspaceDefinitions) (name: string) (version: string option) : bool =
        (findAgentDef workspace name version).IsSome || (findBuiltAgent workspace name version).IsSome

    /// Save a fact into this session's persisted memory, replacing any entry with the same key.
    let saveMemory (key: string) (value: string) =
        let record = MemoryRecord()
        record.Key <- key
        record.Value <- value
        record.Timestamp <- DateTimeOffset.UtcNow
        match persistentState.State.Memories |> Seq.tryFindIndex (fun m -> m.Key = key) with
        | Some idx -> persistentState.State.Memories.[idx] <- record
        | None -> persistentState.State.Memories.Add(record)

    /// IMemoryStore backed by this session's grain state. Per-session scope: the AgentId is
    /// ignored so every agent in the session shares the same persisted memories.
    let sessionMemoryStore =
        { new IMemoryStore with
            member _.SaveAsync _ entry =
                task {
                    saveMemory entry.Key entry.Value
                    do! persistentState.WriteStateAsync()
                }
            member _.RecallAsync _ prefix =
                persistentState.State.Memories
                |> Seq.filter (fun m -> m.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                |> Seq.map GrainStateMapping.toMemoryEntry
                |> List.ofSeq
                |> Task.FromResult
            member _.RecallAllAsync _ =
                persistentState.State.Memories
                |> Seq.map GrainStateMapping.toMemoryEntry
                |> List.ofSeq
                |> Task.FromResult
            member _.ForgetAsync _ key =
                task {
                    match persistentState.State.Memories |> Seq.tryFindIndex (fun m -> m.Key = key) with
                    | Some idx -> persistentState.State.Memories.RemoveAt idx
                    | None -> ()
                    do! persistentState.WriteStateAsync()
                }
            member _.ClearAsync _ =
                task {
                    persistentState.State.Memories.Clear()
                    do! persistentState.WriteStateAsync()
                } }

    let sessionMemoryConfig = { OrchestratorMemoryConfig.None with MemoryStore = Some sessionMemoryStore }

    /// Built-in tool the agent calls to remember a fact across turns of this session. Input
    /// is "key=value" (or plain text, stored under an auto-generated key).
    let sessionMemoryTool =
        Tool.Create(
            "remember",
            "Save a fact to long-term session memory so it can be recalled in later turns. Input: \"key=value\".",
            fun input ->
                task {
                    let key, value =
                        match input.IndexOf '=' with
                        | i when i > 0 -> input.Substring(0, i).Trim(), input.Substring(i + 1).Trim()
                        | _ -> sprintf "note-%d" DateTimeOffset.UtcNow.Ticks, input.Trim()
                    saveMemory key value
                    do! persistentState.WriteStateAsync()
                    return sprintf "Remembered '%s'." key
                })

    /// Build an agent from the resolved definitions (the agent and its sub-agents).
    /// Definitions are used exactly as loaded — feedback annotations are an offline-upgrade
    /// input and are never applied at runtime.
    /// The agent publishes its execution progress (rounds, tool calls, delegations) onto the
    /// durable event bus tagged with `scope`, so the turn's whole process can be recorded.
    let createAgentAsync (workspace: WorkspaceDefinitions) (name: string) (version: string option) (tools: Tool list) (scope: EventScope) (context: ToolContext) : Task<IAgent option> =
        task {
            match findAgentDef workspace name version with
            | Some def ->
                let subAgents = ResizeArray<IAgent>()
                for subName in def.SubAgents do
                    let (subN, subVer) = VersionRef.parse subName
                    if not (String.Equals(subN, name, StringComparison.OrdinalIgnoreCase)) then
                        match findAgentDef workspace subN subVer with
                        | Some subDef ->
                            subAgents.Add(DefinitionBuilder.buildAgentWithFactory orchestratorFactory provider [] [] eventBus scope context OrchestratorMemoryConfig.None subDef)
                        | None ->
                            match findBuiltAgent workspace subN subVer with
                            | Some a -> subAgents.Add a
                            | None -> ()
                let toolsWithMemory = tools @ [ sessionMemoryTool ]
                return Some (DefinitionBuilder.buildAgentWithFactory orchestratorFactory provider toolsWithMemory (List.ofSeq subAgents) eventBus scope context sessionMemoryConfig def)
            | None ->
                return findBuiltAgent workspace name version
        }

    /// Core turn processing. `llmInput` is the prompt the agent sees (may contain embedded
    /// attachment content); `displayText` + `attachmentNames` are what gets persisted into
    /// the rendered transcript so the file body is never stored or shown.
    let processCoreAsync (llmInput: string) (displayText: string) (attachmentNames: string[]) : Task<string> =
        task {
            let agentName = persistentState.State.Info.AgentName
            if String.IsNullOrEmpty(agentName) then
                return "[Error] Session not started. Call StartAsync first."
            elif not persistentState.State.Info.IsActive then
                return "[Error] Session is paused. Call ResumeAsync first."
            else

            match getWorkspace () with
            | None ->
                return sprintf "[Error] Workspace '%s' not available" persistentState.State.Info.WorkspaceKey
            | Some workspace ->

            let agentVersion = versionOpt persistentState.State.Info.AgentVersion
            let info = persistentState.State.Info

            // Agent-level async: when the resolved agent is flagged async — and we are not
            // already executing inside a task's own sub-session — run the whole turn as a
            // spawned background task and return a token immediately, instead of blocking on
            // the harness. The sub-session runs the same agent inline (its Kind = "task").
            let isAsyncAgent =
                info.Kind <> "task"
                && (match findAgentDef workspace agentName agentVersion with
                    | Some def -> def.IsAsync
                    | None -> false)

            if isAsyncAgent then
                let turnId = Guid.NewGuid().ToString("N")
                let title = sprintf "Async run: %s" agentName
                // The spawned agent runs in a fresh sub-session that shares none of this
                // conversation's history, so prefix the recent transcript onto its input —
                // otherwise a follow-up like "convert it to html" has no idea what "it" is.
                let contextualInput =
                    ConversationContextRender.withHistory 8 (activeConversation().Messages) llmInput
                let! taskRef = spawnTaskAsync "agent" title [ "agent", agentName; "input", contextualInput ] turnId
                let tokenMsg =
                    sprintf "Started background task **%s** (`%s`). Track its progress or open the result from the task tag."
                        taskRef.Title taskRef.TaskId
                do! appendTurnAsync displayText attachmentNames tokenMsg turnId []
                info.LastTurnId <- turnId
                info.LastActiveAt <- DateTimeOffset.UtcNow
                do! persistentState.WriteStateAsync()
                return tokenMsg
            else

            let tools = resolveTools workspace (RuntimePolicy.parse persistentState.State.Info.RuntimeMode) (persistentState.State.Info.ToolNames |> Seq.toList)

            // Scope the resolved tools to the ones the agent actually declares. The session's
            // ToolNames is a broad pool; an agent definition narrows it so a tool not listed
            // on the agent is off-limits to it (e.g. convert_document is reserved for the
            // converter agent and must not be callable by the generalist orchestrator).
            let tools =
                match findAgentDef workspace agentName agentVersion with
                | Some def when not (List.isEmpty def.Tools) ->
                    let allowed =
                        def.Tools
                        |> List.map (fun t -> let (n, _) = VersionRef.parse t in n)
                        |> Set.ofList
                    tools |> List.filter (fun t -> allowed.Contains t.Name)
                | _ -> tools

            let turnId = Guid.NewGuid().ToString("N")
            let turnScope = makeScope turnId
            // The recorder must exist before the agent is built so it can subscribe to the
            // event bus and capture this turn's whole execution (rounds, tool calls,
            // delegations) — not just the harness-level final answer. It filters bus events
            // by `scope.ActionId = turnId`, so a shared bus never cross-talks between turns.
            let recorder =
                TurnRecorder.forTools tools
                    (turnId, info.SessionId, info.UserId, info.WorkspaceKey,
                     agentName, agentVersion, llmInput)
            // Expose this turn's recorder so GetLiveStepsAsync can stream progress while
            // the harness runs; always clear it once the turn finishes (success or not).
            currentRecorder <- Some recorder
            // Flow the session/turn identity into the async context so tools that produce
            // files or spawn background tasks can attribute their output to this session.
            // SpawnTask lets a tool launch a grain-backed background task from inside the
            // harness without re-entering this (the primary) grain.
            let sessionKey = sprintf "%s/%s" info.UserId info.SessionId
            // Agents flagged async: the orchestrator spawns a background task when delegating
            // to one of these instead of running it inline (which would lack its own tools).
            let asyncAgentNames =
                workspace.AgentDefs
                |> List.filter (fun d -> d.IsAsync)
                |> List.map (fun d -> d.Name)
                |> Set.ofList
            // A task sub-session works inside its parent's file folder so the input the user
            // attached and the files the task generates are shared with their conversation.
            let filesKey =
                if info.Kind = "task" && not (String.IsNullOrEmpty info.ParentKey) then info.ParentKey
                else sessionKey
            // Build this session's permission context: tools request resource access through
            // it. We answer from the session's OWN granted permissions (held in this grain's
            // state) first, then fall back to the server-registered interactive prompt. A
            // grant the user chose to remember for the session is recorded into this grain's
            // state so the session never re-prompts for it.
            let grants = persistentState.State.GrantedPermissions
            let grantRules () : PermissionRule list =
                grants
                |> Seq.map (fun g ->
                    let kind =
                        match g.Kind with
                        | "file" -> ResourceKind.File
                        | "web" -> ResourceKind.Web
                        | _ -> ResourceKind.Tool
                    { Id = ""
                      Kind = kind
                      Pattern = g.Pattern
                      Operations = []
                      Decision = PermissionDecision.Allow
                      Scope = RuleScope.Session sessionKey
                      CreatedAt = DateTimeOffset.UtcNow })
                |> List.ofSeq
            // Does a stored grant pattern of this kind cover a resource pattern? Uses the
            // same matching the evaluator does, so redundant or overlapping grants (e.g.
            // granting "/a" then "/a/b") collapse instead of letting the list grow unbounded.
            let covers (kind: string) (broader: string) (narrower: string) =
                match kind with
                | "file" -> ResourcePermission.pathMatches broader narrower
                | "web" -> ResourcePermission.hostMatches broader narrower
                | _ -> String.Equals(broader, narrower, StringComparison.OrdinalIgnoreCase)
            let recordGrant (access: ResourceAccess) =
                let kind, pattern =
                    match access with
                    | ResourceAccess.File(_, path) -> "file", path
                    | ResourceAccess.Web(_, url) -> "web", (ResourcePermission.hostOf url |> Option.defaultValue url)
                    | ResourceAccess.ToolCall name -> "tool", name
                // Skip when a broader (or equal) existing grant already covers this resource.
                if not (grants |> Seq.exists (fun g -> g.Kind = kind && covers kind g.Pattern pattern)) then
                    // Drop any narrower existing grants this broader one subsumes, then add it.
                    let subsumed =
                        grants
                        |> Seq.filter (fun g -> g.Kind = kind && covers kind pattern g.Pattern)
                        |> Seq.toList
                    for g in subsumed do
                        grants.Remove g |> ignore
                    grants.Add(PermissionGrantRecord(Kind = kind, Pattern = pattern))
            let requestPermission (access: ResourceAccess) (reason: string) (forceConfirm: bool) : Task<bool> =
                task {
                    // grantRules are all session-scoped for this key; filter through `applicable`
                    // so the scope-agnostic evaluator only ever sees rules that apply here.
                    let applicable = ResourcePermission.applicable sessionKey (grantRules ())
                    if ResourcePermission.evaluateWith PermissionDecision.Deny applicable access = PermissionDecision.Allow then
                        return true
                    else
                        match PermissionGate.Prompt with
                        | None -> return true // no permission system wired → allow
                        | Some prompt ->
                            let! outcome = prompt sessionKey access reason forceConfirm
                            if outcome.Decision = PermissionDecision.Allow && outcome.RememberForSession then
                                recordGrant access
                                try do! persistentState.WriteStateAsync() with _ -> ()
                            return outcome.Decision = PermissionDecision.Allow
                }
            // Flow the session/turn identity explicitly into tools and the orchestrator via
            // this context: tools resolve files under FilesKey, request resource access through
            // RequestPermission, and SpawnTask lets a tool/delegation launch a grain-backed
            // background task from inside the harness without re-entering this (primary) grain.
            let delegableAsyncAgents =
                if info.Kind = "task" then Set.empty else asyncAgentNames

            let toolContext : ToolContext =
                { SessionKey = sessionKey
                  FilesKey = filesKey
                  AsyncAgents = delegableAsyncAgents
                  TurnId = turnId
                  SpawnTask = fun spec ->
                      task {
                          let! taskRef = spawnTaskAsync spec.Kind spec.Title (spec.Params |> Map.toList) turnId
                          return taskRef.TaskId }
                  RequestPermission = requestPermission }
            try
                // Subscribe the recorder for this turn's progress signals; detached in finally.
                eventBus.Subscribe(recorder :> IEventConsumer)
                let! agentOpt = createAgentAsync workspace agentName agentVersion tools turnScope toolContext
                match agentOpt with
                | None ->
                    return sprintf "[Error] Agent '%s' not found in workspace '%s'" agentName persistentState.State.Info.WorkspaceKey
                | Some agent ->
                    let harnessServices = harnessServicesFactory.Invoke(sessionKey, turnId)
                    let harnessConfig = buildHarnessConfig workspace tools turnScope harnessServices
                    let! result = EtclovgHarness.runAsync harnessConfig agent llmInput

                    match result.Success, result.Response with
                    | true, Some response ->
                        // Emit the completed turn; a subscribed storage strategy persists it
                        // so feedback can later be analysed against it.
                        let turnRecord = { recorder.Snapshot() with Output = response }
                        do! eventBus.PublishAsync(TurnCompleted(makeScope turnId, turnRecord))

                        // Persist a CLEAN, user-facing transcript: the display text (no embedded
                        // attachment content) plus one assistant message carrying the process.
                        do! appendTurnAsync displayText attachmentNames response turnId recorder.Steps
                        info.LastTurnId <- turnId

                        persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                        do! persistentState.WriteStateAsync()
                        return response

                    | _ ->
                        let errorMsg =
                            match result.HarnessError with
                            | Some err -> sprintf "[Blocked] %s" err.Message
                            | None -> result.Error |> Option.defaultValue "[Error] Unknown harness failure"
                        return errorMsg
            finally
                eventBus.Unsubscribe(recorder :> IEventConsumer)
                currentRecorder <- None
        }
    // ─── Activation ───

    /// Resolve this session's per-session observability and feedback stores from the grain
    /// key ("userId/sessionId") so each session writes them under its own sessions/<key>/.
    override this.OnActivateAsync(cancellationToken: System.Threading.CancellationToken) : Task =
        let key = this.GetPrimaryKeyString()
        feedback <- feedbackFactory.Invoke key
        base.OnActivateAsync(cancellationToken)

    // ─── Interface implementation ───

    interface ISessionGrain with
        member this.StartAsync(options: SessionStartOptions) : Task<bool> =
            task {
                let (userId, sessionId) = parseKey (this.GetPrimaryKeyString())

                let workspaceKey =
                    if String.IsNullOrEmpty(options.WorkspaceKey) then "default"
                    else options.WorkspaceKey

                match registry.TryGet (WorkspaceId.create workspaceKey) with
                | None -> return false
                | Some workspace ->

                // Agent version may be specified explicitly or inline as "name@version".
                let (agentName, inlineVersion) = VersionRef.parse options.AgentName
                let agentVersion =
                    if String.IsNullOrEmpty options.AgentVersion then inlineVersion
                    else Some options.AgentVersion

                if not (agentExists workspace agentName agentVersion) then
                    return false
                else
                    let info = persistentState.State.Info
                    info.AgentName <- agentName
                    info.AgentVersion <- (agentVersion |> Option.defaultValue "")
                    info.SessionId <- sessionId
                    info.UserId <- userId
                    info.GroupId <- options.GroupId
                    info.WorkspaceKey <- workspaceKey
                    info.IsActive <- true
                    info.ToolNames <- ResizeArray(options.ToolNames)
                    info.RuntimeMode <- options.RuntimeMode
                    info.Kind <- (if String.IsNullOrEmpty options.Kind then "primary" else options.Kind)
                    info.ParentKey <- options.ParentKey
                    info.ActiveConversation <- "default"
                    if info.CreatedAt = DateTimeOffset.MinValue then
                        info.CreatedAt <- DateTimeOffset.UtcNow
                    info.LastActiveAt <- DateTimeOffset.UtcNow

                    // Ensure default conversation exists
                    getOrCreateConversation "default" |> ignore

                    do! persistentState.WriteStateAsync()
                    return true
            }

        member _.ProcessAsync(input: string) : Task<string> =
            processCoreAsync input input [||]

        member _.ProcessWithContextAsync(llmInput: string, displayText: string, attachmentNames: string[]) : Task<string> =
            processCoreAsync llmInput displayText (if isNull attachmentNames then [||] else attachmentNames)

        member _.GetLastTurnIdAsync() : Task<string> =
            Task.FromResult(persistentState.State.Info.LastTurnId)

        member _.GetLiveStepsAsync() : Task<TurnStepRecord[]> =
            match currentRecorder with
            | Some recorder ->
                Task.FromResult(recorder.Steps |> List.map toStepRecord |> List.toArray)
            | None -> Task.FromResult([||])

        member _.SubmitFeedbackAsync(sentiment: string, comment: string) : Task<string array> =
            task {
                let info = persistentState.State.Info
                if String.IsNullOrEmpty info.LastTurnId then
                    return [||]
                else
                    let parsedSentiment =
                        match (sentiment |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
                        | "positive" | "up" | "good" -> FeedbackSentiment.Positive
                        | "negative" | "down" | "bad" -> FeedbackSentiment.Negative
                        | _ -> FeedbackSentiment.Neutral
                    let fb =
                        { Id = Guid.NewGuid()
                          TurnId = info.LastTurnId
                          SessionId = info.SessionId
                          UserId = info.UserId
                          Sentiment = parsedSentiment
                          Comment = if String.IsNullOrWhiteSpace comment then None else Some comment
                          CreatedAt = DateTimeOffset.UtcNow
                          Metadata = Map.empty }
                    do! feedback.SubmitFeedbackAsync fb
                    return [||]
            }

        member _.SwitchWorkspaceAsync(workspaceKey: string) : Task<bool> =
            task {
                match registry.TryGet (WorkspaceId.create workspaceKey) with
                | None -> return false
                | Some workspace ->
                    let agentName = persistentState.State.Info.AgentName
                    let agentVersion = versionOpt persistentState.State.Info.AgentVersion
                    if not (String.IsNullOrEmpty(agentName)) && not (agentExists workspace agentName agentVersion) then
                        return false
                    else
                        persistentState.State.Info.WorkspaceKey <- workspaceKey
                        persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                        do! persistentState.WriteStateAsync()
                        return true
            }

        member _.SwitchAgentAsync(agentName: string) : Task<bool> =
            task {
                match getWorkspace () with
                | None -> return false
                | Some workspace ->
                    // The target agent may be version-qualified ("name@version").
                    let (targetName, targetVersion) = VersionRef.parse agentName
                    if not (agentExists workspace targetName targetVersion) then
                        return false
                    else
                        persistentState.State.Info.AgentName <- targetName
                        persistentState.State.Info.AgentVersion <- (targetVersion |> Option.defaultValue "")
                        persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                        do! persistentState.WriteStateAsync()
                        return true
            }

        member _.SwitchConversationAsync(conversationName: string) : Task =
            task {
                persistentState.State.Info.ActiveConversation <- conversationName
                getOrCreateConversation conversationName |> ignore
                persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                do! persistentState.WriteStateAsync()
            }

        member _.ListConversationsAsync() : Task<string array> =
            persistentState.State.Conversations
            |> Seq.map (fun c -> c.Name)
            |> Seq.toArray
            |> Task.FromResult

        member _.GetInfoAsync() : Task<SessionInfo> =
            Task.FromResult(persistentState.State.Info)

        member _.GetHistoryAsync() : Task<MessageRecord array> =
            let ctx = activeConversation ()
            // Restore messages from persistent storage if they're not already loaded in memory
            if ctx.Messages.Count = 0 then
                let sessionId = persistentState.State.Info.SessionId
                let userId = persistentState.State.Info.UserId
                let convName = persistentState.State.Info.ActiveConversation
                if not (String.IsNullOrEmpty sessionId) then
                    let grainKey = sprintf "%s/%s" userId sessionId
                    let loaded = conversationStore.LoadAsync grainKey convName
                    for pm in loaded.Result do
                        let record = MessageRecord()
                        record.Role <- pm.Role
                        record.Content <- pm.Content
                        record.TurnId <- (if isNull (box pm.TurnId) then "" else pm.TurnId)
                        if not (isNull (box pm.Steps)) then
                            record.Steps <- ResizeArray(pm.Steps)
                        if not (isNull (box pm.Attachments)) then
                            record.Attachments <- ResizeArray(pm.Attachments)
                        ctx.Messages.Add(record)
            ctx.Messages |> Seq.toArray |> Task.FromResult

        member this.ClearHistoryAsync() : Task =
            task {
                let ctx = activeConversation ()
                ctx.Messages.Clear()
                let grainKey = this.GetPrimaryKeyString()
                let convName = persistentState.State.Info.ActiveConversation
                do! conversationStore.DeleteConversationAsync grainKey convName
                do! persistentState.WriteStateAsync()
            }

        member _.SaveMemoryAsync(key: string, value: string) : Task =
            task {
                saveMemory key value
                do! persistentState.WriteStateAsync()
            }

        member _.RecallMemoryAsync(key: string) : Task<MemoryEntry array> =
            persistentState.State.Memories
            |> Seq.filter (fun m -> m.Key.Contains(key, StringComparison.OrdinalIgnoreCase))
            |> Seq.map GrainStateMapping.toMemoryEntry
            |> Seq.toArray
            |> Task.FromResult

        member _.ClearMemoriesAsync() : Task =
            task {
                persistentState.State.Memories.Clear()
                do! persistentState.WriteStateAsync()
            }

        member _.PauseAsync() : Task =
            task {
                persistentState.State.Info.IsActive <- false
                do! persistentState.WriteStateAsync()
            }

        member _.ResumeAsync() : Task =
            task {
                persistentState.State.Info.IsActive <- true
                persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                do! persistentState.WriteStateAsync()
            }

        member this.DestroyAsync() : Task =
            task {
                // Clean up external conversation store
                let grainKey = this.GetPrimaryKeyString()
                do! conversationStore.DeleteSessionAsync(grainKey)
                do! persistentState.ClearStateAsync()
                this.DeactivateOnIdle()
            }

        member _.UpdateTaskStatusAsync(taskRef: TaskRef) : Task =
            task {
                upsertTaskRef taskRef
                do! persistentState.WriteStateAsync()
            }

        member _.ListTasksAsync() : Task<TaskRef array> =
            persistentState.State.Tasks
            |> Seq.sortByDescending (fun t -> t.CreatedAt)
            |> Seq.toArray
            |> Task.FromResult

module SessionGrain =

    /// Build a grain key from userId and sessionId
    let buildKey (userId: string) (sessionId: string) =
        sprintf "%s/%s" userId sessionId
