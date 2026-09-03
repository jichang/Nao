namespace Nao.Agents

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks

[<RequireQualifiedAccess>]
module McpJson =
    type EmptyParamsDto() = class end

    type ToolsCapabilityDto() = class end

    type CapabilitiesDto() =
        [<JsonPropertyName("tools")>]
        member val Tools = ToolsCapabilityDto() with get, set

    type InitializeParamsDto() =
        [<JsonPropertyName("capabilities")>]
        member val Capabilities = CapabilitiesDto() with get, set

    type ToolCallParamsDto() =
        [<JsonPropertyName("name")>]
        member val Name: string = null with get, set

        [<JsonPropertyName("arguments")>]
        member val Arguments = JsonElement() with get, set

    type RequestDto() =
        [<JsonPropertyName("jsonrpc")>]
        member val JsonRpc = "2.0" with get, set

        [<JsonPropertyName("id")>]
        member val Id: string = null with get, set

        [<JsonPropertyName("method")>]
        member val Method: string = null with get, set

        [<JsonPropertyName("params")>]
        member val Params: obj = null with get, set

    let serializeRequest id methodName parameters =
        let request = RequestDto()
        request.Id <- id
        request.Method <- methodName
        request.Params <- parameters
        JsonSerializer.Serialize(request)

/// MCP transport type
[<RequireQualifiedAccess>]
type McpTransport =
    /// Standard I/O (stdin/stdout) — for local tool processes
    | Stdio of command: string * args: string list
    /// Server-Sent Events over HTTP
    | Sse of url: Uri
    /// Streamable HTTP (bidirectional)
    | StreamableHttp of url: Uri * headers: Map<string, string>

/// MCP capability flags
[<Flags>]
type McpCapability =
    | None = 0
    | Tools = 1
    | Resources = 2
    | Prompts = 4
    | Sampling = 8
    | Logging = 16

/// MCP server info as advertised during initialization
type McpServerInfo =
    { Name: string
      Version: string
      Capabilities: McpCapability
      ProtocolVersion: string }

/// MCP resource (file, data, etc. exposed by server)
type McpResource =
    { Uri: string
      Name: string
      Description: string option
      MimeType: string option }

/// MCP tool definition as received from a server
type McpToolDef =
    { Name: string
      Description: string option
      InputSchema: string (* JSON Schema as string *)
      Annotations: Map<string, string> }

/// State of an MCP connection
[<RequireQualifiedAccess>]
type McpConnectionState =
    | Disconnected
    | Connecting
    | Connected of McpServerInfo
    | Error of string

/// Functions for an MCP client connection to a single server.
type McpClient =
    { /// Initialize the connection and perform capability negotiation.
      ConnectAsync: unit -> Task<Result<McpServerInfo, string>>
      /// List available tools from the server.
      ListToolsAsync: unit -> Task<McpToolDef list>
      /// List available resources.
      ListResourcesAsync: unit -> Task<McpResource list>
      /// Invoke a tool by name with JSON arguments.
      InvokeToolAsync: string -> string -> Task<Result<string, string>>
      /// Read a resource by URI.
      ReadResourceAsync: string -> Task<Result<string, string>>
      /// Get the current connection state.
      State: unit -> McpConnectionState
      /// Disconnect and cleanup.
      DisconnectAsync: unit -> Task<unit> }

/// Adapts remote MCP definitions to the canonical executable tool boundary.
[<RequireQualifiedAccess>]
module McpTool =
    let create exposedName (client: McpClient) (definition: McpToolDef) =
        let input = ToolCodec.create definition.InputSchema Ok Ok
        let operation =
            ToolOperation.create (fun _ arguments -> task {
                match! client.InvokeToolAsync definition.Name arguments with
                | Ok output -> return Ok output
                | Error message -> return Error(ToolExecError.Failed message) })

        Tool.create
            exposedName
            (definition.Description |> Option.defaultValue "MCP tool")
            0
            [ ResourceAccess.ToolCall exposedName ]
            input
            ToolCodec.text
            operation

/// Functions for a registry of multiple MCP server connections.
type McpRegistry =
    { /// Register a new MCP server.
      RegisterAsync: string -> McpTransport -> Task<Result<McpServerInfo, string>>
      /// Unregister and disconnect a server.
      UnregisterAsync: string -> Task<unit>
      /// Get all registered servers.
      GetServers: unit -> (string * McpConnectionState) list
      /// Get a specific client by server name.
      GetClient: string -> McpClient option
      /// Discover tools from all connected servers.
      DiscoverToolsAsync: unit -> Task<McpToolDef list> }

