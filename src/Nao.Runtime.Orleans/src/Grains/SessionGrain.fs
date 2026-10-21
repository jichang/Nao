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

    [<Id(4u)>]
    member val TenantId: string = "" with get, set

    [<Id(5u)>]
    member val WorkspaceId: string = "" with get, set

    [<Id(6u)>]
    member val UserId: string = "" with get, set

    [<Id(7u)>]
    member val SessionId: string = "" with get, set

/// Persistent state for a session grain
[<GenerateSerializer>]
type SessionGrainState() =
    [<Id(0u)>]
    member val Info: SessionInfo = SessionInfo() with get, set

    [<Id(1u)>]
    member val Conversations: ResizeArray<ConversationContext> = ResizeArray() with get, set

    [<Id(5u)>]
    member val SchemaVersion: int = 0 with get, set

    [<Id(6u)>]
    member val TenantId: string = "" with get, set

    [<Id(7u)>]
    member val WorkspaceId: string = "" with get, set

    [<Id(8u)>]
    member val UserId: string = "" with get, set

    [<Id(9u)>]
    member val SessionId: string = "" with get, set

    [<Id(10u)>]
    member val Turns: ResizeArray<SessionTurnRecord> = ResizeArray() with get, set

    [<Id(11u)>]
    member val NextTurnSequence: int64 = 1L with get, set

    [<Id(12u)>]
    member val Lifecycle: string = SessionLifecyclePhase.Active with get, set

    [<Id(13u)>]
    member val ArchiveAfter: DateTimeOffset = DateTimeOffset.MaxValue with get, set

    [<Id(14u)>]
    member val DeleteAfter: DateTimeOffset = DateTimeOffset.MaxValue with get, set

    [<Id(15u)>]
    member val LegalHoldReason: string = "" with get, set

    [<Id(16u)>]
    member val ArchivedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

/// Options for starting or reconfiguring a session
[<GenerateSerializer>]
type SessionStartOptions() =
    [<Id(0u)>]
    member val AgentName: string = "" with get, set

    [<Id(1u)>]
    member val ToolNames: ResizeArray<string> = ResizeArray() with get, set

    [<Id(3u)>]
    member val GroupId: string = "" with get, set

    /// Runtime policy for launching tools ("" = host default; "docker"/"docker:&lt;image&gt;"
    /// to containerize; a runtime name like "deno" to force it for all tools).
    [<Id(5u)>]
    member val RuntimeMode: string = "" with get, set

module internal SessionAuthorization =
    let tryCreateScope (principal: SecurityPrincipal) address (groupId: string) =
        let parsedGroupId =
            if String.IsNullOrWhiteSpace groupId then
                Some None
            else
                GroupId.tryParse groupId |> Option.map Some

        match parsedGroupId with
        | Some requestedGroupId when
            SecurityPrincipal.tenantId principal = RuntimeAddress.sessionTenant address
            && SecurityPrincipal.userId principal = RuntimeAddress.sessionUser address
            ->
            AuthorizationScope.tryCreate
                principal
                requestedGroupId
                (RuntimeAddress.sessionWorkspace address)
                (Some(RuntimeAddress.sessionId address))
        | _ -> None

    let matchesPersisted (info: SessionInfo) scope =
        let groupMatches =
            AuthorizationScope.groupId scope
            |> Option.map GroupId.value
            |> Option.defaultValue ""
            |> (=) info.GroupId

        info.TenantId = (AuthorizationScope.tenantId scope |> TenantId.value)
        && info.WorkspaceKey = (AuthorizationScope.workspaceId scope |> WorkspaceId.value)
        && info.UserId = (AuthorizationScope.userId scope |> UserId.value)
        && info.SessionId = (AuthorizationScope.sessionId scope
                             |> Option.map SessionId.value
                             |> Option.defaultValue "")
        && groupMatches

/// Orleans grain interface for a user session.
[<GenerateSerializer>]
type OrleansMemoryEntry() =
    [<Id(0u)>]
    member val Key: string = "" with get, set

    [<Id(1u)>]
    member val Value: string = "" with get, set

    [<Id(2u)>]
    member val Timestamp: DateTimeOffset = DateTimeOffset.MinValue with get, set

    [<Id(3u)>]
    member val Tags: ResizeArray<string> = ResizeArray() with get, set

/// Grain key format is produced by RuntimeAddress.sessionKey.
/// The grain resolves agents/tools from the workspace registry.
type ISessionGrain =
    inherit IGrainWithStringKey

    /// Initialize the tenant/workspace-scoped session with a specific agent and optional tool overrides.
    abstract member StartAsync: options: SessionStartOptions -> Task<bool>

    /// Execute or recover one caller-identified logical turn. Reusing the turn id with the
    /// same request returns its durable terminal result; changed input is rejected.
    abstract member ExecuteTurnAsync:
        request: SessionTurnRequest * cancellationToken: CancellationToken -> Task<SessionTurnResult>

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
    abstract member RecallMemoryAsync: key: string -> Task<OrleansMemoryEntry array>

    /// Clear all memories
    abstract member ClearMemoriesAsync: unit -> Task

    /// Pause the session (marks inactive, keeps state)
    abstract member PauseAsync: unit -> Task

    /// Resume a paused session
    abstract member ResumeAsync: unit -> Task

    /// Configure the earliest automatic archival and deletion eligibility timestamps.
    abstract member ConfigureRetentionAsync: archiveAfter: DateTimeOffset * deleteAfter: DateTimeOffset -> Task

    /// Apply time-based lifecycle transitions. Hosts call this from their durable scheduler.
    abstract member EvaluateLifecycleAsync: now: DateTimeOffset -> Task<SessionLifecycleSnapshot>

    /// Archive immediately; archived sessions are immutable and cannot resume.
    abstract member ArchiveAsync: unit -> Task

    /// Place the session under legal hold. The non-blank reason is persisted.
    abstract member SetLegalHoldAsync: reason: string -> Task

    /// Remove an existing legal hold.
    abstract member ClearLegalHoldAsync: unit -> Task

    abstract member GetLifecycleAsync: unit -> Task<SessionLifecycleSnapshot>

    /// Permanently destroy the session and all its data
    abstract member DestroyAsync: unit -> Task

