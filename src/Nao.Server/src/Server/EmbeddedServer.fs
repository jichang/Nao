namespace Nao.Assistant

open System
open System.IO
open System.Net.WebSockets
open System.Net.Sockets
open System.Data.Common
open System.Text
open System.Text.RegularExpressions
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Nao.Agents
open Nao.Agents
open Nao.Loader
open Nao.Providers
open Nao.Persistence
open Nao.Agents
open Nao.Agents
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

module EmbeddedServer =

    let private waitForListener (port: int) (timeout: TimeSpan) =
        let deadline = DateTime.UtcNow + timeout
        let mutable started = false
        let mutable lastError = "no connection attempt made"

        while not started && DateTime.UtcNow < deadline do
            try
                use client = new TcpClient()
                let connected = client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(300.0))
                if connected && client.Connected then
                    started <- true
                else
                    lastError <- "connection attempt timed out"
            with ex ->
                lastError <- ex.Message

            if not started then
                Thread.Sleep(200)

        if not started then
            failwithf "Embedded server failed to start on localhost:%d within %0.1f seconds (%s)" port timeout.TotalSeconds lastError

    let private jsonOptions =
        let opts = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        opts

    /// Map a stored conversation message (including its process steps) to the wire DTO.
    let private messageToDto (m: MessageRecord) : MessageDto =
        let steps =
            if isNull (box m.Steps) then [||]
            else
                m.Steps
                |> Seq.map (fun s ->
                    { TurnStepDto.Kind = s.Kind; Title = s.Title; Input = s.Input; Output = s.Output })
                |> Seq.toArray
        { MessageDto.Role = m.Role.ToLowerInvariant()
          Content = m.Content
          TurnId = (if isNull (box m.TurnId) then "" else m.TurnId)
          Steps = steps
          Attachments =
            if isNull (box m.Attachments) then [||]
            else m.Attachments |> Seq.toArray }

    let private sendWs (socket: WebSocket) (resp: WsResponse) = task {
        let json = JsonSerializer.Serialize(resp, jsonOptions)
        let bytes = Encoding.UTF8.GetBytes(json)
        do! socket.SendAsync(ArraySegment(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
    }

    let private handleWsMessage (send: WsResponse -> Task) (grainFactory: IGrainFactory) (sessionId: string) (msg: WsRequest) = task {
        let session = grainFactory.GetGrain<ISessionGrain>(sessionId)
        try
            match msg.Type with
            | WsRequestType.Chat ->
                // The payload is a structured ChatMessageRequest (text + attachments). The
                // attachment content is embedded into the LLM prompt only; the transcript
                // stores the text plus attachment names so the file body is never rendered.
                // Fall back to treating the payload as plain text for older clients.
                let request =
                    try
                        let r = JsonSerializer.Deserialize<ChatMessageRequest>(msg.Payload, jsonOptions)
                        if isNull (box r) || (isNull (box r.Text) && isNull (box r.Attachments))
                        then { Text = msg.Payload; Attachments = [||] }
                        else r
                    with _ -> { Text = msg.Payload; Attachments = [||] }

                let text = if isNull request.Text then "" else request.Text
                let attachments = if isNull (box request.Attachments) then [||] else request.Attachments
                // Persist uploaded attachments into the session's file folder so the user can
                // review or download them, and so the agent can read them on demand with the
                // read_file tool. The attachment content is deliberately NOT placed into the
                // prompt — the agent reads a file only when it actually needs the contents.
                // Each upload is stored under a content-hash name so two attachments sharing a
                // display name don't clobber each other (and identical content dedups); we map
                // the user's original name to the stored hash name for the agent.
                let saved =
                    if attachments.Length = 0 then [||]
                    else
                        let store = SessionFiles.forKey sessionId
                        attachments
                        |> Array.map (fun a ->
                            try
                                let bytes = System.Text.Encoding.UTF8.GetBytes(if isNull a.Content then "" else a.Content)
                                let dto = store.SaveUpload(a.Name, "", bytes)
                                (a.Name, dto.Name)
                            with _ -> (a.Name, a.Name))
                // The transcript chips show the original (display) names the user attached.
                let attachmentNames = saved |> Array.map fst
                let llmInput =
                    if attachments.Length = 0 then text
                    else
                        let mapping =
                            saved
                            |> Array.map (fun (display, stored) -> sprintf "\"%s\" as %s" display stored)
                            |> String.concat "; "
                        let note =
                            sprintf "[The user attached %d file(s). Each is stored under a unique name — reference it by that stored name when using the convert_document tool: %s.]"
                                attachments.Length mapping
                        if String.IsNullOrWhiteSpace text then note else text + "\n\n" + note

                // Run the turn while streaming the in-progress steps to the client, so the UI
                // can show "what's been done so far" live. We poll the grain's reentrant
                // GetLiveStepsAsync (which interleaves with the running turn) and push an
                // Event frame whenever a new step appears, then a final Done frame.
                let processTask = session.ProcessWithContextAsync(llmInput, text, attachmentNames)
                let mutable lastCount = -1
                while not processTask.IsCompleted do
                    let! _ = Task.WhenAny(processTask, Task.Delay(350))
                    if not processTask.IsCompleted then
                        let! steps = session.GetLiveStepsAsync()
                        if steps.Length <> lastCount then
                            lastCount <- steps.Length
                            let dtos =
                                steps
                                |> Array.map (fun s ->
                                    { TurnStepDto.Kind = s.Kind; Title = s.Title; Input = s.Input; Output = s.Output })
                            let payload = JsonSerializer.Serialize({| steps = dtos |}, jsonOptions)
                            do! send { Type = WsResponseType.Event; Payload = payload }
                let! response = processTask
                do! send { Type = WsResponseType.Done; Payload = response }

            | WsRequestType.Info ->
                let! info = session.GetInfoAsync()
                let payload = JsonSerializer.Serialize(
                    {| sessionId = info.SessionId; agentName = info.AgentName
                       workspaceKey = info.WorkspaceKey; activeConversation = info.ActiveConversation
                       isActive = info.IsActive; createdAt = info.CreatedAt; lastActiveAt = info.LastActiveAt |}, jsonOptions)
                do! send { Type = WsResponseType.Info; Payload = payload }

            | WsRequestType.History ->
                let! history = session.GetHistoryAsync()
                let dtos = history |> Array.map messageToDto
                let payload = JsonSerializer.Serialize(dtos, jsonOptions)
                do! send { Type = WsResponseType.History; Payload = payload }

            | WsRequestType.Clear ->
                do! session.ClearHistoryAsync()
                do! send { Type = WsResponseType.Done; Payload = "History cleared" }

            | WsRequestType.Conversations ->
                let! convs = session.ListConversationsAsync()
                let payload = JsonSerializer.Serialize(convs, jsonOptions)
                do! send { Type = WsResponseType.Conversations; Payload = payload }

            | WsRequestType.Switch ->
                do! session.SwitchConversationAsync(msg.Payload)
                do! send { Type = WsResponseType.Done; Payload = sprintf "Switched to: %s" msg.Payload }

            | WsRequestType.PermissionResponse ->
                // The user's answer to a permission prompt: hand it to the broker, which
                // resumes the parked tool call awaiting this decision. No reply frame.
                PermissionBroker.resolve msg.Payload

            | _ ->
                do! send { Type = WsResponseType.Error; Payload = "Unknown request type" }
        with ex ->
            do! send { Type = WsResponseType.Error; Payload = ex.Message }
    }

    let private handleWebSocket (ctx: HttpContext) (grainFactory: IGrainFactory) (sessionId: string) = task {
        let! socket = ctx.WebSockets.AcceptWebSocketAsync()
        let buffer = Array.zeroCreate<byte> 8192

        // A turn can stream step events while, concurrently, a tool's permission prompt is
        // pushed to the client — so serialize every write through one lock (WebSocket forbids
        // concurrent sends).
        let sendLock = new SemaphoreSlim(1, 1)
        let send (resp: WsResponse) : Task =
            (task {
                do! sendLock.WaitAsync()
                try do! sendWs socket resp
                finally sendLock.Release() |> ignore
            }) :> Task

        // Register this session's channel so the permission broker can prompt its user, and
        // make sure tool calls parked on a prompt fail closed once the socket goes away.
        PermissionBroker.registerSession sessionId (fun payload ->
            send { Type = WsResponseType.PermissionRequest; Payload = payload })

        try
            let mutable running = true
            while running && socket.State = WebSocketState.Open do
                let segments = ResizeArray<byte>()
                let mutable endOfMessage = false
                while not endOfMessage do
                    let! result = socket.ReceiveAsync(ArraySegment(buffer), CancellationToken.None)
                    if result.MessageType = WebSocketMessageType.Close then
                        do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                        running <- false
                        endOfMessage <- true
                    else
                        segments.AddRange(buffer.[0..result.Count - 1])
                        endOfMessage <- result.EndOfMessage

                if running && segments.Count > 0 then
                    let json = Encoding.UTF8.GetString(segments.ToArray())
                    try
                        let msg = JsonSerializer.Deserialize<WsRequest>(json, jsonOptions)
                        match msg.Type with
                        | WsRequestType.PermissionResponse ->
                            // Fast, non-blocking: just resume the parked tool call.
                            PermissionBroker.resolve msg.Payload
                        | _ ->
                            // Run the request WITHOUT awaiting it here, so the receive loop stays
                            // free to read a permission reply that a tool inside this very turn is
                            // waiting for. Errors are reported back over the socket.
                            (task {
                                try do! handleWsMessage send grainFactory sessionId msg
                                with ex -> do! send { Type = WsResponseType.Error; Payload = ex.Message }
                             })
                            |> ignore
                    with ex ->
                        do! send { Type = WsResponseType.Error; Payload = sprintf "Invalid message: %s" ex.Message }
        with
        | :? WebSocketException -> ()

        PermissionBroker.unregisterSession sessionId
        sendLock.Dispose()
    }

    let mutable private host: WebApplication option = None
    let mutable private cts: CancellationTokenSource option = None

    // ─────────────────────────────────────────────────────────────────────────
    // Feedback / suggestion enhancement loop — shared, Orleans-independent core.
    //
    // These helpers and endpoint mappings depend only on FeedbackService and
    // IWorkspaceRegistry, so they can be hosted standalone (see startEnhancementHost)
    // for fast integration tests without booting the Orleans silo or an LLM.
    // ─────────────────────────────────────────────────────────────────────────

    /// Load the on-disk workspace and merge the built-in assistant tools.
    let loadMergedWorkspace (workspaceRoot: string) =
        let workspace = WorkspaceLoader.loadWorkspace workspaceRoot
        { workspace with Tools = workspace.Tools @ AssistantTools.allTools }

    /// Reload the workspace from disk and overwrite the default registry entry.
    /// Used after a user registers (or promotes) a new tool/agent definition so
    /// it becomes resolvable without restarting the app.
    let reloadWorkspaceAt (workspaceRoot: string) (registry: IWorkspaceRegistry) =
        registry.Register(WorkspaceId.defaultId, loadMergedWorkspace workspaceRoot)

    /// Persist a user-supplied tool/agent JSON definition into the workspace and
    /// reload so it becomes resolvable. Returns the written file path.
    let registerDefinitionAt (workspaceRoot: string) (subdir: string) (req: RegisterDefinitionRequest) (registry: IWorkspaceRegistry) =
        let name = (req.Name |> Option.ofObj |> Option.defaultValue "").Trim()
        if String.IsNullOrEmpty name then Error "name is required"
        else
            let safe = name |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '-' || c = '_' then c else '_') |> Seq.toArray |> System.String
            let dir = Path.Combine(workspaceRoot, ".nao", subdir)
            Directory.CreateDirectory dir |> ignore
            let path = Path.Combine(dir, sprintf "%s.json" safe)
            let json = req.Definition.GetRawText()
            File.WriteAllText(path, json)
            reloadWorkspaceAt workspaceRoot registry
            Ok path

    /// Register the services the enhancement endpoints depend on. Used by the
    /// standalone test host; the production `start` registers richer variants.
    let registerEnhancementServices (services: IServiceCollection) (workspaceRoot: string) (feedbackDir: string) =
        services.AddSingleton<IWorkspaceRegistry>(fun _ ->
            let registry = WorkspaceRegistry()
            registry.Register(WorkspaceId.defaultId, loadMergedWorkspace workspaceRoot)
            registry :> IWorkspaceRegistry) |> ignore
        services.AddSingleton<FeedbackService>(fun _ -> FeedbackDb.file feedbackDir) |> ignore

    /// Map the register endpoints onto the given app. Shared by the production server
    /// and the test host. Feedback is recorded elsewhere; nothing here mutates tools/agents.
    let mapEnhancementEndpoints (app: WebApplication) (workspaceRoot: string) =
        let registerDefinition (subdir: string) (req: RegisterDefinitionRequest) (registry: IWorkspaceRegistry) = registerDefinitionAt workspaceRoot subdir req registry

        // ─── Register user-supplied tools / agents ───

        app.MapPost("/api/register/tool", Func<HttpContext, IWorkspaceRegistry, _>(fun ctx registry -> task {
            let! req = ctx.Request.ReadFromJsonAsync<RegisterDefinitionRequest>()
            match registerDefinition "tools" req registry with
            | Ok path -> return Results.Ok({| registered = true; path = path |})
            | Error e -> return Results.BadRequest({| error = e |})
        })) |> ignore

        app.MapPost("/api/register/agent", Func<HttpContext, IWorkspaceRegistry, _>(fun ctx registry -> task {
            let! req = ctx.Request.ReadFromJsonAsync<RegisterDefinitionRequest>()
            match registerDefinition "agents" req registry with
            | Ok path -> return Results.Ok({| registered = true; path = path |})
            | Error e -> return Results.BadRequest({| error = e |})
        })) |> ignore

    /// Start a standalone host exposing ONLY the enhancement-loop endpoints
    /// (no Orleans silo, no LLM). Intended for integration tests. Returns the
    /// running WebApplication so the caller can stop it.
    let startEnhancementHost (workspaceRoot: string) (feedbackDir: string) (port: int) : WebApplication =
        let builder = WebApplication.CreateBuilder([||])
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.UseUrls(sprintf "http://127.0.0.1:%d" port) |> ignore
        registerEnhancementServices builder.Services workspaceRoot feedbackDir
        let app = builder.Build()
        mapEnhancementEndpoints app workspaceRoot
        app.StartAsync().GetAwaiter().GetResult()
        app

    /// Start the embedded server on a background thread. Returns the base URL.
    let start (settings: AppSettings) : string =
        let port = 5000
        let baseUrl = sprintf "http://localhost:%d" port

        Database.initialize ()

        let tcs = TaskCompletionSource<unit>()
        let cancellation = new CancellationTokenSource()
        cts <- Some cancellation

        Task.Factory.StartNew((fun () ->
            try
                let builder = WebApplication.CreateBuilder([||])

                builder.Host.UseOrleans(fun (siloBuilder: ISiloBuilder) ->
                    siloBuilder
                        .UseLocalhostClustering()
                        .AddAdoNetGrainStorage("sessionStore", fun (opts: Orleans.Configuration.AdoNetGrainStorageOptions) ->
                            opts.Invariant <- "System.Data.SQLite"
                            opts.ConnectionString <- Database.connectionString)
                        .Configure<ClusterOptions>(fun (opts: ClusterOptions) ->
                            opts.ClusterId <- "nao-desktop"
                            opts.ServiceId <- "nao-desktop")
                        // LLM turns routinely run far longer than Orleans' default 30s
                        // response timeout, so raise it for both the silo and the
                        // co-hosted client to avoid spurious timeout exceptions.
                        .Configure<SiloMessagingOptions>(fun (opts: SiloMessagingOptions) ->
                            opts.ResponseTimeout <- TimeSpan.FromMinutes(10.0)
                            opts.SystemResponseTimeout <- TimeSpan.FromMinutes(10.0))
                        .Configure<ClientMessagingOptions>(fun (opts: ClientMessagingOptions) ->
                            opts.ResponseTimeout <- TimeSpan.FromMinutes(10.0))
                    |> ignore)
                |> ignore

                let workspaceRoot =
                    let envPath = Environment.GetEnvironmentVariable("NAO_WORKSPACE")
                    if String.IsNullOrEmpty(envPath) then
                        Path.Combine(AppContext.BaseDirectory, ".nao")
                        |> fun p -> Path.GetFullPath(Path.Combine(p, ".."))
                    else envPath

                builder.Services.AddSingleton<ILlmProvider>(fun _ ->
                    // Build the provider the user actually selected (Ollama / vLLM /
                    // llama.cpp / OpenAI), defaulting blanks per provider, instead of
                    // hard-coding Ollama.
                    ProviderCatalog.toProviderType settings.Provider
                    |> ProviderFactory.create) |> ignore

                builder.Services.AddSingleton<IWorkspaceRegistry>(fun _ ->
                    let registry = WorkspaceRegistry()
                    registry.Register(WorkspaceId.defaultId, loadMergedWorkspace workspaceRoot)
                    registry :> IWorkspaceRegistry) |> ignore

                builder.Services.AddSingleton<IOrchestratorFactory>(fun _ ->
                    DefaultOrchestratorFactory() :> IOrchestratorFactory) |> ignore

                // All per-session data lives under .nao-data/sessions/<key>/ so everything
                // about one conversation — its messages, files, observability traces and
                // feedback — sits in a single folder, keyed by the sanitized grain key.
                let sessionsRoot = Path.Combine(Database.dataDir, "sessions")

                // Single event bus shared by every storage consumer. Producers (the grain,
                // the agent harness) publish domain events; consumers subscribe / wrap and
                // persist them, choosing the folder from the event's session key — so a
                // producer never decides where data lands.
                let eventBus = InMemoryEventBus() :> IEventBus

                // Conversation history persistence — the file store writes to
                // sessions/<key>/conversations/, wrapped in a PublishingConversationStore tee
                // so every transcript WRITE is also broadcast as a ConversationCaptured event.
                // Swapping FileConversationStore for a database/cloud store needs ZERO producer
                // changes, and any subscriber can persist/forward the transcript stream.
                builder.Services.AddSingleton<IConversationStore>(fun _ ->
                    PublishingConversationStore(eventBus, FileConversationStore(sessionsRoot))
                    :> IConversationStore) |> ignore

                // Externalized task tracking — each async task grain mirrors its authoritative
                // state to sessions/<key>/tasks.json (+ tasks/<id>/meta.json) so the full task
                // history is readable straight from disk without activating a grain. A task's
                // folder also holds the sub-session it spawns under tasks/<id>/sessions/.
                builder.Services.AddSingleton<ITaskStore>(fun _ ->
                    FileTaskStore(sessionsRoot) :> ITaskStore) |> ignore

                // Observability storage via the event bus. ObservabilityServices wraps a
                // PER-SESSION backing bundle rooted at sessions/<key>/observability/ in a
                // PublishingHarnessServices tee: every span/metric/journal/trace/audit WRITE
                // is broadcast as an ObservabilityCaptured event while reads (regression
                // baselines, revert history) still hit the real backing store. Where the data
                // lands is the backing factory (the store-level swap point).
                let observability =
                    ObservabilityServices(eventBus, fun key ->
                        let dir = Path.Combine(SessionFiles.sessionDir key, "observability")
                        Persistence.harnessServices (PersistenceMode.File dir))
                builder.Services.AddSingleton<Func<string, string, IHarnessServices>>(fun _ ->
                    Func<string, string, IHarnessServices>(fun key turnId -> observability.ServicesFor(key, turnId))) |> ignore

                // Feedback storage via the event bus. FeedbackEventConsumer is BOTH the
                // consumer (persists published TurnCompleted / ImplicitFeedbackCaptured events
                // under sessions/<key>/feedback/) AND the read/command side the grain queries.
                // The backing FeedbackService is the store-level swap point (File today).
                let feedbackConsumer =
                    FeedbackEventConsumer(fun key -> Path.Combine(SessionFiles.sessionDir key, "feedback"))
                eventBus.Subscribe(feedbackConsumer :> IEventConsumer)
                builder.Services.AddSingleton<IEventBus>(eventBus) |> ignore
                builder.Services.AddSingleton<Func<string, FeedbackService>>(fun _ ->
                    Func<string, FeedbackService>(fun key -> feedbackConsumer.FeedbackFor key)) |> ignore

                // Global feedback registry backing the feedback recording + register endpoints.
                let feedbackDir = Path.Combine(Database.dataDir, "feedback")
                builder.Services.AddSingleton<FeedbackService>(fun _ ->
                    FeedbackDb.file feedbackDir) |> ignore

                // Async-task executors — matched by Kind on the SessionTaskGrain. Agent tasks
                // drive a sub-session (e.g. the async converter agent runs its whole harness there).
                // Tool tasks run an async executable tool in the background on its own grain.
                builder.Services.AddSingleton<ITaskExecutor, AgentTaskExecutor>() |> ignore
                builder.Services.AddSingleton<ITaskExecutor, ToolTaskExecutor>() |> ignore

                let app = builder.Build()
                app.UseWebSockets() |> ignore

                app.MapPost("/api/sessions", Func<HttpContext, IGrainFactory, _>(fun ctx grainFactory -> task {
                    let! request = ctx.Request.ReadFromJsonAsync<SessionStartRequest>()
                    let userId = Environment.UserName
                    let sessionId = Guid.NewGuid().ToString("N").[..7]
                    let grainKey = sprintf "%s/%s" userId sessionId

                    let session = grainFactory.GetGrain<ISessionGrain>(grainKey)
                    let startOpts = SessionStartOptions()
                    startOpts.AgentName <- request.AgentName
                    startOpts.WorkspaceKey <- request.WorkspaceKey
                    startOpts.ToolNames <- ResizeArray(request.ToolNames)

                    let! started = session.StartAsync(startOpts)
                    if started then
                        // Register in session directory for discoverability after restart
                        let directory = grainFactory.GetGrain<ISessionDirectoryGrain>(userId)
                        let entry = SessionDirectoryEntry()
                        entry.SessionId <- grainKey
                        entry.AgentName <- request.AgentName
                        entry.Title <- sprintf "%s session" request.AgentName
                        entry.CreatedAt <- DateTimeOffset.UtcNow
                        entry.LastActiveAt <- DateTimeOffset.UtcNow
                        entry.IsActive <- true
                        do! directory.RegisterAsync(entry)
                        return Results.Ok({| sessionId = grainKey |})
                    else
                        return Results.BadRequest({| error = "Failed to start session" |})
                })) |> ignore

                app.MapGet("/api/sessions", Func<IGrainFactory, _>(fun grainFactory -> task {
                    let userId = Environment.UserName
                    let directory = grainFactory.GetGrain<ISessionDirectoryGrain>(userId)
                    let! entries = directory.ListAllAsync()
                    let dtos =
                        entries
                        |> Array.map (fun e ->
                            {| sessionId = e.SessionId
                               agentName = e.AgentName
                               title = e.Title
                               createdAt = e.CreatedAt
                               lastActiveAt = e.LastActiveAt
                               isActive = e.IsActive |})
                    return Results.Ok(dtos)
                })) |> ignore

                app.MapGet("/api/sessions/history/{**id}", Func<IGrainFactory, string, _>(fun grainFactory id -> task {
                    let session = grainFactory.GetGrain<ISessionGrain>(id)
                    let! history = session.GetHistoryAsync()
                    let dtos = history |> Array.map messageToDto
                    return Results.Ok(dtos)
                })) |> ignore

                app.MapPost("/api/sessions/feedback/{**id}", Func<HttpContext, IGrainFactory, string, _>(fun ctx grainFactory id -> task {
                    let! request = ctx.Request.ReadFromJsonAsync<FeedbackRequest>()
                    let session = grainFactory.GetGrain<ISessionGrain>(id)
                    let! rationales = session.SubmitFeedbackAsync(request.Sentiment, request.Comment)
                    return Results.Ok({| proposals = rationales |})
                })) |> ignore

                // ─── Per-session files & async tasks ───
                // The session key ("userId/sessionId") contains a slash, so it is always
                // captured by the trailing catch-all route segment; the file/task id is
                // passed as a query parameter.

                // List the files stored for a session (uploads + tool/agent output).
                app.MapGet("/api/sessions/files/{**id}", Func<string, _>(fun id -> task {
                    let store = SessionFiles.forKey id
                    return Results.Ok(store.List() |> List.toArray)
                })) |> ignore

                // Download a single session file by id (?fileId=...).
                app.MapGet("/api/sessions/file/{**id}", Func<HttpContext, string, _>(fun ctx id -> task {
                    let fileId = ctx.Request.Query.["fileId"].ToString()
                    let store = SessionFiles.forKey id
                    match store.TryOpen fileId with
                    | Some(dto, bytes) ->
                        let mt = if String.IsNullOrWhiteSpace dto.MediaType then "application/octet-stream" else dto.MediaType
                        return Results.File(bytes, mt, dto.DisplayName)
                    | None -> return Results.NotFound()
                })) |> ignore

                // List the async tasks for a session (with live status/progress).
                app.MapGet("/api/sessions/tasks/{**id}", Func<IGrainFactory, string, _>(fun grainFactory id -> task {
                    let session = grainFactory.GetGrain<ISessionGrain>(id)
                    let! tasks = session.ListTasksAsync()
                    let dtos =
                        tasks
                        |> Array.map (fun (t: Nao.Runtime.Orleans.Grains.TaskRef) ->
                            { Id = t.TaskId
                              Kind = t.Kind
                              Title = t.Title
                              Status = t.Status
                              Progress = t.Progress
                              Message = t.Message
                              ResultFileId = (if t.ResultFileIds.Count > 0 then t.ResultFileIds.[0] else "")
                              Error = t.Error
                              TurnId = t.TurnId
                              CreatedAt = t.CreatedAt
                              UpdatedAt = t.UpdatedAt } : TaskDto)
                    return Results.Ok(dtos)
                })) |> ignore

                // Register endpoints — shared with the standalone enhancement test host.
                mapEnhancementEndpoints app workspaceRoot

                // ─── Workspace knowledge base (RAG) ───
                let knowledge = Knowledge.KnowledgeStore(workspaceRoot)

                app.MapGet("/api/knowledge", Func<HttpContext, _>(fun _ctx -> task {
                    return Results.Ok(knowledge.Files())
                })) |> ignore

                app.MapPost("/api/knowledge", Func<HttpContext, _>(fun ctx -> task {
                    let! req = ctx.Request.ReadFromJsonAsync<KnowledgeUploadRequest>()
                    if String.IsNullOrWhiteSpace req.Name then
                        return Results.BadRequest({| error = "name is required" |})
                    else
                        knowledge.Save req.Name (req.Content |> Option.ofObj |> Option.defaultValue "")
                        return Results.Ok({| saved = true; name = req.Name |})
                })) |> ignore

                app.MapDelete("/api/knowledge/{name}", Func<string, _>(fun name -> task {
                    let ok = knowledge.Delete name
                    return (if ok then Results.Ok({| deleted = true |}) else Results.NotFound())
                })) |> ignore

                // ─── List + LLM generation of tools and agents ───
                app.MapGet("/api/tools", Func<IWorkspaceRegistry, _>(fun registry -> task {
                    let defs = registry.Get WorkspaceId.defaultId
                    let code =
                        AssistantTools.allTools
                        |> List.map (fun t -> ({ Name = t.Name; Description = t.Description; Source = "code" } : DefinitionInfoDto))
                    let json =
                        defs.ToolDefs
                        |> List.map (fun d -> ({ Name = d.Name; Description = d.Description; Source = "json" } : DefinitionInfoDto))
                    return Results.Ok(code @ json)
                })) |> ignore

                app.MapPost("/api/tools/generate", Func<HttpContext, ILlmProvider, _>(fun ctx provider -> task {
                    let! req = ctx.Request.ReadFromJsonAsync<GenerateRequest>()
                    let! result = Generation.generateTool provider req.Requirement
                    match result with
                    | Ok dto -> return Results.Ok(dto)
                    | Error e -> return Results.BadRequest({| error = e |})
                })) |> ignore

                app.MapGet("/api/agents", Func<IWorkspaceRegistry, _>(fun registry -> task {
                    let defs = registry.Get WorkspaceId.defaultId
                    let agents =
                        defs.AgentDefs
                        |> List.map (fun a -> ({ Name = a.Name; Description = a.Description; Source = "json" } : DefinitionInfoDto))
                    return Results.Ok(agents)
                })) |> ignore

                app.MapPost("/api/agents/generate", Func<HttpContext, IWorkspaceRegistry, ILlmProvider, _>(fun ctx registry provider -> task {
                    let! req = ctx.Request.ReadFromJsonAsync<GenerateRequest>()
                    let defs = registry.Get WorkspaceId.defaultId
                    let toolNames =
                        (AssistantTools.allTools |> List.map (fun t -> t.Name))
                        @ (defs.ToolDefs |> List.map (fun d -> d.Name))
                    let! result = Generation.generateAgent provider toolNames req.Requirement
                    match result with
                    | Ok dto -> return Results.Ok(dto)
                    | Error e -> return Results.BadRequest({| error = e |})
                })) |> ignore

                app.Map("/ws/sessions/{**id}", Func<HttpContext, IGrainFactory, string, _>(fun ctx grainFactory id -> task {
                    if ctx.WebSockets.IsWebSocketRequest then
                        do! handleWebSocket ctx grainFactory id
                        return Results.Empty
                    else
                        return Results.BadRequest({| error = "WebSocket connection required" |})
                })) |> ignore

                host <- Some app
                app.StartAsync(cancellation.Token).GetAwaiter().GetResult()
                tcs.TrySetResult() |> ignore

                // Block this background thread until shutdown is requested.
                app.WaitForShutdownAsync(cancellation.Token).GetAwaiter().GetResult()
            with ex ->
                tcs.TrySetException(ex) |> ignore
        ), cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default) |> ignore

        // Wait for host startup completion and verify the listener is reachable.
        let startupCompleted = tcs.Task.Wait(TimeSpan.FromSeconds(20.0))
        if not startupCompleted then
            failwith "Embedded server startup timed out after 20 seconds"

        waitForListener port (TimeSpan.FromSeconds(8.0))
        baseUrl

    /// Gracefully stop the embedded server.
    let stop () =
        // Cancel first to abort any in-flight requests
        match cts with
        | Some c ->
            c.Cancel()
            c.Dispose()
            cts <- None
        | None -> ()

        match host with
        | Some app ->
            try
                // Use a very short timeout — for an embedded local silo there's
                // nothing to gracefully hand off to.
                app.StopAsync(TimeSpan.FromMilliseconds(500.0)).Wait(1000) |> ignore
            with _ -> ()
            host <- None
        | None -> ()
        
        // Force exit the process to avoid Orleans silo lingering
        Environment.Exit(0)

    /// Restart the server with new settings (e.g. after provider/model change).
    let restart (settings: AppSettings) =
        // Stop existing server (without Environment.Exit)
        match cts with
        | Some c ->
            c.Cancel()
            c.Dispose()
            cts <- None
        | None -> ()

        match host with
        | Some app ->
            try app.StopAsync(TimeSpan.FromMilliseconds(500.0)).Wait(1000) |> ignore
            with _ -> ()
            host <- None
        | None -> ()

        // Start fresh
        start settings |> ignore