/// Stdio-based MCP client construction.
[<RequireQualifiedAccess>]
module StdioMcpClient =
    let create (command: string) (args: string list) : McpClient =
        let mutable state = McpConnectionState.Disconnected
        let mutable serverInfo: McpServerInfo option = None
        let mutable proc: System.Diagnostics.Process option = None
        let tools = System.Collections.Concurrent.ConcurrentBag<McpToolDef>()

        let startProcess () =
            let psi = System.Diagnostics.ProcessStartInfo(command)
            for arg in args do
                psi.ArgumentList.Add(arg)
            psi.UseShellExecute <- false
            psi.RedirectStandardInput <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.CreateNoWindow <- true
            let p = System.Diagnostics.Process.Start(psi)
            proc <- Some p
            p

        let sendJsonRpc (p: System.Diagnostics.Process) (methodName: string) (parameters: obj) =
            task {
                let id = Guid.NewGuid().ToString("N").[..7]
                let msg = McpJson.serializeRequest id methodName parameters
                let bytes = System.Text.Encoding.UTF8.GetBytes(msg)
                let header = sprintf "Content-Length: %d\r\n\r\n" bytes.Length
                do! p.StandardInput.WriteAsync(header)
                do! p.StandardInput.WriteAsync(msg)
                do! p.StandardInput.FlushAsync()
                // Read response (simplified — production would handle framing properly)
                let! line = p.StandardOutput.ReadLineAsync()
                return if isNull line then "" else line
            }

        { ConnectAsync = fun () ->
            task {
                try
                    state <- McpConnectionState.Connecting
                    let p = startProcess ()
                    let! _response = sendJsonRpc p "initialize" (McpJson.InitializeParamsDto())
                    let info =
                        { Name = command
                          Version = "1.0"
                          Capabilities = McpCapability.Tools
                          ProtocolVersion = "2025-03-26" }
                    serverInfo <- Some info
                    state <- McpConnectionState.Connected info
                    return Ok info
                with ex ->
                    state <- McpConnectionState.Error ex.Message
                    return Error ex.Message
            }

          ListToolsAsync = fun () ->
            task {
                match proc with
                | Some p when not p.HasExited ->
                    let! _response = sendJsonRpc p "tools/list" (McpJson.EmptyParamsDto())
                    // In production, parse JSON response into McpToolDef list
                    return tools |> Seq.toList
                | _ -> return []
            }

          ListResourcesAsync = fun () -> Task.FromResult([])

          InvokeToolAsync = fun (name: string) (arguments: string) ->
            task {
                match proc with
                | Some p when not p.HasExited ->
                    try
                        use document = JsonDocument.Parse(arguments)
                        let parameters = McpJson.ToolCallParamsDto()
                        parameters.Name <- name
                        parameters.Arguments <- document.RootElement.Clone()
                        let! response = sendJsonRpc p "tools/call" parameters
                        if String.IsNullOrEmpty response then
                            return Error "No response from tool server"
                        else
                            return Ok response
                    with :? JsonException as ex ->
                        return Error(sprintf "Invalid MCP tool arguments: %s" ex.Message)
                | _ -> return Error "MCP server not connected"
            }

          ReadResourceAsync = fun _uri ->
            Task.FromResult(Error "Resources not supported in stdio transport")

          State = fun () -> state

          DisconnectAsync = fun () ->
            task {
                match proc with
                | Some p ->
                    if not p.HasExited then
                        let! _ = sendJsonRpc p "shutdown" (McpJson.EmptyParamsDto())
                        p.Kill()
                    p.Dispose()
                    proc <- None
                | None -> ()
                state <- McpConnectionState.Disconnected
            } }

/// Registry managing multiple MCP connections.
[<RequireQualifiedAccess>]
module McpRegistry =
    let create () : McpRegistry =
        let clients = System.Collections.Concurrent.ConcurrentDictionary<string, McpClient>()

        { RegisterAsync = fun (name: string) (transport: McpTransport) ->
            task {
                let client =
                    match transport with
                    | McpTransport.Stdio (cmd, args) -> StdioMcpClient.create cmd args
                    | McpTransport.Sse _url ->
                        // SSE client would be implemented here
                        StdioMcpClient.create "echo" ["not-implemented"]
                    | McpTransport.StreamableHttp (_url, _headers) ->
                        StdioMcpClient.create "echo" ["not-implemented"]
                let! result = client.ConnectAsync()
                match result with
                | Ok info ->
                    clients.TryAdd(name, client) |> ignore
                    return Ok info
                | Error msg -> return Error msg
            }

          UnregisterAsync = fun name ->
            task {
                match clients.TryRemove(name) with
                | true, client -> do! client.DisconnectAsync()
                | _ -> ()
            }

          GetServers = fun () ->
            clients
            |> Seq.map (fun kvp -> (kvp.Key, kvp.Value.State()))
            |> Seq.toList

          GetClient = fun name ->
            match clients.TryGetValue(name) with
            | true, client -> Some client
            | _ -> None

          DiscoverToolsAsync = fun () ->
            task {
                let results = ResizeArray<McpToolDef>()
                for kvp in clients do
                    let! tools = kvp.Value.ListToolsAsync()
                    results.AddRange(tools)
                return results |> Seq.toList
            } }

    /// Discover connected MCP tools as qualified executable tools (`server.tool`).
    let discoverExecutableToolsAsync (registry: McpRegistry) =
        task {
            let tools = ResizeArray<Tool>()
            for serverName, state in registry.GetServers() do
                match state, registry.GetClient serverName with
                | McpConnectionState.Connected _, Some client ->
                    let! definitions = client.ListToolsAsync()
                    for definition in definitions do
                        tools.Add(McpTool.create (sprintf "%s.%s" serverName definition.Name) client definition)
                | _ -> ()
            return tools |> Seq.toList
        }

    /// Build the canonical invocation protocol over all connected MCP tools.
    let toToolProtocolAsync registry =
        task {
            let! tools = discoverExecutableToolsAsync registry
            return ToolProtocol.fromTools tools
        }
