namespace Nao.Runtime.Orleans.Grains

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Nao.Agents
open Nao.Runtime.Orleans

// Allow the Orleans C# codegen project to access internal F# DU backing fields.
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Nao.Runtime.Orleans.Codegen")>]
[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Nao.Runtime.Orleans.Tests")>]
do ()

/// Metadata about a session
[<GenerateSerializer>]
type SessionInfo() =
    [<Id(0u)>]
    member val AgentName: string = "" with get, set

    [<Id(1u)>]
    member val SessionId: string = "" with get, set

    [<Id(2u)>]
    member val UserId: string = "" with get, set

    [<Id(3u)>]
    member val GroupId: string = "" with get, set

    [<Id(4u)>]
    member val WorkspaceKey: string = "default" with get, set

    [<Id(5u)>]
    member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

    [<Id(6u)>]
    member val LastActiveAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

    [<Id(7u)>]
    member val IsActive: bool = true with get, set

    [<Id(8u)>]
    member val ToolNames: ResizeArray<string> = ResizeArray() with get, set

    [<Id(9u)>]
    member val ActiveConversation: string = "default" with get, set

    /// Id of the most recently processed turn (used to attach feedback).
    [<Id(11u)>]
    member val LastTurnId: string = "" with get, set

    /// Runtime policy for launching tools ("" = host default; "docker"/"docker:&lt;image&gt;"
    /// to containerize; a runtime name like "deno" to force it for all tools).
    [<Id(12u)>]
    member val RuntimeMode: string = "" with get, set

    /// Tenant identity supplied by the trusted host principal.
    [<Id(13u)>]
    member val TenantId: string = "" with get, set

/// A named conversation context within a session
[<GenerateSerializer>]
type ConversationContext() =
    [<Id(0u)>]
    member val Name: string = "default" with get, set

    [<Id(1u)>]
    member val Messages: ResizeArray<MessageRecord> = ResizeArray() with get, set

    [<Id(2u)>]
    member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

    [<Id(3u)>]
    member val AgentName: string = "" with get, set

/// A resource-access grant the user approved for the lifetime of this session. Stored in the
/// session grain's own state (Orleans-persisted) so each session tracks its own permissions
/// and never re-prompts for something it already approved.
[<GenerateSerializer>]
type PermissionGrantRecord() =
    /// Resource class: "file", "web", or "tool".
    [<Id(0u)>]
    member val Kind: string = "" with get, set

    /// Match pattern: a file path, a web host, or a tool name.
    [<Id(1u)>]
    member val Pattern: string = "" with get, set

/// Persistent state for a session grain
[<GenerateSerializer>]
type SessionGrainState() =
    [<Id(0u)>]
    member val Info: SessionInfo = SessionInfo() with get, set

    [<Id(1u)>]
    member val Conversations: ResizeArray<ConversationContext> = ResizeArray() with get, set

    /// Resource-access grants the user approved for this session ("remember for session").
    [<Id(4u)>]
    member val GrantedPermissions: ResizeArray<PermissionGrantRecord> = ResizeArray() with get, set

    [<Id(5u)>]
    member val SchemaVersion: int = 0 with get, set

/// Options for starting or reconfiguring a session
[<GenerateSerializer>]
type SessionStartOptions() =
    [<Id(0u)>]
    member val AgentName: string = "" with get, set

    [<Id(1u)>]
    member val ToolNames: ResizeArray<string> = ResizeArray() with get, set

    [<Id(2u)>]
    member val WorkspaceKey: string = "default" with get, set

    [<Id(3u)>]
    member val GroupId: string = "" with get, set

    /// Runtime policy for launching tools ("" = host default; "docker"/"docker:&lt;image&gt;"
    /// to containerize; a runtime name like "deno" to force it for all tools).
    [<Id(5u)>]
    member val RuntimeMode: string = "" with get, set

module internal SessionAuthorization =
    let tryCreateScope
        (principal: SecurityPrincipal)
        (grainUserId: string)
        (grainSessionId: string)
        (groupId: string)
        (workspaceKey: string)
        =
        let parsedGroupId =
            if String.IsNullOrWhiteSpace groupId then
                Some None
            else
                GroupId.tryParse groupId |> Option.map Some

        match
            UserId.tryParse grainUserId,
            SessionId.tryParse grainSessionId,
            parsedGroupId,
            WorkspaceId.tryParse workspaceKey
        with
        | Some userId, Some sessionId, Some requestedGroupId, Some workspaceId when
            userId = SecurityPrincipal.userId principal
            ->
            AuthorizationScope.tryCreate principal requestedGroupId workspaceId (Some sessionId)
        | _ -> None

    let matchesPersisted (info: SessionInfo) scope =
        let groupMatches =
            AuthorizationScope.groupId scope
            |> Option.map GroupId.value
            |> Option.defaultValue ""
            |> (=) info.GroupId

        info.TenantId = (AuthorizationScope.tenantId scope |> TenantId.value)
        && info.UserId = (AuthorizationScope.userId scope |> UserId.value)
        && info.SessionId = (AuthorizationScope.sessionId scope
                             |> Option.map SessionId.value
                             |> Option.defaultValue "")
        && groupMatches

/// Orleans grain interface for a user session.
/// Grain key format: "userId/sessionId"
/// The grain resolves agents/tools from the workspace registry.
type ISessionGrain =
    inherit IGrainWithStringKey

    /// Initialize the session with a specific agent, workspace, and optional tool overrides.
    abstract member StartAsync: options: SessionStartOptions -> Task<bool>

    /// Process user input through the ETCLOVG harness until completion or caller cancellation.
    abstract member ProcessAsync: input: string * cancellationToken: CancellationToken -> Task<string>

    /// Process user input where the LLM prompt and the persisted/display text differ:
    /// `llmInput` (with embedded attachment content) is what the agent sees, while only
    /// `displayText` plus `attachmentNames` are stored in the transcript. Caller cancellation
    /// flows through the grain boundary into the harness.
    abstract member ProcessWithContextAsync:
        llmInput: string * displayText: string * attachmentNames: string[] * cancellationToken: CancellationToken ->
            Task<string>

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

/// Self-contained session grain. Resolves workspaces from WorkspaceRegistry,
/// manages multiple conversation contexts and memory through Orleans persistence.
type SessionGrain
    (
        [<PersistentState("sessionState", "sessionStore")>] persistentState: IPersistentState<SessionGrainState>,
        registry: WorkspaceRegistry,
        provider: LlmProvider,
        orchestratorFactory: OrchestratorFactory,
        conversationStore: ConversationStore,
        principalAccessor: Func<SecurityPrincipal>,
        harnessServicesFactory: Func<string, string, CorrelationContext, HarnessServices>,
        feedbackFactory: Func<string, FeedbackService>,
        eventBus: EventBus,
        memoryStore: MemoryStore,
        memoryToolConfig: MemoryToolConfig,
        grainFactory: IGrainFactory
    ) as this =
    inherit Grain()

    /// The recorder for the turn currently being processed (None when idle). Held so the
    /// reentrant `GetLiveStepsAsync` can surface in-progress steps for live UIs. Reads go
    /// through `recorder.Steps`, which is internally lock-protected.
    let mutable currentRecorder: TurnRecorder option = None

    /// Per-session feedback store, resolved once from the grain key in OnActivateAsync so
    /// this session's annotations are written under sessions/<key>/. Observability services
    /// are built per turn (so each signal carries its turn id) in ProcessWithContextAsync.
    let mutable feedback: FeedbackService = Unchecked.defaultof<FeedbackService>

    // ─── Workspace resolution ───

    let getWorkspace () : WorkspaceDefinitions option =
        let key = WorkspaceId.create persistentState.State.Info.WorkspaceKey
        registry.TryGet key

    /// Build the identity envelope carried by every emitted event. `actionId` is the turn
    /// the event is about. Producers never decide where data lands — a subscribed storage
    /// strategy routes it (per session, per category, ...) from this scope.
    let makeScope (actionId: string) (correlation: CorrelationContext) : EventScope =
        let info = persistentState.State.Info
        let sessionKey = sprintf "%s/%s" info.UserId info.SessionId

        EventScope.Create(
            info.UserId,
            info.SessionId,
            info.ActiveConversation,
            info.WorkspaceKey,
            actionId,
            sessionKey,
            correlation
        )

    let buildHarnessConfig
        (workspace: WorkspaceDefinitions)
        (tools: Tool list)
        (services: HarnessServices)
        : EtclovgConfig =
        let constitution = WorkspaceDefinitions.mergedConstitution workspace

        let toolProtocol =
            if tools.Length > 0 then
                Some(ToolProtocol.fromTools tools)
            else
                None

        let baseConfig =
            { EtclovgConfig.Default with
                Constitution = constitution
                ToolProtocol = toolProtocol
                Bus = eventBus }

        baseConfig.WithServices(services)

    // ─── Key parsing ───

    let parseKey (key: string) =
        match key.IndexOf('/') with
        | -1 -> (key, "default")
        | idx -> (key.Substring(0, idx), key.Substring(idx + 1))

    let tryCurrentScope workspaceKey =
        let info = persistentState.State.Info

        SessionAuthorization.tryCreateScope
            (principalAccessor.Invoke())
            info.UserId
            info.SessionId
            info.GroupId
            workspaceKey
        |> Option.filter (SessionAuthorization.matchesPersisted info)

    let requireCurrentScope () =
        match tryCurrentScope persistentState.State.Info.WorkspaceKey with
        | Some scope -> scope
        | None ->
            PlatformFailure.create
                PlatformErrorCategory.PermissionDenied
                "The authenticated principal is not authorized for this session."
                false
                None
            |> PlatformFailure.raiseException

    // ─── Conversation context management ───

    let getOrCreateConversation (name: string) : ConversationContext =
        let existing =
            persistentState.State.Conversations |> Seq.tryFind (fun c -> c.Name = name)

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

    let toMessageRecord (pm: PersistedMessage) =
        let record = MessageRecord()
        record.Role <- pm.Role
        record.Content <- pm.Content
        record.TurnId <- pm.TurnId
        record.Correlation <- pm.Correlation
        record.Steps <- ResizeArray(pm.Steps)
        record.Attachments <- ResizeArray(pm.Attachments)
        record.Artifacts <- ResizeArray(pm.Artifacts)
        record

    let restoreActiveConversationAsync () =
        task {
            let ctx = activeConversation ()

            if ctx.Messages.Count = 0 then
                let sessionId = persistentState.State.Info.SessionId
                let userId = persistentState.State.Info.UserId
                let convName = persistentState.State.Info.ActiveConversation

                if not (String.IsNullOrEmpty sessionId) then
                    let grainKey = sprintf "%s/%s" userId sessionId
                    let! loaded = conversationStore.LoadAsync grainKey convName

                    for pm in loaded do
                        ctx.Messages.Add(toMessageRecord pm)
        }

    /// Map a recorded process step onto its serializable storage form.
    let toStepRecord (s: TurnStep) : TurnStepRecord =
        TurnStepRecord(Kind = s.Kind, Title = s.Title, Input = s.Input, Output = s.Output)

    let toPersistedMessage
        timestamp
        correlation
        turnId
        role
        content
        messageSteps
        attachments
        messageArtifacts
        : PersistedMessage =
        { Role = role
          Content = content
          Timestamp = timestamp
          Correlation = correlation
          TurnId = turnId
          Steps = messageSteps
          Attachments = attachments
          Artifacts = messageArtifacts }

    /// Append a completed turn — the user prompt plus a single assistant message that
    /// carries the whole process (ordered tool/sub-agent steps) and the final answer —
    /// to both in-memory grain state and the external append-only store.
    ///
    /// The orchestrator's internal LLM conversation (where tool results are themselves
    /// `User` messages and intermediate action-JSON is an `Assistant` message) is an
    /// implementation detail and is intentionally NOT persisted as the transcript.
    let appendTurnAsync
        (userInput: string)
        (attachmentNames: string[])
        (response: string)
        (turnId: string)
        (correlation: CorrelationContext)
        (steps: TurnStep list)
        (artifacts: Artifact list)
        : Task =
        task {
            let ctx = activeConversation ()
            let stepRecords = steps |> List.map toStepRecord

            let artifactRecords =
                artifacts
                |> List.map (fun value ->
                    ArtifactRecord(
                        Id = ArtifactId.serialize value.Id,
                        Kind = value.Kind,
                        ContentType = value.ContentType,
                        Payload = value.Payload
                    ))

            let userRecord =
                MessageRecord(
                    Role = "User",
                    Content = userInput,
                    TurnId = turnId,
                    Correlation = correlation,
                    Attachments = ResizeArray(attachmentNames)
                )

            let assistantRecord =
                MessageRecord(
                    Role = "Assistant",
                    Content = response,
                    TurnId = turnId,
                    Correlation = correlation,
                    Steps = ResizeArray(stepRecords),
                    Artifacts = ResizeArray(artifactRecords)
                )

            ctx.Messages.Add userRecord
            ctx.Messages.Add assistantRecord

            let sessionId = persistentState.State.Info.SessionId
            let userId = persistentState.State.Info.UserId
            let convName = persistentState.State.Info.ActiveConversation

            if not (String.IsNullOrEmpty sessionId) then
                let grainKey = sprintf "%s/%s" userId sessionId
                let now = DateTimeOffset.UtcNow

                let persisted =
                    [| toPersistedMessage now correlation turnId "User" userInput [||] attachmentNames [||]
                       toPersistedMessage
                           now
                           correlation
                           turnId
                           "Assistant"
                           response
                           (stepRecords |> List.toArray)
                           [||]
                           (artifactRecords |> List.toArray) |]

                do! conversationStore.AppendAsync grainKey convName persisted

                // Persist each sub-agent delegation as a nested child conversation, so the
                // delegated work is browsable beneath its parent rather than only inlined as a
                // step. One child conversation per delegation (uniquely keyed by turn + index).
                let agentSteps = steps |> List.filter (fun s -> s.Kind = "agent")
                let mutable idx = 0

                for s in agentSteps do
                    idx <- idx + 1

                    let title =
                        if String.IsNullOrWhiteSpace s.Title then
                            "agent"
                        else
                            s.Title

                    let childName = sprintf "%s/%s-%s-%d" convName title turnId idx

                    let childMsgs =
                        [| toPersistedMessage now correlation turnId "User" s.Input [||] [||] [||]
                           toPersistedMessage now correlation turnId "Assistant" s.Output [||] [||] [||] |]

                    do! conversationStore.AppendAsync grainKey childName childMsgs
        }

    // ─── Tool resolution ───

    let resolveTool (workspace: WorkspaceDefinitions) (name: string) : Tool option =
        workspace.Tools |> List.tryFind (fun tool -> tool.Name = name)

    let resolveTools (workspace: WorkspaceDefinitions) (names: string list) : Tool list =
        names |> List.choose (resolveTool workspace)

    // ─── Agent resolution ───

    let findBuiltAgent (workspace: WorkspaceDefinitions) (name: string) : Agent option =
        workspace.Agents |> List.tryFind (fun agent -> agent.Metadata.Name = name)

    let agentExists (workspace: WorkspaceDefinitions) (name: string) : bool = (findBuiltAgent workspace name).IsSome

    /// Stable external-storage owner for this session. The grain key is immutable even
    /// though the session's selected agent and other runtime settings may change.
    let memoryOwner () =
        sprintf "session:%s" (this.GetPrimaryKeyString())

    let addMemoryAgentTool (tools: Tool list) =
        let existingNames = tools |> List.map _.Name |> Set.ofList
        let memoryTools = MemoryTools.create memoryToolConfig memoryStore memoryOwner

        if memoryTools.IsEmpty || existingNames.Contains "memory" then
            tools
        else
            let memoryAgent = MemoryAgent.create orchestratorFactory provider memoryTools
            MemoryAgent.asTool memoryAgent :: tools

    let createAgentAsync (workspace: WorkspaceDefinitions) (name: string) : Task<Agent option> =
        findBuiltAgent workspace name |> Task.FromResult

    /// Core turn processing. `llmInput` is the prompt the agent sees (may contain embedded
    /// attachment content); `displayText` + `attachmentNames` are what gets persisted into
    /// the rendered transcript so the file body is never stored or shown.
    let processCoreAsync
        (llmInput: string)
        (displayText: string)
        (attachmentNames: string[])
        (cancellationToken: CancellationToken)
        : Task<string> =
        task {
            requireCurrentScope () |> ignore
            let agentName = persistentState.State.Info.AgentName

            if String.IsNullOrEmpty(agentName) then
                return "[Error] Session not started. Call StartAsync first."
            elif not persistentState.State.Info.IsActive then
                return "[Error] Session is paused. Call ResumeAsync first."
            else

                do! restoreActiveConversationAsync ()

                match getWorkspace () with
                | None -> return sprintf "[Error] Workspace '%s' not available" persistentState.State.Info.WorkspaceKey
                | Some workspace ->

                    let info = persistentState.State.Info

                    let tools =
                        resolveTools workspace (persistentState.State.Info.ToolNames |> Seq.toList)

                    // The persisted display text remains the user's latest message; this expanded
                    // input is only what the agent sees for resolving follow-ups.
                    let contextualInput =
                        ConversationContextRender.withHistory 8 (activeConversation().Messages) llmInput

                    let turnId = TurnId.generate () |> TurnId.value
                    let correlation = CorrelationContext.root ()
                    let turnScope = makeScope turnId correlation
                    // The recorder must exist before the agent is built so it can subscribe to the
                    // event bus and capture this turn's whole execution (rounds, tool calls,
                    // delegations) — not just the harness-level final answer. It filters bus events
                    // by `scope.ActionId = turnId`, so a shared bus never cross-talks between turns.
                    let recorder =
                        TurnRecorder.create (
                            turnId,
                            correlation,
                            info.SessionId,
                            info.UserId,
                            info.WorkspaceKey,
                            agentName,
                            contextualInput
                        )
                    // Expose this turn's recorder so GetLiveStepsAsync can stream progress while
                    // the harness runs; always clear it once the turn finishes (success or not).
                    currentRecorder <- Some recorder
                    // Flow the session/turn identity into tools so file operations and permission
                    // requests can attribute their output to this session.
                    let sessionKey = sprintf "%s/%s" info.UserId info.SessionId
                    // Build this session's permission context: tools request resource access through
                    // it. We answer from the session's OWN granted permissions (held in this grain's
                    // state) first, then fall back to the server-registered interactive prompt. A
                    // grant the user chose to remember for the session is recorded into this grain's
                    // state so the session never re-prompts for it.
                    let grants = persistentState.State.GrantedPermissions

                    let targetOf kind pattern =
                        match kind with
                        | "file" -> PermissionTarget.File(pattern, [])
                        | "web" -> PermissionTarget.Web(pattern, [])
                        | _ -> PermissionTarget.Tool pattern

                    let grantRules () : PermissionRule list =
                        grants
                        |> Seq.map (fun g ->
                            { Id = ""
                              AppliesTo = targetOf g.Kind g.Pattern
                              Decision = PermissionDecision.Allow
                              Scope = RuleScope.Session sessionKey
                              CreatedAt = DateTimeOffset.UtcNow })
                        |> List.ofSeq
                    // Does a stored grant pattern of this kind cover a resource pattern? Uses the
                    // same matching the evaluator does, so redundant or overlapping grants (e.g.
                    // granting "/a" then "/a/b") collapse instead of letting the list grow unbounded.
                    let covers kind broader narrower =
                        ResourcePermission.targetCovers (targetOf kind broader) (targetOf kind narrower)

                    let recordGrant (access: ResourceAccess) =
                        let kind, pattern =
                            match access with
                            | ResourceAccess.File(_, path) -> "file", path
                            | ResourceAccess.Web(_, url) ->
                                "web", (ResourcePermission.hostOf url |> Option.defaultValue url)
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

                    let resolvePermission (access: ResourceAccess) (reason: string) (forceConfirm: bool) : Task<bool> =
                        task {
                            // grantRules are all session-scoped for this key; filter through `applicable`
                            // so the scope-agnostic evaluator only ever sees rules that apply here.
                            let applicable = ResourcePermission.applicable sessionKey (grantRules ())

                            if
                                ResourcePermission.evaluateWith PermissionDecision.Deny applicable access = PermissionDecision.Allow
                            then
                                return true
                            else
                                let! outcome = PermissionGate.resolve sessionKey access reason forceConfirm

                                if outcome.Decision = PermissionDecision.Allow && outcome.RememberForSession then
                                    recordGrant access

                                    try
                                        do! persistentState.WriteStateAsync()
                                    with _ ->
                                        ()

                                return outcome.Decision = PermissionDecision.Allow
                        }

                    let artifacts = ResizeArray<Artifact>()
                    let grantedResources = ResizeArray<ResourceAccess>()
                    let permissionLock = new SemaphoreSlim(1, 1)

                    let requestPermission access reason forceConfirm =
                        task {
                            do! permissionLock.WaitAsync()

                            try
                                let alreadyGranted =
                                    lock grantedResources (fun () ->
                                        grantedResources
                                        |> Seq.exists (fun approved -> ResourceAccess.isCoveredBy approved access))

                                if alreadyGranted then
                                    return true
                                else
                                    let! allowed = resolvePermission access reason forceConfirm

                                    if allowed then
                                        lock grantedResources (fun () -> grantedResources.Add access)

                                    return allowed
                            finally
                                permissionLock.Release() |> ignore
                        }

                    let publishArtifact artifact : Task =
                        (task {
                            lock artifacts (fun () -> artifacts.Add artifact)
                            do! EventBus.publishAsync (TurnProgress(turnScope, ArtifactPublished artifact)) eventBus
                        }
                        :> Task)

                    let agentContext: AgentContext =
                        { Correlation = correlation
                          SessionKey = sessionKey
                          TurnId = turnId
                          ExecutionBoundary = ExecutionBoundary.HarnessRequired
                          CancellationToken = cancellationToken
                          GetArtifacts = (fun () -> lock artifacts (fun () -> List.ofSeq artifacts))
                          GetGrantedResources =
                            (fun () -> lock grantedResources (fun () -> List.ofSeq grantedResources))
                          RequestPermission = requestPermission
                          PublishArtifact = publishArtifact }

                    try
                        // Subscribe the recorder for this turn's progress signals; detached in finally.
                        EventBus.subscribe recorder.Consumer eventBus
                        let! agentOpt = createAgentAsync workspace agentName

                        match agentOpt with
                        | None ->
                            return
                                sprintf
                                    "[Error] Agent '%s' not found in workspace '%s'"
                                    agentName
                                    persistentState.State.Info.WorkspaceKey
                        | Some agent ->
                            let harnessServices = harnessServicesFactory.Invoke(sessionKey, turnId, correlation)

                            let harnessConfig = buildHarnessConfig workspace tools harnessServices

                            let request =
                                ExecutionRequest.create
                                    (requireCurrentScope ())
                                    (TurnId.parse turnId)
                                    info.ActiveConversation
                                    agent.Metadata.Id
                                    contextualInput
                                    SandboxConfig.Default
                                    Map.empty
                                    Map.empty
                                    correlation

                            let! result =
                                EtclovgHarness.runAsync harnessConfig agentContext agent request cancellationToken

                            match result.Status, result.Outputs.Response with
                            | ExecutionTerminalStatus.Succeeded, Some response ->
                                // Emit the completed turn; a subscribed storage strategy persists it
                                // so feedback can later be analysed against it.
                                let turnRecord =
                                    { TurnRecorder.snapshot recorder with
                                        Output = response }

                                do!
                                    EventBus.publishAsync
                                        (TurnCompleted(makeScope turnId correlation, turnRecord))
                                        eventBus

                                // Persist a CLEAN, user-facing transcript: the display text (no embedded
                                // attachment content) plus one assistant message carrying the process.
                                do!
                                    appendTurnAsync
                                        displayText
                                        attachmentNames
                                        response
                                        turnId
                                        correlation
                                        (TurnRecorder.steps recorder)
                                        (TurnRecorder.artifacts recorder)

                                info.LastTurnId <- turnId

                                persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                                do! persistentState.WriteStateAsync()
                                return response

                            | _ ->
                                let failure = result.Status.ToPlatformFailure(Some turnId)

                                return PlatformFailure.raiseException failure
                    finally
                        EventBus.unsubscribe recorder.Consumer eventBus
                        currentRecorder <- None
        }
    // ─── Activation ───

    /// Resolve this session's per-session observability and feedback stores from the grain
    /// key ("userId/sessionId") so each session writes them under its own sessions/<key>/.
    override this.OnActivateAsync(cancellationToken: System.Threading.CancellationToken) : Task =
        GrainStateVersion.prepare
            GrainStateVersion.SessionCurrent
            "Session state"
            persistentState.RecordExists
            persistentState.State.SchemaVersion
            (fun version -> persistentState.State.SchemaVersion <- version)

        let key = this.GetPrimaryKeyString()
        feedback <- feedbackFactory.Invoke key
        base.OnActivateAsync(cancellationToken)

    // ─── Interface implementation ───

    interface ISessionGrain with
        member this.StartAsync(options: SessionStartOptions) : Task<bool> =
            task {
                let (userId, sessionId) = parseKey (this.GetPrimaryKeyString())

                let workspaceKey =
                    if String.IsNullOrEmpty(options.WorkspaceKey) then
                        "default"
                    else
                        options.WorkspaceKey

                let scope =
                    SessionAuthorization.tryCreateScope
                        (principalAccessor.Invoke())
                        userId
                        sessionId
                        options.GroupId
                        workspaceKey

                match scope with
                | None -> return false
                | Some scope when
                    persistentState.RecordExists
                    && not (SessionAuthorization.matchesPersisted persistentState.State.Info scope)
                    ->
                    return false
                | Some scope ->
                    match registry.TryGet(AuthorizationScope.workspaceId scope) with
                    | None -> return false
                    | Some workspace ->

                        let agentName = options.AgentName

                        if not (agentExists workspace agentName) then
                            return false
                        else
                            let info = persistentState.State.Info
                            info.AgentName <- agentName
                            info.TenantId <- AuthorizationScope.tenantId scope |> TenantId.value
                            info.SessionId <- AuthorizationScope.sessionId scope |> Option.get |> SessionId.value
                            info.UserId <- AuthorizationScope.userId scope |> UserId.value

                            info.GroupId <-
                                AuthorizationScope.groupId scope
                                |> Option.map GroupId.value
                                |> Option.defaultValue ""

                            info.WorkspaceKey <- AuthorizationScope.workspaceId scope |> WorkspaceId.value
                            info.IsActive <- true
                            info.ToolNames <- ResizeArray(options.ToolNames)
                            info.RuntimeMode <- options.RuntimeMode
                            info.ActiveConversation <- "default"

                            if info.CreatedAt = DateTimeOffset.MinValue then
                                info.CreatedAt <- DateTimeOffset.UtcNow

                            info.LastActiveAt <- DateTimeOffset.UtcNow

                            // Ensure default conversation exists
                            getOrCreateConversation "default" |> ignore

                            do! persistentState.WriteStateAsync()
                            return true
            }

        member _.ProcessAsync(input: string, cancellationToken: CancellationToken) : Task<string> =
            processCoreAsync input input [||] cancellationToken

        member _.ProcessWithContextAsync
            (llmInput: string, displayText: string, attachmentNames: string[], cancellationToken: CancellationToken)
            : Task<string> =
            processCoreAsync
                llmInput
                displayText
                (if isNull attachmentNames then [||] else attachmentNames)
                cancellationToken

        member _.GetLastTurnIdAsync() : Task<string> =
            requireCurrentScope () |> ignore
            Task.FromResult(persistentState.State.Info.LastTurnId)

        member _.GetLiveStepsAsync() : Task<TurnStepRecord[]> =
            requireCurrentScope () |> ignore

            match currentRecorder with
            | Some recorder -> Task.FromResult(TurnRecorder.steps recorder |> List.map toStepRecord |> List.toArray)
            | None -> Task.FromResult([||])

        member _.SubmitFeedbackAsync(sentiment: string, comment: string) : Task<string array> =
            task {
                requireCurrentScope () |> ignore
                let info = persistentState.State.Info

                if String.IsNullOrEmpty info.LastTurnId then
                    return [||]
                else
                    let parsedSentiment =
                        match (sentiment |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
                        | "positive"
                        | "up"
                        | "good" -> FeedbackSentiment.Positive
                        | "negative"
                        | "down"
                        | "bad" -> FeedbackSentiment.Negative
                        | _ -> FeedbackSentiment.Neutral

                    let fb =
                        { Id = Guid.NewGuid()
                          TurnId = info.LastTurnId
                          SessionId = info.SessionId
                          UserId = info.UserId
                          Sentiment = parsedSentiment
                          Comment =
                            if String.IsNullOrWhiteSpace comment then
                                None
                            else
                                Some comment
                          CreatedAt = DateTimeOffset.UtcNow
                          Metadata = Map.empty }

                    do! feedback.SubmitFeedbackAsync fb
                    return [||]
            }

        member _.SwitchWorkspaceAsync(workspaceKey: string) : Task<bool> =
            task {
                let info = persistentState.State.Info

                let scope = tryCurrentScope workspaceKey

                match scope with
                | None -> return false
                | Some scope ->
                    match registry.TryGet(AuthorizationScope.workspaceId scope) with
                    | None -> return false
                    | Some workspace ->
                        let agentName = persistentState.State.Info.AgentName

                        if not (String.IsNullOrEmpty(agentName)) && not (agentExists workspace agentName) then
                            return false
                        else
                            persistentState.State.Info.WorkspaceKey <-
                                AuthorizationScope.workspaceId scope |> WorkspaceId.value

                            persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                            do! persistentState.WriteStateAsync()
                            return true
            }

        member _.SwitchAgentAsync(agentName: string) : Task<bool> =
            task {
                requireCurrentScope () |> ignore

                match getWorkspace () with
                | None -> return false
                | Some workspace ->
                    if not (agentExists workspace agentName) then
                        return false
                    else
                        persistentState.State.Info.AgentName <- agentName
                        persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                        do! persistentState.WriteStateAsync()
                        return true
            }

        member _.SwitchConversationAsync(conversationName: string) : Task =
            task {
                requireCurrentScope () |> ignore
                persistentState.State.Info.ActiveConversation <- conversationName
                getOrCreateConversation conversationName |> ignore
                persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                do! persistentState.WriteStateAsync()
            }

        member _.ListConversationsAsync() : Task<string array> =
            requireCurrentScope () |> ignore

            persistentState.State.Conversations
            |> Seq.map (fun c -> c.Name)
            |> Seq.toArray
            |> Task.FromResult

        member _.GetInfoAsync() : Task<SessionInfo> =
            requireCurrentScope () |> ignore
            Task.FromResult(persistentState.State.Info)

        member _.GetHistoryAsync() : Task<MessageRecord array> =
            task {
                requireCurrentScope () |> ignore
                do! restoreActiveConversationAsync ()
                return activeConversation().Messages |> Seq.toArray
            }

        member this.ClearHistoryAsync() : Task =
            task {
                requireCurrentScope () |> ignore
                let ctx = activeConversation ()
                ctx.Messages.Clear()
                let grainKey = this.GetPrimaryKeyString()
                let convName = persistentState.State.Info.ActiveConversation
                do! conversationStore.DeleteConversationAsync grainKey convName
                do! persistentState.WriteStateAsync()
            }

        member _.SaveMemoryAsync(key: string, value: string) : Task =
            task {
                requireCurrentScope () |> ignore

                let entry =
                    { Key = key
                      Value = value
                      Timestamp = DateTimeOffset.UtcNow
                      Tags = [] }

                do! memoryStore.SaveAsync (memoryOwner ()) entry
            }

        member _.RecallMemoryAsync(key: string) : Task<MemoryEntry array> =
            task {
                requireCurrentScope () |> ignore
                let! entries = memoryStore.RecallAsync (memoryOwner ()) key
                return entries |> List.toArray
            }

        member _.ClearMemoriesAsync() : Task =
            task {
                requireCurrentScope () |> ignore

                match! memoryStore.DeleteOwnerAsync(memoryOwner ()) with
                | Ok _ -> return ()
                | Error failure -> return PlatformFailure.raiseException failure
            }

        member _.PauseAsync() : Task =
            task {
                requireCurrentScope () |> ignore
                persistentState.State.Info.IsActive <- false
                do! persistentState.WriteStateAsync()
            }

        member _.ResumeAsync() : Task =
            task {
                requireCurrentScope () |> ignore
                persistentState.State.Info.IsActive <- true
                persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                do! persistentState.WriteStateAsync()
            }

        member this.DestroyAsync() : Task =
            task {
                requireCurrentScope () |> ignore
                let grainKey = this.GetPrimaryKeyString()

                try
                    let deletionServices =
                        harnessServicesFactory.Invoke(grainKey, "", CorrelationContext.root ())

                    match!
                        SessionDeletion.executeForSessionAsync
                            grainKey
                            (memoryOwner ())
                            parseKey
                            conversationStore.DeleteSessionAsync
                            feedback.DeleteSessionAsync
                            memoryStore.DeleteOwnerAsync
                            (fun sessionKey ->
                                match deletionServices.Metrics with
                                | Some metrics -> metrics.DeleteOwnerAsync sessionKey
                                | None -> Task.FromResult(Ok 0))
                            (fun sessionKey ->
                                match deletionServices.ExecutionJournal with
                                | Some journal -> journal.DeleteOwnerAsync sessionKey
                                | None -> Task.FromResult(Ok 0))
                            (fun userId sessionId ->
                                grainFactory.GetGrain<ISessionDirectoryGrain>(userId).RemoveAsync sessionId)
                            persistentState.ClearStateAsync
                    with
                    | Ok() -> this.DeactivateOnIdle()
                    | Error failure -> return PlatformFailure.raiseException failure
                with ex ->
                    return
                        PlatformFailure.fromException PlatformFailureBoundary.Host (Some grainKey) ex
                        |> PlatformFailure.raiseException
            }

module SessionGrain =

    /// Build a grain key from userId and sessionId
    let buildKey (userId: string) (sessionId: string) = sprintf "%s/%s" userId sessionId
