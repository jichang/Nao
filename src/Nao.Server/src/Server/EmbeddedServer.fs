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
open Nao.Core
open Nao.Agents
open Nao.Loader
open Nao.Providers
open Nao.Persistence
open Nao.Feedback
open Nao.Events
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

    let private handleWsMessage (socket: WebSocket) (grainFactory: IGrainFactory) (sessionId: string) (msg: WsRequest) = task {
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
                // Each upload is stored under a unique name so two attachments sharing a name
                // don't clobber each other; we tell the agent the actual stored names.
                let attachmentNames =
                    if attachments.Length = 0 then [||]
                    else
                        let store = SessionFiles.forKey sessionId
                        attachments
                        |> Array.map (fun a ->
                            try
                                let saved = store.SaveText(a.Name, "upload", "", (if isNull a.Content then "" else a.Content), ensureUnique = true)
                                saved.Name
                            with _ -> a.Name)
                let llmInput =
                    if attachments.Length = 0 then text
                    else
                        let names = attachmentNames |> String.concat ", "
                        let note =
                            sprintf "[The user attached %d file(s), saved to the workspace: %s. The contents are not shown here — use the read_file tool with a file's name to read it, but only if you actually need its contents.]"
                                attachments.Length names
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
                            do! sendWs socket { Type = WsResponseType.Event; Payload = payload }
                let! response = processTask
                do! sendWs socket { Type = WsResponseType.Done; Payload = response }

            | WsRequestType.Info ->
                let! info = session.GetInfoAsync()
                let payload = JsonSerializer.Serialize(
                    {| sessionId = info.SessionId; agentName = info.AgentName
                       workspaceKey = info.WorkspaceKey; activeConversation = info.ActiveConversation
                       isActive = info.IsActive; createdAt = info.CreatedAt; lastActiveAt = info.LastActiveAt |}, jsonOptions)
                do! sendWs socket { Type = WsResponseType.Info; Payload = payload }

            | WsRequestType.History ->
                let! history = session.GetHistoryAsync()
                let dtos = history |> Array.map messageToDto
                let payload = JsonSerializer.Serialize(dtos, jsonOptions)
                do! sendWs socket { Type = WsResponseType.History; Payload = payload }

            | WsRequestType.Clear ->
                do! session.ClearHistoryAsync()
                do! sendWs socket { Type = WsResponseType.Done; Payload = "History cleared" }

            | WsRequestType.Conversations ->
                let! convs = session.ListConversationsAsync()
                let payload = JsonSerializer.Serialize(convs, jsonOptions)
                do! sendWs socket { Type = WsResponseType.Conversations; Payload = payload }

            | WsRequestType.Switch ->
                do! session.SwitchConversationAsync(msg.Payload)
                do! sendWs socket { Type = WsResponseType.Done; Payload = sprintf "Switched to: %s" msg.Payload }

            | _ ->
                do! sendWs socket { Type = WsResponseType.Error; Payload = "Unknown request type" }
        with ex ->
            do! sendWs socket { Type = WsResponseType.Error; Payload = ex.Message }
    }

    let private handleWebSocket (ctx: HttpContext) (grainFactory: IGrainFactory) (sessionId: string) = task {
        let! socket = ctx.WebSockets.AcceptWebSocketAsync()
        let buffer = Array.zeroCreate<byte> 8192

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
                        do! handleWsMessage socket grainFactory sessionId msg
                    with ex ->
                        do! sendWs socket { Type = WsResponseType.Error; Payload = sprintf "Invalid message: %s" ex.Message }
        with
        | :? WebSocketException -> ()
    }

    let mutable private host: WebApplication option = None
    let mutable private cts: CancellationTokenSource option = None

    /// Mutable handler for tool confirmation requests.
    /// The UI layer sets this to show a popup dialog.
    let mutable confirmationHandler: ToolConfirmationRequest -> unit =
        fun req -> req.Completion.TrySetResult(true) |> ignore

    /// Set the handler that will be called when the orchestrator needs tool confirmation.
    let setConfirmationHandler (handler: ToolConfirmationRequest -> unit) =
        confirmationHandler <- handler

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

    /// Build a CANDIDATE workspace from all Confirmed suggestions: the proposed
    /// improvement overlays are baked into a fresh copy of the workspace and
    /// registered under the "candidate" key, so the user can start a test session
    /// against the enhanced tools/agents without touching the live "default" one.
    let buildCandidateAt (workspaceRoot: string) (feedback: FeedbackService) (registry: IWorkspaceRegistry) =
        task {
            let! confirmed = feedback.GetSuggestionsByStatusAsync SuggestionStatus.Confirmed
            let anns = confirmed |> List.choose (fun s -> s.ProposedAnnotation)
            let toolAnns = anns |> List.filter (fun a -> a.Kind = AnnotationKind.Tool)
            let agentAnns = anns |> List.filter (fun a -> a.Kind = AnnotationKind.Agent)
            let workspace = WorkspaceLoader.loadWorkspace workspaceRoot
            let mergedTools = workspace.Tools @ AssistantTools.allTools
            let candidate =
                { workspace with
                    Tools = Annotations.applyToolAnnotations toolAnns mergedTools
                    AgentDefs = workspace.AgentDefs |> List.map (Annotations.applyAgentAnnotations agentAnns) }
            registry.Register(WorkspaceId.create "candidate", candidate)
            return confirmed
        }

    /// Upgrade the live system from all Confirmed suggestions: bake each improvement
    /// into the canonical workspace definition (rewriting the JSON file when the
    /// target is JSON-sourced, otherwise persisting a durable live annotation as a
    /// fallback), mark the suggestion Applied, reload "default", and drop the
    /// candidate. This is the irreversible "make it permanent" step.
    let upgradeCandidateAt (workspaceRoot: string) (feedback: FeedbackService) (registry: IWorkspaceRegistry) =
        task {
            let! confirmed = feedback.GetSuggestionsByStatusAsync SuggestionStatus.Confirmed
            let live = registry.TryGet WorkspaceId.defaultId
            let results = ResizeArray<{| target: string; kind: string; persisted: string; detail: string |}>()
            for s in confirmed do
                match s.ProposedAnnotation with
                | None -> ()
                | Some ann ->
                    let prov =
                        match ann.Provenance with
                        | Some p -> Some p
                        | None ->
                            match s.Kind, live with
                            | AnnotationKind.Agent, Some w ->
                                w.AgentDefs |> List.tryFind (fun d -> d.Name = s.TargetName) |> Option.bind (fun d -> d.Provenance)
                            | AnnotationKind.Tool, Some w ->
                                w.ToolDefs |> List.tryFind (fun d -> d.Name = s.TargetName) |> Option.bind (fun d -> d.Provenance)
                            | _ -> None
                    let rewrite =
                        match s.Kind with
                        | AnnotationKind.Tool -> Annotations.rewriteToolDefinition prov [ ann ]
                        | AnnotationKind.Agent -> Annotations.rewriteAgentDefinition prov [ ann ]
                    let kindLabel = match s.Kind with AnnotationKind.Tool -> "tool" | AnnotationKind.Agent -> "agent"
                    match rewrite with
                    | Ok path ->
                        let! _ = feedback.MarkSuggestionAppliedAsync s.Id
                        results.Add({| target = s.TargetName; kind = kindLabel; persisted = "file"; detail = path |})
                    | Error e ->
                        // Fallback: persist as a live annotation overlay so the
                        // improvement still sticks even without a JSON source file.
                        let! _ = feedback.AddAnnotationAsync ann
                        let! _ = feedback.MarkSuggestionAppliedAsync s.Id
                        results.Add({| target = s.TargetName; kind = kindLabel; persisted = "annotation"; detail = e |})
            reloadWorkspaceAt workspaceRoot registry
            registry.Remove(WorkspaceId.create "candidate") |> ignore
            return List.ofSeq results
        }

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

    let private parseKind (s: string) : AnnotationKind option =
        match (s |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
        | "tool" -> Some AnnotationKind.Tool
        | "agent" -> Some AnnotationKind.Agent
        | _ -> None

    let private strOpt (s: string) = if String.IsNullOrWhiteSpace s then None else Some s
    let private jsonResult (value: 'a) = Results.Content(FeedbackJson.serializeIndented value, "application/json")

    /// Register the services the enhancement endpoints depend on. Used by the
    /// standalone test host; the production `start` registers richer variants.
    let registerEnhancementServices (services: IServiceCollection) (workspaceRoot: string) (feedbackDir: string) =
        services.AddSingleton<IWorkspaceRegistry>(fun _ ->
            let registry = WorkspaceRegistry()
            registry.Register(WorkspaceId.defaultId, loadMergedWorkspace workspaceRoot)
            registry :> IWorkspaceRegistry) |> ignore
        services.AddSingleton<FeedbackService>(fun _ -> FeedbackService.File feedbackDir) |> ignore

    /// Map all feedback / annotation / version / suggestion / candidate / register
    /// endpoints onto the given app. Shared by the production server and the test host.
    let mapEnhancementEndpoints (app: WebApplication) (workspaceRoot: string) =
        let reloadWorkspace (registry: IWorkspaceRegistry) = reloadWorkspaceAt workspaceRoot registry
        let buildCandidate (feedback: FeedbackService) (registry: IWorkspaceRegistry) = buildCandidateAt workspaceRoot feedback registry
        let upgradeCandidate (feedback: FeedbackService) (registry: IWorkspaceRegistry) = upgradeCandidateAt workspaceRoot feedback registry
        let registerDefinition (subdir: string) (req: RegisterDefinitionRequest) (registry: IWorkspaceRegistry) = registerDefinitionAt workspaceRoot subdir req registry

        // ─── Annotations: persistent runtime overlays on tools/agents ───

        app.MapGet("/api/annotations", Func<FeedbackService, _>(fun feedback -> task {
            let! annotations = feedback.ListAnnotationsAsync()
            return jsonResult annotations
        })) |> ignore

        app.MapPost("/api/annotations", Func<HttpContext, FeedbackService, _>(fun ctx feedback -> task {
            let! req = ctx.Request.ReadFromJsonAsync<AnnotationRequest>()
            match parseKind req.Kind with
            | None -> return Results.BadRequest({| error = "kind must be 'tool' or 'agent'" |})
            | Some kind ->
                let baseAnn =
                    match kind with
                    | AnnotationKind.Tool -> Annotation.ForTool req.TargetName
                    | AnnotationKind.Agent -> Annotation.ForAgent req.TargetName
                let annotation =
                    { baseAnn with
                        BaseVersion = strOpt req.BaseVersion
                        DescriptionOverride = strOpt req.DescriptionOverride
                        DescriptionAppend = strOpt req.DescriptionAppend
                        InputPrefix = strOpt req.InputPrefix
                        OutputSuffix = strOpt req.OutputSuffix
                        GuidanceAppend = strOpt req.GuidanceAppend
                        Reason = strOpt req.Reason }
                let! stored = feedback.AddAnnotationAsync annotation
                return jsonResult stored
        })) |> ignore

        app.MapPost("/api/annotations/{id}/status", Func<HttpContext, FeedbackService, string, _>(fun ctx feedback id -> task {
            let! req = ctx.Request.ReadFromJsonAsync<AnnotationStatusRequest>()
            match Guid.TryParse id with
            | false, _ -> return Results.BadRequest({| error = "invalid annotation id" |})
            | true, guid ->
                let status =
                    match (req.Status |> Option.ofObj |> Option.defaultValue "").Trim().ToLowerInvariant() with
                    | "disabled" | "off" -> AnnotationStatus.Disabled
                    | _ -> AnnotationStatus.Active
                let! ok = feedback.SetAnnotationStatusAsync(guid, status)
                return (if ok then Results.Ok({| updated = true |}) else Results.NotFound())
        })) |> ignore

        app.MapDelete("/api/annotations/{id}", Func<FeedbackService, string, _>(fun feedback id -> task {
            match Guid.TryParse id with
            | false, _ -> return Results.BadRequest({| error = "invalid annotation id" |})
            | true, guid ->
                let! ok = feedback.DropAnnotationAsync guid
                return (if ok then Results.Ok({| dropped = true |}) else Results.NotFound())
        })) |> ignore

        // ─── Versions: reviewed Draft → Active → Deprecated lifecycle ───

        app.MapGet("/api/versions", Func<FeedbackService, _>(fun feedback -> task {
            let! versions = feedback.ListVersionsAsync()
            return jsonResult versions
        })) |> ignore

        app.MapPost("/api/versions/promote", Func<HttpContext, FeedbackService, IWorkspaceRegistry, _>(fun ctx feedback registry -> task {
            let! req = ctx.Request.ReadFromJsonAsync<PromoteVersionRequest>()
            match parseKind req.Kind with
            | None -> return Results.BadRequest({| error = "kind must be 'tool' or 'agent'" |})
            | Some kind ->
                let! version =
                    match strOpt req.Version with
                    | Some v -> feedback.PromoteAsync(kind, req.TargetName, version = v)
                    | None -> feedback.PromoteAsync(kind, req.TargetName)
                // Materialised definition (if any) becomes resolvable after reload.
                if version.Location.IsSome then reloadWorkspace registry
                return jsonResult version
        })) |> ignore

        app.MapPost("/api/versions/{id}/confirm", Func<HttpContext, FeedbackService, IWorkspaceRegistry, string, _>(fun ctx feedback registry id -> task {
            let! req = ctx.Request.ReadFromJsonAsync<ConfirmVersionRequest>()
            match Guid.TryParse id with
            | false, _ -> return Results.BadRequest({| error = "invalid version id" |})
            | true, guid ->
                let! ok = feedback.ConfirmVersionAsync(guid, req.ReplaceLegacy)
                if ok then reloadWorkspace registry
                return (if ok then Results.Ok({| confirmed = true |}) else Results.NotFound())
        })) |> ignore

        app.MapPost("/api/versions/{id}/deprecate", Func<FeedbackService, string, _>(fun feedback id -> task {
            match Guid.TryParse id with
            | false, _ -> return Results.BadRequest({| error = "invalid version id" |})
            | true, guid ->
                let! ok = feedback.DeprecateVersionAsync guid
                return (if ok then Results.Ok({| deprecated = true |}) else Results.NotFound())
        })) |> ignore

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

        // ─── Cross-session suggestions (review-gated enhancement pipeline) ───

        app.MapGet("/api/suggestions", Func<FeedbackService, _>(fun feedback -> task {
            let! suggestions = feedback.ListSuggestionsAsync()
            return jsonResult suggestions
        })) |> ignore

        // "Enhance the system": aggregate ALL feedback (explicit + implicit) across
        // every session into review-gated improvement suggestions.
        app.MapPost("/api/suggestions/generate", Func<FeedbackService, _>(fun feedback -> task {
            let! generated = feedback.GenerateSuggestionsAsync()
            return jsonResult generated
        })) |> ignore

        app.MapPost("/api/suggestions/{id}/confirm", Func<FeedbackService, string, _>(fun feedback id -> task {
            match Guid.TryParse id with
            | false, _ -> return Results.BadRequest({| error = "invalid suggestion id" |})
            | true, guid ->
                let! ok = feedback.ConfirmSuggestionAsync guid
                return (if ok then Results.Ok({| confirmed = true |}) else Results.NotFound())
        })) |> ignore

        app.MapPost("/api/suggestions/{id}/reject", Func<FeedbackService, string, _>(fun feedback id -> task {
            match Guid.TryParse id with
            | false, _ -> return Results.BadRequest({| error = "invalid suggestion id" |})
            | true, guid ->
                let! ok = feedback.RejectSuggestionAsync guid
                return (if ok then Results.Ok({| rejected = true |}) else Results.NotFound())
        })) |> ignore

        // ─── Candidate workspace: test confirmed improvements, then upgrade ───

        // Build a sandbox workspace ("candidate") from all confirmed suggestions so the
        // user can start a test session against it (WorkspaceKey = "candidate").
        app.MapPost("/api/candidate/build", Func<FeedbackService, IWorkspaceRegistry, _>(fun feedback registry -> task {
            let! confirmed = buildCandidate feedback registry
            return Results.Ok({| workspaceKey = "candidate"; improvements = List.length confirmed |})
        })) |> ignore

        // Promote the confirmed improvements into the live system permanently.
        app.MapPost("/api/candidate/upgrade", Func<FeedbackService, IWorkspaceRegistry, _>(fun feedback registry -> task {
            let! results = upgradeCandidate feedback registry
            return Results.Ok({| upgraded = List.length results; results = results |})
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
                    AssistantOrchestratorFactory(fun req -> confirmationHandler req) :> IOrchestratorFactory) |> ignore

                // All per-session data lives under .nao-data/sessions/<key>/ so everything
                // about one conversation — its messages, files, observability traces and
                // feedback — sits in a single folder, keyed by the sanitized grain key.
                let sessionsRoot = Path.Combine(Database.dataDir, "sessions")

                // Single event bus shared by every storage strategy. Producers (the grain,
                // the agent harness) publish domain events; strategies subscribe / wrap and
                // decide where data lands — swapping a strategy never touches a producer.
                let eventBus = InMemoryEventBus() :> IEventBus

                // Conversation history persistence — the file store writes to
                // sessions/<key>/conversations/, wrapped in a PublishingConversationStore tee
                // so every transcript WRITE is also broadcast as a ConversationCaptured event.
                // Swapping FileConversationStore for a database/cloud store needs ZERO producer
                // changes, and any subscriber can persist/forward the transcript stream.
                builder.Services.AddSingleton<IConversationStore>(fun _ ->
                    PublishingConversationStore(eventBus, FileConversationStore(sessionsRoot))
                    :> IConversationStore) |> ignore

                // Observability storage via the event bus. The session strategy wraps a
                // PER-SESSION backing bundle rooted at sessions/<key>/observability/ in a
                // PublishingHarnessServices tee: every span/metric/journal/trace/audit WRITE
                // is broadcast as an ObservabilityCaptured event while reads (regression
                // baselines, revert history) still hit the real backing store. Swap it for
                // CategoryObservabilityStrategy to share one folder — ZERO producer changes.
                let observabilityStrategy : IObservabilityStorageStrategy =
                    SessionObservabilityStrategy(eventBus, fun key ->
                        let dir = Path.Combine(SessionFiles.sessionDir key, "observability")
                        Persistence.harnessServices (PersistenceMode.File dir))
                builder.Services.AddSingleton<Func<string, IHarnessServices>>(fun _ ->
                    Func<string, IHarnessServices>(fun key -> observabilityStrategy.ServicesFor key)) |> ignore

                // Feedback storage via the event bus. The session strategy is BOTH the
                // consumer (persists published TurnCompleted / ImplicitFeedbackCaptured events
                // under sessions/<key>/feedback/) AND the read/command side the grain queries.
                // Swap it for CategoryFeedbackStrategy to change the layout with ZERO producer
                // changes — that is the whole point of routing through events.
                let feedbackStrategy : IFeedbackStorageStrategy =
                    SessionFeedbackStrategy(fun key -> Path.Combine(SessionFiles.sessionDir key, "feedback"))
                eventBus.Subscribe(feedbackStrategy :> IEventConsumer)
                builder.Services.AddSingleton<IEventBus>(eventBus) |> ignore
                builder.Services.AddSingleton<Func<string, FeedbackService>>(fun _ ->
                    Func<string, FeedbackService>(fun key -> feedbackStrategy.FeedbackFor key)) |> ignore

                // Global feedback registry backing the management/enhancement HTTP endpoints
                // (/api/annotations, /api/versions, /api/suggestions, /api/candidate).
                let feedbackDir = Path.Combine(Database.dataDir, "feedback")
                builder.Services.AddSingleton<FeedbackService>(fun _ ->
                    FeedbackService.File feedbackDir) |> ignore

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
                        return Results.File(bytes, mt, dto.Name)
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

                // Feedback / annotation / version / suggestion / candidate / register
                // endpoints — shared with the standalone enhancement test host.
                mapEnhancementEndpoints app workspaceRoot

                // ─── Workspace knowledge base (RAG) ───
                let knowledge = Knowledge.KnowledgeStore(workspaceRoot)
                // The knowledge base is NOT injected into every message. It is exposed only
                // through the search_knowledge tool, which the agent invokes on demand (after
                // asking the user) to consult files the user explicitly uploaded.
                AssistantTools.knowledgeSearch <- Some(fun query topK -> knowledge.Retrieve query topK)

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