/// Self-contained session grain. Resolves exact workspace versions from WorkspaceCatalog,
/// manages multiple conversation contexts and memory through Orleans persistence.
type SessionGrain
    (
        [<PersistentState("sessionState", "sessionStore")>] persistentState: IPersistentState<SessionGrainState>,
        catalog: WorkspaceCatalog,
        provider: LlmProvider,
        orchestratorFactory: OrchestratorFactory,
        conversationStore: ConversationStore,
        principalAccessor: Func<SecurityPrincipal>,
        permissionBroker: PermissionApprovalBroker,
        permissionGrants: PermissionGrantStore,
        harnessServicesFactory: Func<string, string, CorrelationContext, HarnessServices>,
        feedbackFactory: Func<string, FeedbackService>,
        eventBus: EventBus,
        memoryStore: MemoryStore,
        memoryToolConfig: MemoryToolConfig,
        admissionController: RuntimeAdmissionController,
        grainFactory: IGrainFactory
    ) as this =
    inherit Grain()

    /// The recorder for the turn currently being processed (None when idle). Held so the
    /// reentrant `GetLiveStepsAsync` can surface in-progress steps for live UIs. Reads go
    /// through `recorder.Steps`, which is internally lock-protected.
    let mutable currentRecorder: TurnRecorder option = None
    let mutable activationLease: RuntimeAdmissionLease option = None

    /// Per-session feedback store, resolved once from the grain key in OnActivateAsync so
    /// this session's annotations are written under sessions/<key>/. Observability services
    /// are built per turn (so each signal carries its turn id) in ProcessWithContextAsync.
    let mutable feedback: FeedbackService = Unchecked.defaultof<FeedbackService>

    // ─── Workspace resolution ───

    let getWorkspace () : WorkspaceDefinitions option =
        let info = persistentState.State.Info

        catalog.TryGet(TenantId.parse info.TenantId, WorkspaceVersionId.parse info.WorkspaceKey)
        |> Option.map _.Definitions

    /// Build the identity envelope carried by every emitted event. `actionId` is the turn
    /// the event is about. Producers never decide where data lands — a subscribed storage
    /// strategy routes it (per session, per category, ...) from this scope.
    let makeScope (actionId: string) (correlation: CorrelationContext) : EventScope =
        let info = persistentState.State.Info

        let sessionKey =
            RuntimeAddress.session
                (TenantId.parse info.TenantId)
                (info.WorkspaceKey |> WorkspaceVersionId.parse |> WorkspaceVersionId.workspaceId)
                (UserId.parse info.UserId)
                (SessionId.parse info.SessionId)
            |> RuntimeAddress.sessionKey

        EventScope.Create(
            info.TenantId,
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

        { ToolProtocol = toolProtocol
          ProcessEnvironment = None
          ContainerEnvironment = None
          ExecutionJournal = services.ExecutionJournal
          ReplayFixtures = None
          Lifecycle = []
          Tracer = services.Tracer
          Metrics = services.Metrics
          Resilience = ResilienceConfig.NoResilience
          ReadinessChecks = []
          TraceStore = services.TraceStore
          Judge = None
          Constitution = constitution
          ConstitutionRemediationBroker = ConstitutionRemediationBroker.unavailable
          AuditLog = services.AuditLog
          PolicyEngine = None
          PolicyConfirmationBroker = PolicyConfirmationBroker.unavailable
          Bus = eventBus }

    let currentAddress () =
        RuntimeAddress.tryParseSessionKey (this.GetPrimaryKeyString())
        |> Option.defaultWith (fun () ->
            raise (UnauthorizedAccessException "Invalid tenant/workspace/user/session grain key."))

    let validatePersistedHierarchy (address: RuntimeSessionAddress) =
        let owner = PersistedOwnerScope.session address

        let matches tenantId workspaceId userId sessionId =
            tenantId = owner.TenantId
            && workspaceId = owner.WorkspaceId
            && userId = owner.UserId
            && sessionId = owner.SessionId.Value

        let rootMatches =
            matches
                persistentState.State.TenantId
                persistentState.State.WorkspaceId
                persistentState.State.UserId
                persistentState.State.SessionId

        let hierarchyMatches =
            (persistentState.State.Conversations
             |> Seq.forall (fun context ->
                 matches context.TenantId context.WorkspaceId context.UserId context.SessionId
                 && (context.Messages
                     |> Seq.forall (fun message ->
                         matches message.TenantId message.WorkspaceId message.UserId message.SessionId))))
            && (persistentState.State.Turns
                |> Seq.forall (fun turn -> matches turn.TenantId turn.WorkspaceId turn.UserId turn.SessionId))

        if not rootMatches || not hierarchyMatches then
            raise (UnauthorizedAccessException "Persisted session hierarchy does not match its grain key.")

    let tryCurrentScope () =
        let info = persistentState.State.Info
        let address = currentAddress ()

        if persistentState.RecordExists then
            validatePersistedHierarchy address

        SessionAuthorization.tryCreateScope (principalAccessor.Invoke()) address info.GroupId
        |> Option.filter (SessionAuthorization.matchesPersisted info)

    let requireCurrentScope () =
        match tryCurrentScope () with
        | Some scope -> scope
        | None ->
            PlatformFailure.create
                PlatformErrorCategory.PermissionDenied
                "The authenticated principal is not authorized for this session."
                false
                None
            |> PlatformFailure.raiseException

    let lifecycleSnapshot () =
        SessionLifecycle.snapshot
            persistentState.State.Lifecycle
            persistentState.State.ArchiveAfter
            persistentState.State.DeleteAfter
            persistentState.State.LegalHoldReason
            persistentState.State.ArchivedAt

    let applyLifecycle now =
        if
            persistentState.State.Lifecycle <> SessionLifecyclePhase.Archived
            && now >= persistentState.State.ArchiveAfter
        then
            persistentState.State.Lifecycle <- SessionLifecyclePhase.Archived
            persistentState.State.ArchivedAt <- now
            persistentState.State.Info.IsActive <- false
            true
        else
            false

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
            let info = persistentState.State.Info
            ctx.AgentName <- info.AgentName
            ctx.TenantId <- info.TenantId
            ctx.WorkspaceId <- info.WorkspaceKey
            ctx.UserId <- info.UserId
            ctx.SessionId <- info.SessionId
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
        let info = persistentState.State.Info
        record.TenantId <- info.TenantId
        record.WorkspaceId <- info.WorkspaceKey
        record.UserId <- info.UserId
        record.SessionId <- info.SessionId
        record

    let restoreActiveConversationAsync () =
        task {
            let ctx = activeConversation ()
            let authorization = requireCurrentScope ()
            let sessionId = persistentState.State.Info.SessionId
            let convName = persistentState.State.Info.ActiveConversation

            if not (String.IsNullOrEmpty sessionId) then
                let! loaded = conversationStore.LoadAsync authorization convName
                ctx.Messages.Clear()

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
            let authorization = requireCurrentScope ()
            let ctx = activeConversation ()

            let existing =
                ctx.Messages
                |> Seq.filter (fun message -> message.TurnId = turnId)
                |> Seq.toArray

            if existing.Length > 0 then
                if
                    existing.Length <> 2
                    || existing.[0].Role <> "User"
                    || existing.[0].Content <> userInput
                    || existing.[1].Role <> "Assistant"
                    || existing.[1].Content <> response
                then
                    raise (
                        InvalidDataException
                            "A different transcript entry already exists for the durable turn identity."
                    )
            else
                let stepRecords = steps |> List.map toStepRecord

                let artifactRecords =
                    artifacts
                    |> List.map (fun value ->
                        let lineage = Artifact.lineage value
                        let id = value |> Artifact.id |> ArtifactId.serialize
                        let idempotencyKey = value |> Artifact.idempotencyKey |> IdempotencyKey.value
                        let schemaVersion = value |> Artifact.schemaVersion |> ArtifactSchemaVersion.value
                        let role = value |> Artifact.role |> ArtifactRole.serialize
                        let address = value |> Artifact.address |> ArtifactAddress.value
                        let contentHash = value |> Artifact.contentHash |> ArtifactContentHash.value
                        let recordedBy = lineage |> ArtifactLineage.recordedBy |> ExecutionId.serialize

                        let parentArtifactIds =
                            lineage
                            |> ArtifactLineage.parentArtifactIds
                            |> List.map ArtifactId.serialize
                            |> List.toArray

                        ArtifactRecord(
                            Id = id,
                            IdempotencyKey = idempotencyKey,
                            SchemaVersion = schemaVersion,
                            Role = role,
                            Address = address,
                            Kind = Artifact.kind value,
                            ContentType = Artifact.contentType value,
                            Payload = Artifact.payload value,
                            ContentHash = contentHash,
                            RecordedBy = recordedBy,
                            RecordedAt = ArtifactLineage.recordedAt lineage,
                            ParentArtifactIds = parentArtifactIds
                        ))

                let info = persistentState.State.Info

                let userRecord =
                    MessageRecord(
                        Role = "User",
                        Content = userInput,
                        TurnId = turnId,
                        Correlation = correlation,
                        Attachments = ResizeArray(attachmentNames),
                        TenantId = info.TenantId,
                        WorkspaceId = info.WorkspaceKey,
                        UserId = info.UserId,
                        SessionId = info.SessionId
                    )

                let assistantRecord =
                    MessageRecord(
                        Role = "Assistant",
                        Content = response,
                        TurnId = turnId,
                        Correlation = correlation,
                        Steps = ResizeArray(stepRecords),
                        Artifacts = ResizeArray(artifactRecords),
                        TenantId = info.TenantId,
                        WorkspaceId = info.WorkspaceKey,
                        UserId = info.UserId,
                        SessionId = info.SessionId
                    )

                ctx.Messages.Add userRecord
                ctx.Messages.Add assistantRecord

                let sessionId = persistentState.State.Info.SessionId
                let convName = persistentState.State.Info.ActiveConversation

                if not (String.IsNullOrEmpty sessionId) then
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

                    do! conversationStore.AppendAsync authorization convName persisted

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

                        do! conversationStore.AppendAsync authorization childName childMsgs
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

    let addMemoryAgentTool (tools: Tool list) =
        let existingNames = tools |> List.map _.Name |> Set.ofList
        let memoryTools = MemoryTools.create memoryToolConfig memoryStore None

        if memoryTools.IsEmpty || existingNames.Contains "memory" then
            tools
        else
            let memoryAgent = MemoryAgent.create orchestratorFactory provider memoryTools
            MemoryAgent.asTool memoryAgent :: tools

    let createAgentAsync (workspace: WorkspaceDefinitions) (name: string) : Task<Agent option> =
        findBuiltAgent workspace name |> Task.FromResult

    let processCoreAsync
        (request: SessionTurnRequest)
        (cancellationToken: CancellationToken)
        : Task<SessionTurnResult> =
        SessionTurnLedger.validateRequest request
        let authorization = requireCurrentScope ()
        let info = persistentState.State.Info
        let agentName = info.AgentName
        let correlation = OrleansCorrelationContext.toDomain request.Correlation
        let turnId = request.TurnId

        if String.IsNullOrEmpty(agentName) then
            invalidOp "Session not started. Call StartAsync first."

        if persistentState.State.Lifecycle <> SessionLifecyclePhase.Active then
            invalidOp "Only an active session can execute a turn."

        let workspace =
            getWorkspace ()
            |> Option.defaultWith (fun () -> invalidOp (sprintf "Workspace '%s' not available" info.WorkspaceKey))

        let requestHash =
            SessionTurnLedger.requestHash info.ActiveConversation agentName request

        let existingTurn =
            persistentState.State.Turns |> Seq.tryFind (fun turn -> turn.TurnId = turnId)

        existingTurn
        |> Option.iter (fun turn ->
            if turn.RequestHash <> requestHash then
                invalidArg (nameof request) "The turn identity is already bound to a different request.")

        match existingTurn with
        | Some turn when SessionTurnPhase.isTerminal turn.Phase -> Task.FromResult(SessionTurnLedger.toResult true turn)
        | _ ->
            task {
                do! restoreActiveConversationAsync ()

                let turn, isNew =
                    match existingTurn with
                    | Some value -> value, false
                    | None ->
                        let owner = PersistedOwnerScope.fromAuthorization authorization

                        let value =
                            SessionTurnRecord(
                                TenantId = owner.TenantId,
                                WorkspaceId = owner.WorkspaceId,
                                UserId = owner.UserId,
                                SessionId = owner.SessionId.Value,
                                TurnId = turnId,
                                IdempotencyKey =
                                    (IdempotencyKey.turn authorization info.ActiveConversation (TurnId.parse turnId)
                                     |> IdempotencyKey.value),
                                RequestHash = requestHash,
                                ConversationName = info.ActiveConversation,
                                AgentName = agentName,
                                DisplayText = request.DisplayText,
                                AttachmentNames = Array.copy request.AttachmentNames,
                                Correlation = correlation,
                                Phase = SessionTurnPhase.Accepted,
                                AcceptedAt = DateTimeOffset.UtcNow,
                                Sequence = persistentState.State.NextTurnSequence
                            )

                        persistentState.State.NextTurnSequence <- persistentState.State.NextTurnSequence + 1L

                        persistentState.State.Turns.Add value
                        value, true

                if isNew then
                    do! persistentState.WriteStateAsync()

                let tools =
                    resolveTools workspace (persistentState.State.Info.ToolNames |> Seq.toList)

                let executionTenant = TenantId.parse info.TenantId

                do!
                    RuntimeAdministrationDisablement.requireEnabledAsync
                        grainFactory
                        executionTenant
                        AdministrativeResourceKind.Tenant
                        info.TenantId

                do!
                    RuntimeAdministrationDisablement.requireEnabledAsync
                        grainFactory
                        executionTenant
                        AdministrativeResourceKind.Agent
                        agentName

                for tool in tools do
                    do!
                        RuntimeAdministrationDisablement.requireEnabledAsync
                            grainFactory
                            executionTenant
                            AdministrativeResourceKind.Tool
                            tool.Name

                // The persisted display text remains the user's latest message; this expanded
                // input is only what the agent sees for resolving follow-ups.
                let contextualInput =
                    ConversationContextRender.withHistory 8 (activeConversation().Messages) request.LlmInput

                let turnScope = makeScope turnId correlation
                // The recorder must exist before the agent is built so it can subscribe to the
                // event bus and capture this turn's whole execution (rounds, tool calls,
                // delegations) — not just the harness-level final answer. It filters bus events
                // by `scope.ActionId = turnId`, so a shared bus never cross-talks between turns.
                let recorder = TurnRecorder.create (turnId, correlation, agentName, contextualInput)
                // Flow the session/turn identity into tools so file operations and permission
                // requests can attribute their output to this session.
                let sessionKey = RuntimeAddress.sessionKey (currentAddress ())

                let policyVersions =
                    Map.ofList [ PermissionPolicy.identity, PermissionPolicy.currentVersion ]

                let permissionDecisions = ResizeArray<PermissionDecisionEvidence>()
                let harnessServices = harnessServicesFactory.Invoke(sessionKey, turnId, correlation)

                let executionJournal =
                    harnessServices.ExecutionJournal
                    |> Option.defaultWith (fun () -> invalidOp "Durable session turns require an execution journal.")

                let loadMemoryReferencesAsync executionId =
                    task {
                        let! records = executionJournal.GetByExecutionAsync authorization executionId

                        return
                            records
                            |> List.filter (fun record -> record.ToolName = "memory_remember")
                            |> List.map (_.IdempotencyKey >> IdempotencyKey.value)
                            |> List.distinct
                            |> List.toArray
                    }

                let! durableOutcome =
                    executionJournal.Outcomes.TryGetByIdempotencyKey
                        authorization
                        (IdempotencyKey.parse turn.IdempotencyKey)

                let recoverOutcomeAsync (outcome: HarnessTerminalOutcome) =
                    task {
                        if outcome.Phase = HarnessCheckpointPhase.Succeeded then
                            let response =
                                outcome.Response
                                |> Option.defaultWith (fun () ->
                                    raise (
                                        InvalidDataException
                                            "A successful durable harness outcome is missing its response."
                                    ))

                            do!
                                appendTurnAsync
                                    turn.DisplayText
                                    turn.AttachmentNames
                                    response
                                    turnId
                                    correlation
                                    []
                                    outcome.Artifacts

                            turn.Phase <- SessionTurnPhase.Succeeded
                            turn.Response <- response
                            turn.CompletedAt <- outcome.RecordedAt

                            turn.ArtifactIds <-
                                outcome.Artifacts
                                |> List.map (Artifact.id >> ArtifactId.serialize)
                                |> List.toArray

                            turn.TraceExecutionIds <- [| outcome.Correlation.ExecutionId |> ExecutionId.serialize |]

                            let! memoryReferences = loadMemoryReferencesAsync outcome.Correlation.ExecutionId

                            turn.MemoryKeys <- memoryReferences
                            info.LastTurnId <- turnId
                            info.LastActiveAt <- DateTimeOffset.UtcNow
                        else
                            turn.Phase <- SessionTurnPhase.Failed
                            turn.Failure <- outcome.Failure |> Option.defaultValue "Execution failed."
                            turn.CompletedAt <- outcome.RecordedAt

                            turn.TraceExecutionIds <- [| outcome.Correlation.ExecutionId |> ExecutionId.serialize |]

                            let! memoryReferences = loadMemoryReferencesAsync outcome.Correlation.ExecutionId

                            turn.MemoryKeys <- memoryReferences

                        do! persistentState.WriteStateAsync()
                        return SessionTurnLedger.toResult true turn
                    }

                match durableOutcome with
                | Some outcome ->
                    let! result = recoverOutcomeAsync outcome
                    raise (SessionTurnRecovered result)
                | None -> ()

                let! admission =
                    RuntimeAdmissionController.acquireAsync
                        RuntimeAdmissionClass.TurnExecution
                        info.TenantId
                        cancellationToken
                        admissionController

                use executionLease =
                    match admission with
                    | Ok lease -> lease :> IDisposable
                    | Error failure ->
                        failure
                        |> RuntimeAdmissionFailure.toPlatformFailure
                        |> PlatformFailureException
                        |> raise

                turn.Phase <- SessionTurnPhase.Running
                turn.StartedAt <- DateTimeOffset.UtcNow
                do! persistentState.WriteStateAsync()

                let grantRules (grants: PermissionGrant list) : PermissionRule list =
                    grants
                    |> List.map (fun grant ->
                        { Id = grant.Id.ToString("N")
                          AppliesTo = grant.Target
                          Decision = PermissionDecision.Allow
                          Scope = RuleScope.Session sessionKey
                          CreatedAt = grant.GrantedAt })

                let resolvePermission (access: ResourceAccess) (reason: string) (forceConfirm: bool) : Task<bool> =
                    task {
                        let requestedAt = DateTimeOffset.UtcNow
                        let! activeGrants = permissionGrants.GetActiveAsync authorization requestedAt
                        let applicable = ResourcePermission.applicable sessionKey (grantRules activeGrants)
                        let target = ResourcePermission.targetForAccess access

                        let matchingGrants =
                            activeGrants
                            |> List.filter (fun grant -> ResourcePermission.targetCovers grant.Target target)

                        let evidence decision basis grants =
                            { Id = Guid.NewGuid()
                              Access = access
                              Decision = decision
                              Basis = basis
                              PolicyVersions = policyVersions
                              Grants =
                                grants
                                |> List.map (fun (grant: PermissionGrant) ->
                                    { Id = grant.Id
                                      Version = grant.Version })
                              EvaluatedAt = requestedAt }

                        let recordEvidence evidence =
                            task {
                                match harnessServices.AuditLog with
                                | Some audit ->
                                    do!
                                        audit.RecordAsync
                                            authorization
                                            (AuditLog.permissionDecision
                                                agentName
                                                evidence
                                                (Some correlation.ExecutionId))

                                    permissionDecisions.Add evidence

                                    turn.PermissionGrantIds <-
                                        Seq.append
                                            turn.PermissionGrantIds
                                            (evidence.Grants |> Seq.map (fun grant -> grant.Id.ToString("N")))
                                        |> Seq.distinct
                                        |> Seq.toArray

                                    do! persistentState.WriteStateAsync()
                                | None ->
                                    return
                                        raise (
                                            GovernedExecutionException
                                                "Permission decisions require a durable authenticated audit log."
                                        )
                            }

                        if
                            not forceConfirm
                            && ResourcePermission.evaluateWith PermissionDecision.Deny applicable access = PermissionDecision.Allow
                        then
                            do!
                                recordEvidence (
                                    evidence
                                        PermissionDecision.Allow
                                        PermissionDecisionBasis.ReusableGrant
                                        matchingGrants
                                )

                            return true
                        else
                            let permissionRequest =
                                { Authorization = authorization
                                  Correlation = correlation
                                  Access = access
                                  Reason = reason
                                  ForceConfirmation = forceConfirm
                                  RequestedAt = requestedAt
                                  AllowedGrantScopes =
                                    Set.ofList
                                        [ PermissionGrantScope.Operation
                                          PermissionGrantScope.Session
                                          PermissionGrantScope.Workspace
                                          PermissionGrantScope.Durable ] }

                            let! approval = PermissionGate.resolve permissionBroker permissionRequest cancellationToken

                            match approval with
                            | PermissionApproval.Approved grant when grant.Scope <> PermissionGrantScope.Operation ->
                                let permissionGrant =
                                    PermissionGrant.create grant.Scope target requestedAt grant.ExpiresAt

                                do! permissionGrants.SaveAsync authorization permissionGrant

                                do!
                                    recordEvidence (
                                        evidence
                                            PermissionDecision.Allow
                                            (PermissionDecisionBasis.HostApproval grant.Scope)
                                            [ permissionGrant ]
                                    )

                                return true
                            | PermissionApproval.Approved grant ->
                                do!
                                    recordEvidence (
                                        evidence
                                            PermissionDecision.Allow
                                            (PermissionDecisionBasis.HostApproval grant.Scope)
                                            []
                                    )

                                return true
                            | PermissionApproval.Denied ->
                                do!
                                    recordEvidence (
                                        evidence PermissionDecision.Deny PermissionDecisionBasis.HostDenial []
                                    )

                                return false
                    }

                let artifacts = ResizeArray<Artifact>()
                let grantedResources = ResizeArray<ResourceAccess>()
                let permissionLock = new SemaphoreSlim(1, 1)

                let requestPermission access reason forceConfirm =
                    task {
                        do! permissionLock.WaitAsync()

                        try
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
                      NextToolCallKey = (fun () -> invalidOp "Tool keys are allocated by the execution harness.")
                      NextArtifactKey = (fun () -> invalidOp "Artifact keys are allocated by the execution harness.")
                      GetArtifacts = (fun () -> lock artifacts (fun () -> List.ofSeq artifacts))
                      GetGrantedResources = (fun () -> lock grantedResources (fun () -> List.ofSeq grantedResources))
                      RequestPermission = requestPermission
                      RequestModelEligibility = (fun _ -> Task.FromResult false)
                      PublishArtifact = publishArtifact
                      Authorization = None }

                currentRecorder <- Some recorder

                try
                    // Subscribe the recorder for this turn's progress signals; detached in finally.
                    EventBus.subscribe recorder.Consumer eventBus
                    let! agentOpt = createAgentAsync workspace agentName

                    match agentOpt with
                    | None ->
                        return
                            invalidOp (
                                sprintf
                                    "Agent '%s' not found in workspace '%s'"
                                    agentName
                                    persistentState.State.Info.WorkspaceKey
                            )
                    | Some agent ->
                        let harnessConfig = buildHarnessConfig workspace tools harnessServices

                        let request =
                            ExecutionRequest.create
                                authorization
                                (TurnId.parse turnId)
                                info.ActiveConversation
                                agent.Metadata.Id
                                contextualInput
                                SandboxConfig.Default
                                policyVersions
                                Map.empty
                                correlation

                        let! result = EtclovgHarness.runAsync harnessConfig agentContext agent request cancellationToken

                        match result.Status, result.Outputs.Response with
                        | ExecutionTerminalStatus.Succeeded, Some response ->
                            // Emit the completed turn; a subscribed storage strategy persists it
                            // so feedback can later be analysed against it.
                            let turnRecord =
                                { TurnRecorder.snapshot recorder with
                                    Output = response
                                    PermissionDecisions = List.ofSeq permissionDecisions
                                    PolicyEvaluations = result.PolicyDecisions.PolicyEvaluations }

                            do! EventBus.publishAsync (TurnCompleted(makeScope turnId correlation, turnRecord)) eventBus

                            // Persist a CLEAN, user-facing transcript: the display text (no embedded
                            // attachment content) plus one assistant message carrying the process.
                            do!
                                appendTurnAsync
                                    turn.DisplayText
                                    turn.AttachmentNames
                                    response
                                    turnId
                                    correlation
                                    (TurnRecorder.steps recorder)
                                    (TurnRecorder.artifacts recorder)

                            info.LastTurnId <- turnId
                            info.LastActiveAt <- DateTimeOffset.UtcNow
                            turn.Phase <- SessionTurnPhase.Succeeded
                            turn.Response <- response
                            turn.CompletedAt <- DateTimeOffset.UtcNow

                            turn.PermissionGrantIds <-
                                permissionDecisions
                                |> Seq.collect _.Grants
                                |> Seq.map (fun grant -> grant.Id.ToString("N"))
                                |> Seq.distinct
                                |> Seq.toArray

                            turn.ArtifactIds <-
                                result.Outputs.Artifacts
                                |> List.map (Artifact.id >> ArtifactId.serialize)
                                |> List.toArray

                            turn.TraceExecutionIds <- [| result.Correlation.ExecutionId |> ExecutionId.serialize |]

                            let! memoryReferences = loadMemoryReferencesAsync result.Correlation.ExecutionId

                            turn.MemoryKeys <- memoryReferences

                            do! persistentState.WriteStateAsync()
                            return SessionTurnLedger.toResult false turn

                        | _ ->
                            let failure = result.Status.ToPlatformFailure(Some turnId)
                            turn.Phase <- SessionTurnPhase.Failed
                            turn.Failure <- failure.Message
                            turn.CompletedAt <- DateTimeOffset.UtcNow
                            turn.TraceExecutionIds <- [| result.Correlation.ExecutionId |> ExecutionId.serialize |]

                            let! memoryReferences = loadMemoryReferencesAsync result.Correlation.ExecutionId

                            turn.MemoryKeys <- memoryReferences

                            do! persistentState.WriteStateAsync()
                            return SessionTurnLedger.toResult false turn
                finally
                    EventBus.unsubscribe recorder.Consumer eventBus
                    currentRecorder <- None
            }
    // ─── Activation ───

    /// Resolve this session's per-session services from its tenant/workspace/user/session key.
    override this.OnActivateAsync(cancellationToken: System.Threading.CancellationToken) : Task =
        GrainStateVersion.prepare
            GrainStateVersion.SessionCurrent
            "Session state"
            persistentState.RecordExists
            persistentState.State.SchemaVersion
            (fun version -> persistentState.State.SchemaVersion <- version)

        let key = this.GetPrimaryKeyString()
        let address = currentAddress ()

        if persistentState.RecordExists then
            validatePersistedHierarchy address

        feedback <- feedbackFactory.Invoke key
        let baseActivation = base.OnActivateAsync(cancellationToken)

        task {
            let mutable completed = false

            try
                let! admission =
                    RuntimeAdmissionController.acquireAsync
                        RuntimeAdmissionClass.SessionActivation
                        (address |> RuntimeAddress.sessionTenant |> TenantId.value)
                        cancellationToken
                        admissionController

                activationLease <-
                    match admission with
                    | Ok lease -> Some lease
                    | Error failure ->
                        failure
                        |> RuntimeAdmissionFailure.toPlatformFailure
                        |> PlatformFailureException
                        |> raise

                let mutable stateChanged = applyLifecycle DateTimeOffset.UtcNow

                if SessionTurnLedger.recoverInterrupted persistentState.State.Turns then
                    stateChanged <- true

                if stateChanged then
                    do! persistentState.WriteStateAsync()

                do! baseActivation
                completed <- true
            finally
                if not completed then
                    activationLease |> Option.iter (fun lease -> (lease :> IDisposable).Dispose())

                    activationLease <- None
        }

    override _.OnDeactivateAsync(reason, cancellationToken) =
        activationLease |> Option.iter (fun lease -> (lease :> IDisposable).Dispose())
        activationLease <- None
        base.OnDeactivateAsync(reason, cancellationToken)

    // ─── Interface implementation ───

    interface ISessionGrain with
        member this.StartAsync(options: SessionStartOptions) : Task<bool> =
            task {
                let address = currentAddress ()

                let scope =
                    SessionAuthorization.tryCreateScope (principalAccessor.Invoke()) address options.GroupId

                match scope with
                | None -> return false
                | Some scope when
                    persistentState.RecordExists
                    && not (SessionAuthorization.matchesPersisted persistentState.State.Info scope)
                    ->
                    return false
                | Some _ when
                    persistentState.RecordExists
                    && persistentState.State.Lifecycle = SessionLifecyclePhase.Archived
                    ->
                    return false
                | Some scope ->
                    let workspaceIdentity =
                        AuthorizationScope.workspaceId scope
                        |> WorkspaceId.value
                        |> WorkspaceVersionId.parse

                    match catalog.TryGet(AuthorizationScope.tenantId scope, workspaceIdentity) with
                    | None -> return false
                    | Some registration ->
                        let workspace = registration.Definitions

                        let! groupAuthorized =
                            match AuthorizationScope.groupId scope with
                            | None -> Task.FromResult true
                            | Some groupId ->
                                task {
                                    let groupKey =
                                        RuntimeAddress.group (AuthorizationScope.tenantId scope) groupId
                                        |> RuntimeAddress.groupKey

                                    let! groupState = grainFactory.GetGrain<IGroupDirectoryGrain>(groupKey).GetAsync()

                                    let expectedTenantId = AuthorizationScope.tenantId scope |> TenantId.value

                                    let expectedUserId = AuthorizationScope.userId scope |> UserId.value

                                    let memberExists =
                                        groupState.Members
                                        |> Seq.exists (fun memberRecord ->
                                            memberRecord.TenantId = expectedTenantId
                                            && memberRecord.GroupId = GroupId.value groupId
                                            && memberRecord.UserId = expectedUserId)

                                    return
                                        groupState.Lifecycle = GroupLifecycle.serialize GroupLifecycle.Active
                                        && memberExists
                                }

                        if not groupAuthorized then
                            return false
                        else

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
                                persistentState.State.Lifecycle <- SessionLifecyclePhase.Active
                                info.ToolNames <- ResizeArray(options.ToolNames)
                                info.RuntimeMode <- options.RuntimeMode
                                info.ActiveConversation <- "default"

                                if info.CreatedAt = DateTimeOffset.MinValue then
                                    info.CreatedAt <- DateTimeOffset.UtcNow

                                info.LastActiveAt <- DateTimeOffset.UtcNow
                                persistentState.State.TenantId <- info.TenantId
                                persistentState.State.WorkspaceId <- info.WorkspaceKey
                                persistentState.State.UserId <- info.UserId
                                persistentState.State.SessionId <- info.SessionId

                                getOrCreateConversation "default" |> ignore

                                do! persistentState.WriteStateAsync()
                                let entry = SessionDirectoryEntry()
                                entry.TenantId <- info.TenantId
                                entry.WorkspaceId <- info.WorkspaceKey
                                entry.UserId <- info.UserId
                                entry.SessionId <- info.SessionId
                                entry.AgentName <- info.AgentName
                                entry.CreatedAt <- info.CreatedAt
                                entry.LastActiveAt <- info.LastActiveAt
                                entry.IsActive <- true

                                let directoryKey =
                                    RuntimeAddress.directoryFromScope scope |> RuntimeAddress.directoryKey

                                do! grainFactory.GetGrain<ISessionDirectoryGrain>(directoryKey).RegisterAsync entry

                                return true
            }

        member _.ExecuteTurnAsync(request: SessionTurnRequest, cancellationToken: CancellationToken) =
            task {
                try
                    return! processCoreAsync request cancellationToken
                with
                | SessionTurnRecovered result -> return result
                | error ->
                    match
                        persistentState.State.Turns
                        |> Seq.tryFind (fun turn ->
                            turn.TurnId = request.TurnId && turn.Phase = SessionTurnPhase.Running)
                    with
                    | Some turn ->
                        turn.Phase <- SessionTurnPhase.Interrupted
                        do! persistentState.WriteStateAsync()
                    | None -> ()

                    return raise error
            }

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
                          Sentiment = parsedSentiment
                          Comment =
                            if String.IsNullOrWhiteSpace comment then
                                None
                            else
                                Some comment
                          CreatedAt = DateTimeOffset.UtcNow
                          Metadata = Map.empty }

                    do! feedback.SubmitFeedbackAsync (requireCurrentScope ()) fb
                    return [||]
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
                let authorization = requireCurrentScope ()
                let ctx = activeConversation ()
                ctx.Messages.Clear()
                let convName = persistentState.State.Info.ActiveConversation
                do! conversationStore.DeleteConversationAsync authorization convName
                do! persistentState.WriteStateAsync()
            }

        member _.SaveMemoryAsync(key: string, value: string) : Task =
            task {
                let authorization = requireCurrentScope ()

                let entry =
                    { Key = key
                      Value = value
                      Timestamp = DateTimeOffset.UtcNow
                      Tags = [] }

                do! memoryStore.SaveAsync authorization entry
            }

        member _.RecallMemoryAsync(key: string) : Task<OrleansMemoryEntry array> =
            task {
                let authorization = requireCurrentScope ()
                let! entries = memoryStore.RecallAsync authorization key

                return
                    entries
                    |> List.map (fun entry ->
                        OrleansMemoryEntry(
                            Key = entry.Key,
                            Value = entry.Value,
                            Timestamp = entry.Timestamp,
                            Tags = ResizeArray(entry.Tags)
                        ))
                    |> List.toArray
            }

        member _.ClearMemoriesAsync() : Task =
            task {
                let authorization = requireCurrentScope ()

                match! memoryStore.DeleteOwnerAsync authorization with
                | Ok _ -> return ()
                | Error failure -> return PlatformFailure.raiseException failure
            }

        member _.PauseAsync() : Task =
            task {
                requireCurrentScope () |> ignore

                if persistentState.State.Lifecycle = SessionLifecyclePhase.Archived then
                    invalidOp "An archived session cannot be paused."

                persistentState.State.Lifecycle <- SessionLifecyclePhase.Paused
                persistentState.State.Info.IsActive <- false
                do! persistentState.WriteStateAsync()
            }

        member _.ResumeAsync() : Task =
            task {
                requireCurrentScope () |> ignore

                if persistentState.State.Lifecycle = SessionLifecyclePhase.Archived then
                    invalidOp "An archived session cannot resume."

                persistentState.State.Lifecycle <- SessionLifecyclePhase.Active
                persistentState.State.Info.IsActive <- true
                persistentState.State.Info.LastActiveAt <- DateTimeOffset.UtcNow
                do! persistentState.WriteStateAsync()
            }

        member _.ConfigureRetentionAsync(archiveAfter, deleteAfter) : Task =
            task {
                requireCurrentScope () |> ignore
                SessionLifecycle.validateSchedule archiveAfter deleteAfter
                persistentState.State.ArchiveAfter <- archiveAfter
                persistentState.State.DeleteAfter <- deleteAfter
                let changed = applyLifecycle DateTimeOffset.UtcNow

                if changed || persistentState.RecordExists then
                    do! persistentState.WriteStateAsync()
            }

        member _.EvaluateLifecycleAsync(now) : Task<SessionLifecycleSnapshot> =
            task {
                requireCurrentScope () |> ignore

                if applyLifecycle now then
                    do! persistentState.WriteStateAsync()

                return lifecycleSnapshot ()
            }

        member _.ArchiveAsync() : Task =
            task {
                requireCurrentScope () |> ignore

                if persistentState.State.Lifecycle <> SessionLifecyclePhase.Archived then
                    persistentState.State.Lifecycle <- SessionLifecyclePhase.Archived
                    persistentState.State.ArchivedAt <- DateTimeOffset.UtcNow
                    persistentState.State.Info.IsActive <- false
                    do! persistentState.WriteStateAsync()
            }

        member _.SetLegalHoldAsync(reason) : Task =
            task {
                requireCurrentScope () |> ignore

                if String.IsNullOrWhiteSpace reason then
                    invalidArg (nameof reason) "A legal hold requires a non-blank reason."

                persistentState.State.LegalHoldReason <- SecretRedaction.redact reason
                do! persistentState.WriteStateAsync()
            }

        member _.ClearLegalHoldAsync() : Task =
            task {
                requireCurrentScope () |> ignore
                persistentState.State.LegalHoldReason <- ""
                do! persistentState.WriteStateAsync()
            }

        member _.GetLifecycleAsync() : Task<SessionLifecycleSnapshot> =
            requireCurrentScope () |> ignore
            Task.FromResult(lifecycleSnapshot ())

        member this.DestroyAsync() : Task =
            task {
                let authorization = requireCurrentScope ()
                let grainKey = this.GetPrimaryKeyString()
                let now = DateTimeOffset.UtcNow

                if
                    not (
                        SessionLifecycle.deletionAllowed
                            now
                            persistentState.State.DeleteAfter
                            persistentState.State.LegalHoldReason
                    )
                then
                    invalidOp "Session deletion is blocked by retention policy or legal hold."

                try
                    do! permissionGrants.DeleteScopeAsync authorization PermissionGrantScope.Session :> Task

                    let deletionServices =
                        harnessServicesFactory.Invoke(grainKey, "", CorrelationContext.root ())

                    match!
                        SessionDeletion.executeForSessionAsync
                            grainKey
                            authorization
                            conversationStore.DeleteScopeAsync
                            feedback.DeleteScopeAsync
                            memoryStore.DeleteOwnerAsync
                            (fun scope ->
                                match deletionServices.Metrics with
                                | Some metrics -> metrics.DeleteScopeAsync scope
                                | None -> Task.FromResult(Ok 0))
                            (fun scope ->
                                match deletionServices.TraceStore with
                                | Some traces -> traces.DeleteScopeAsync scope
                                | None -> Task.FromResult(Ok 0))
                            (fun scope ->
                                match deletionServices.ExecutionJournal with
                                | Some journal -> journal.DeleteScopeAsync scope
                                | None -> Task.FromResult(Ok 0))
                            (fun directoryKey sessionId ->
                                grainFactory.GetGrain<ISessionDirectoryGrain>(directoryKey).RemoveAsync sessionId)
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

    let buildKey tenantId workspaceId userId sessionId =
        RuntimeAddress.session tenantId workspaceId userId sessionId
        |> RuntimeAddress.sessionKey
