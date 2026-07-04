namespace Nao.Assistant

open System
open System.Buffers
open System.Net.Http
open System.Net.Http.Json
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Nao.Agents

/// Event raised when the server pushes a message.
[<RequireQualifiedAccess>]
type NaoEvent =
    | Chunk of text: string
    | Done of fullResponse: string
    | Info of SessionInfoDto
    | History of MessageDto list
    | Conversations of string list
    | Error of string
    | ServerEvent of string
    /// Server is asking the user to approve a resource access (file/web/tool).
    | PermissionRequest of PermissionRequestDto

/// Client for the Nao Demo Server using HTTP (session creation) + WebSocket (chat & commands).
type NaoClient(baseUrl: string) =
    let http = new HttpClient(BaseAddress = Uri(baseUrl))
    let jsonOptions =
        JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    let mutable ws: ClientWebSocket option = None
    let mutable receiveTask: Task option = None
    let mutable cts = new CancellationTokenSource()

    let onMessage = Event<NaoEvent>()

    let wsUrl =
        let uri = Uri(baseUrl)
        let scheme = if uri.Scheme = "https" then "wss" else "ws"
        sprintf "%s://%s:%d" scheme uri.Host uri.Port

    let ensureSuccess (resp: HttpResponseMessage) = task {
        if not resp.IsSuccessStatusCode then
            let! body = resp.Content.ReadAsStringAsync()
            let error =
                try (JsonSerializer.Deserialize<ErrorResponse>(body, jsonOptions)).Error
                with _ -> body
            failwithf "API error (%d): %s" (int resp.StatusCode) error
    }

    // Treat 2xx as success, 404 as "not found" (false), anything else as an error.
    let okOrNotFound (resp: HttpResponseMessage) = task {
        if resp.IsSuccessStatusCode then return true
        elif resp.StatusCode = System.Net.HttpStatusCode.NotFound then return false
        else
            do! ensureSuccess resp
            return false
    }

    let sendWs (msg: WsRequest) = task {
        match ws with
        | Some socket when socket.State = WebSocketState.Open ->
            let json = JsonSerializer.Serialize(msg, jsonOptions)
            let bytes = Encoding.UTF8.GetBytes(json)
            do! socket.SendAsync(ArraySegment(bytes), WebSocketMessageType.Text, true, cts.Token)
        | _ -> failwith "WebSocket not connected"
    }

    let parseResponse (json: string) =
        try
            let resp = JsonSerializer.Deserialize<WsResponse>(json, jsonOptions)
            match resp.Type with
            | WsResponseType.Chunk -> Some (NaoEvent.Chunk resp.Payload)
            | WsResponseType.Done -> Some (NaoEvent.Done resp.Payload)
            | WsResponseType.Info ->
                let info = JsonSerializer.Deserialize<SessionInfoDto>(resp.Payload, jsonOptions)
                Some (NaoEvent.Info info)
            | WsResponseType.History ->
                let msgs = JsonSerializer.Deserialize<MessageDto[]>(resp.Payload, jsonOptions)
                Some (NaoEvent.History (Array.toList msgs))
            | WsResponseType.Conversations ->
                let convs = JsonSerializer.Deserialize<string[]>(resp.Payload, jsonOptions)
                Some (NaoEvent.Conversations (Array.toList convs))
            | WsResponseType.Error -> Some (NaoEvent.Error resp.Payload)
            | WsResponseType.Event -> Some (NaoEvent.ServerEvent resp.Payload)
            | WsResponseType.PermissionRequest ->
                let req = JsonSerializer.Deserialize<PermissionRequestDto>(resp.Payload, jsonOptions)
                Some (NaoEvent.PermissionRequest req)
            | _ -> None
        with _ -> None

    let receiveLoop () = task {
        let buffer = ArrayPool<byte>.Shared.Rent(8192)
        try
            let mutable looping = true
            while looping && ws.IsSome && ws.Value.State = WebSocketState.Open do
                let segments = ResizeArray<byte>()
                let mutable endOfMessage = false
                while not endOfMessage do
                    let! result = ws.Value.ReceiveAsync(ArraySegment(buffer), cts.Token)
                    if result.MessageType = WebSocketMessageType.Close then
                        do! ws.Value.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                        endOfMessage <- true
                        looping <- false
                    else
                        segments.AddRange(buffer.[0..result.Count - 1])
                        endOfMessage <- result.EndOfMessage

                if looping && segments.Count > 0 then
                    let json = Encoding.UTF8.GetString(segments.ToArray())
                    match parseResponse json with
                    | Some evt -> onMessage.Trigger evt
                    | None -> ()
        with
        | :? OperationCanceledException -> ()
        | :? WebSocketException -> ()

        ArrayPool<byte>.Shared.Return(buffer)
    }

    /// Event stream for server-pushed messages.
    [<CLIEvent>]
    member _.OnMessage = onMessage.Publish

    /// Create a new session via HTTP, returns the session ID.
    member _.CreateSessionAsync(request: SessionStartRequest) : Task<string> = task {
        let! resp = http.PostAsJsonAsync("/api/sessions", request, jsonOptions)
        do! ensureSuccess resp
        let! result = resp.Content.ReadFromJsonAsync<{| sessionId: string |}>(jsonOptions)
        return result.sessionId
    }

    /// List all existing sessions via HTTP.
    member _.ListSessionsAsync() : Task<SessionListEntry list> = task {
        let! resp = http.GetAsync("/api/sessions")
        do! ensureSuccess resp
        let! entries = resp.Content.ReadFromJsonAsync<SessionListEntry[]>(jsonOptions)
        return entries |> Array.toList
    }

    /// Load full conversation history for a session via HTTP.
    member _.LoadSessionHistoryAsync(sessionId: string) : Task<MessageDto list> = task {
        let! resp = http.GetAsync(sprintf "/api/sessions/history/%s" sessionId)
        do! ensureSuccess resp
        let! entries = resp.Content.ReadFromJsonAsync<MessageDto[]>(jsonOptions)
        return entries |> Array.toList
    }

    // ─── Per-session files & async tasks (HTTP) ───

    /// List the files stored for a session (uploaded + tool/agent generated). The
    /// session key ("userId/sessionId") is the trailing catch-all route segment.
    member _.ListSessionFilesAsync(sessionKey: string) : Task<SessionFileDto list> = task {
        let! resp = http.GetAsync(sprintf "/api/sessions/files/%s" sessionKey)
        do! ensureSuccess resp
        let! items = resp.Content.ReadFromJsonAsync<SessionFileDto[]>(jsonOptions)
        return (if isNull (box items) then [] else Array.toList items)
    }

    /// Download a single session file's bytes by id.
    member _.DownloadSessionFileAsync(sessionKey: string, fileId: string) : Task<byte[]> = task {
        let! resp = http.GetAsync(sprintf "/api/sessions/file/%s?fileId=%s" sessionKey (Uri.EscapeDataString fileId))
        do! ensureSuccess resp
        return! resp.Content.ReadAsByteArrayAsync()
    }

    /// List the async tasks for a session (with live status/progress).
    member _.ListSessionTasksAsync(sessionKey: string) : Task<TaskDto list> = task {
        let! resp = http.GetAsync(sprintf "/api/sessions/tasks/%s" sessionKey)
        do! ensureSuccess resp
        let! items = resp.Content.ReadFromJsonAsync<TaskDto[]>(jsonOptions)
        return (if isNull (box items) then [] else Array.toList items)
    }

    // ─── Feedback / suggestion enhancement loop (HTTP) ───

    /// Submit feedback for the most recent turn of a session. Returns the
    /// improvement proposals the server derived from the feedback (if any).
    member _.SubmitFeedbackAsync(sessionId: string, sentiment: string, ?comment: string) : Task<string list> = task {
        let request = { FeedbackRequest.Sentiment = sentiment; Comment = defaultArg comment null }
        let! resp = http.PostAsJsonAsync(sprintf "/api/sessions/feedback/%s" sessionId, request, jsonOptions)
        do! ensureSuccess resp
        let! result = resp.Content.ReadFromJsonAsync<{| proposals: string[] |}>(jsonOptions)
        return (if isNull (box result.proposals) then [] else Array.toList result.proposals)
    }

    /// Register a user-supplied tool definition. Returns the written file path.
    member _.RegisterToolAsync(request: RegisterDefinitionRequest) : Task<string> = task {
        let! resp = http.PostAsJsonAsync("/api/register/tool", request, jsonOptions)
        do! ensureSuccess resp
        let! result = resp.Content.ReadFromJsonAsync<{| registered: bool; path: string |}>(jsonOptions)
        return result.path
    }

    /// Register a user-supplied agent definition. Returns the written file path.
    member _.RegisterAgentAsync(request: RegisterDefinitionRequest) : Task<string> = task {
        let! resp = http.PostAsJsonAsync("/api/register/agent", request, jsonOptions)
        do! ensureSuccess resp
        let! result = resp.Content.ReadFromJsonAsync<{| registered: bool; path: string |}>(jsonOptions)
        return result.path
    }

    /// List the tools available in the workspace (code + JSON definitions).
    member _.ListToolsAsync() : Task<DefinitionInfoDto list> = task {
        let! resp = http.GetAsync("/api/tools")
        do! ensureSuccess resp
        let! items = resp.Content.ReadFromJsonAsync<DefinitionInfoDto[]>(jsonOptions)
        return Array.toList items
    }

    /// Ask the configured LLM to generate a tool definition from a requirement.
    member _.GenerateToolAsync(requirement: string) : Task<GeneratedDefinitionDto> = task {
        let! resp = http.PostAsJsonAsync("/api/tools/generate", { Requirement = requirement }, jsonOptions)
        do! ensureSuccess resp
        return! resp.Content.ReadFromJsonAsync<GeneratedDefinitionDto>(jsonOptions)
    }

    /// List the agent definitions available in the workspace.
    member _.ListAgentsAsync() : Task<DefinitionInfoDto list> = task {
        let! resp = http.GetAsync("/api/agents")
        do! ensureSuccess resp
        let! items = resp.Content.ReadFromJsonAsync<DefinitionInfoDto[]>(jsonOptions)
        return Array.toList items
    }

    /// Ask the configured LLM to generate an agent definition from a requirement.
    member _.GenerateAgentAsync(requirement: string) : Task<GeneratedDefinitionDto> = task {
        let! resp = http.PostAsJsonAsync("/api/agents/generate", { Requirement = requirement }, jsonOptions)
        do! ensureSuccess resp
        return! resp.Content.ReadFromJsonAsync<GeneratedDefinitionDto>(jsonOptions)
    }

    /// Register a tool from a raw JSON string (e.g. an LLM-generated definition).
    member this.RegisterToolJsonAsync(name: string, json: string) : Task<string> =
        let element = JsonSerializer.Deserialize<JsonElement>(json, jsonOptions)
        this.RegisterToolAsync({ Name = name; Definition = element })

    /// Register an agent from a raw JSON string (e.g. an LLM-generated definition).
    member this.RegisterAgentJsonAsync(name: string, json: string) : Task<string> =
        let element = JsonSerializer.Deserialize<JsonElement>(json, jsonOptions)
        this.RegisterAgentAsync({ Name = name; Definition = element })

    /// List uploaded knowledge files with chunk counts.
    member _.ListKnowledgeAsync() : Task<KnowledgeFileDto list> = task {
        let! resp = http.GetAsync("/api/knowledge")
        do! ensureSuccess resp
        let! items = resp.Content.ReadFromJsonAsync<KnowledgeFileDto[]>(jsonOptions)
        return Array.toList items
    }

    /// Upload (or overwrite) a knowledge file's text content into the workspace.
    member _.UploadKnowledgeAsync(name: string, content: string) : Task<unit> = task {
        let! resp = http.PostAsJsonAsync("/api/knowledge", { Name = name; Content = content }, jsonOptions)
        do! ensureSuccess resp
    }

    /// Delete a knowledge file from the workspace.
    member _.DeleteKnowledgeAsync(name: string) : Task<bool> = task {
        let! resp = http.DeleteAsync(sprintf "/api/knowledge/%s" (Uri.EscapeDataString name))
        return! okOrNotFound resp
    }

    /// Connect WebSocket to a session for bidirectional communication.
    member _.ConnectAsync(sessionId: string) : Task = task {
        let socket = new ClientWebSocket()
        let url = sprintf "%s/ws/sessions/%s" wsUrl sessionId
        do! socket.ConnectAsync(Uri(url), cts.Token)
        ws <- Some socket
        receiveTask <- Some (Task.Factory.StartNew(fun () -> receiveLoop().Wait()))
    }

    /// Send a chat message (response comes as Chunk/Done events).
    member _.SendChatAsync(message: string) : Task =
        let request = { Text = message; Attachments = [||] }
        let payload = JsonSerializer.Serialize(request, jsonOptions)
        sendWs { Type = WsRequestType.Chat; Payload = payload }

    /// Send a structured chat message with attached files. The attachment content is sent
    /// to the agent but not rendered back into the transcript.
    member _.SendChatAsync(text: string, attachments: (string * string) list) : Task =
        let request =
            { Text = text
              Attachments =
                attachments
                |> List.map (fun (name, content) -> { AttachmentDto.Name = name; Content = content })
                |> List.toArray }
        let payload = JsonSerializer.Serialize(request, jsonOptions)
        sendWs { Type = WsRequestType.Chat; Payload = payload }

    /// Request session info (response comes as Info event).
    member _.RequestInfoAsync() : Task =
        sendWs { Type = WsRequestType.Info; Payload = "" }

    /// Request conversation history (response comes as History event).
    member _.RequestHistoryAsync() : Task =
        sendWs { Type = WsRequestType.History; Payload = "" }

    /// Clear conversation history.
    member _.SendClearAsync() : Task =
        sendWs { Type = WsRequestType.Clear; Payload = "" }

    /// List conversations.
    member _.RequestConversationsAsync() : Task =
        sendWs { Type = WsRequestType.Conversations; Payload = "" }

    /// Switch conversation.
    member _.SendSwitchAsync(name: string) : Task =
        sendWs { Type = WsRequestType.Switch; Payload = name }

    /// Reply to a server permission prompt. `decision` is "allow" | "deny"; `scope` is
    /// "once" | "session" | "global" (ignored for deny).
    member _.SendPermissionResponseAsync(requestId: string, decision: string, scope: string) : Task =
        let dto = { PermissionResponseDto.RequestId = requestId; Decision = decision; Scope = scope }
        let payload = JsonSerializer.Serialize(dto, jsonOptions)
        sendWs { Type = WsRequestType.PermissionResponse; Payload = payload }

    /// Send a chat message and wait for the full response (blocking convenience method).
    member this.ChatAsync(sessionId: string, message: string) : Task<string> = task {
        return! this.ChatAsync(sessionId, message, [])
    }

    /// Send a chat message with attachments and wait for the full response.
    member this.ChatAsync(sessionId: string, text: string, attachments: (string * string) list) : Task<string> = task {
        if ws.IsNone || ws.Value.State <> WebSocketState.Open then
            do! this.ConnectAsync(sessionId)

        let tcs = TaskCompletionSource<string>()
        let handler = Handler<NaoEvent>(fun _ evt ->
            match evt with
            | NaoEvent.Done response -> tcs.TrySetResult(response) |> ignore
            | NaoEvent.Error err -> tcs.TrySetException(Exception(err)) |> ignore
            | _ -> ())

        this.OnMessage.AddHandler handler
        do! this.SendChatAsync(text, attachments)
        let! result = tcs.Task
        this.OnMessage.RemoveHandler handler
        return result
    }

    /// Send a chat message with attachments and wait for the full response, while invoking
    /// `onSteps` with the live in-progress process steps as the server streams them. The
    /// callback may fire several times (each with the cumulative steps so far) before the
    /// final response is returned.
    member this.ChatAsync(sessionId: string, text: string, attachments: (string * string) list, onSteps: TurnStepDto list -> unit) : Task<string> = task {
        if ws.IsNone || ws.Value.State <> WebSocketState.Open then
            do! this.ConnectAsync(sessionId)

        let tcs = TaskCompletionSource<string>()
        let handler = Handler<NaoEvent>(fun _ evt ->
            match evt with
            | NaoEvent.ServerEvent payload ->
                try
                    let env = JsonSerializer.Deserialize<StepsEventDto>(payload, jsonOptions)
                    if not (isNull (box env)) && not (isNull (box env.Steps)) then
                        onSteps (Array.toList env.Steps)
                with _ -> ()
            | NaoEvent.Done response -> tcs.TrySetResult(response) |> ignore
            | NaoEvent.Error err -> tcs.TrySetException(Exception(err)) |> ignore
            | _ -> ())

        this.OnMessage.AddHandler handler
        do! this.SendChatAsync(text, attachments)
        let! result = tcs.Task
        this.OnMessage.RemoveHandler handler
        return result
    }
    member _.DisconnectAsync() : Task = task {
        match ws with
        | Some socket when socket.State = WebSocketState.Open ->
            do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
            ws <- None
        | _ -> ws <- None
    }

    interface IDisposable with
        member this.Dispose() =
            cts.Cancel()
            match ws with
            | Some socket -> try socket.Dispose() with _ -> ()
            | None -> ()
            http.Dispose()
            cts.Dispose()
