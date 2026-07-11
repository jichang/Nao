namespace Nao.Agents

open System
open System.Threading.Tasks

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

/// Interface for an MCP client connection to a single server
type IMcpClient =
    /// Initialize the connection and perform capability negotiation
    abstract member ConnectAsync: unit -> Task<Result<McpServerInfo, string>>
    /// List available tools from the server
    abstract member ListToolsAsync: unit -> Task<McpToolDef list>
    /// List available resources
    abstract member ListResourcesAsync: unit -> Task<McpResource list>
    /// Invoke a tool by name with JSON arguments
    abstract member InvokeToolAsync: name: string -> arguments: string -> Task<Result<string, string>>
    /// Read a resource by URI
    abstract member ReadResourceAsync: uri: string -> Task<Result<string, string>>
    /// Get the current connection state
    abstract member State: McpConnectionState
    /// Disconnect and cleanup
    abstract member DisconnectAsync: unit -> Task<unit>

/// Registry of multiple MCP server connections
type IMcpRegistry =
    /// Register a new MCP server
    abstract member RegisterAsync: name: string -> transport: McpTransport -> Task<Result<McpServerInfo, string>>
    /// Unregister and disconnect a server
    abstract member UnregisterAsync: name: string -> Task<unit>
    /// Get all registered servers
    abstract member GetServers: unit -> (string * McpConnectionState) list
    /// Get a specific client by server name
    abstract member GetClient: name: string -> IMcpClient option
    /// Discover tools from all connected servers
    abstract member DiscoverToolsAsync: unit -> Task<McpToolDef list>

/// Stdio-based MCP client implementation
type StdioMcpClient(command: string, args: string list) =
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

    let sendJsonRpc (p: System.Diagnostics.Process) (method': string) (params': string) =
        task {
            let id = Guid.NewGuid().ToString("N").[..7]
            let msg =
                sprintf """{"jsonrpc":"2.0","id":"%s","method":"%s","params":%s}""" id method' params'
            let bytes = System.Text.Encoding.UTF8.GetBytes(msg)
            let header = sprintf "Content-Length: %d\r\n\r\n" bytes.Length
            do! p.StandardInput.WriteAsync(header)
            do! p.StandardInput.WriteAsync(msg)
            do! p.StandardInput.FlushAsync()
            // Read response (simplified — production would handle framing properly)
            let! line = p.StandardOutput.ReadLineAsync()
            return if isNull line then "" else line
        }

    interface IMcpClient with
        member _.ConnectAsync() =
            task {
                try
                    state <- McpConnectionState.Connecting
                    let p = startProcess ()
                    let! _response = sendJsonRpc p "initialize" """{"capabilities":{"tools":{}}}"""
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

        member _.ListToolsAsync() =
            task {
                match proc with
                | Some p when not p.HasExited ->
                    let! _response = sendJsonRpc p "tools/list" "{}"
                    // In production, parse JSON response into McpToolDef list
                    return tools |> Seq.toList
                | _ -> return []
            }

        member _.ListResourcesAsync() = Task.FromResult([])

        member _.InvokeToolAsync (name: string) (arguments: string) =
            task {
                match proc with
                | Some p when not p.HasExited ->
                    let params' = sprintf """{"name":"%s","arguments":%s}""" name arguments
                    let! response = sendJsonRpc p "tools/call" params'
                    if String.IsNullOrEmpty response then
                        return Error "No response from tool server"
                    else
                        return Ok response
                | _ -> return Error "MCP server not connected"
            }

        member _.ReadResourceAsync(_uri: string) =
            Task.FromResult(Error "Resources not supported in stdio transport")

        member _.State = state

        member _.DisconnectAsync() =
            task {
                match proc with
                | Some p ->
                    if not p.HasExited then
                        let! _ = sendJsonRpc p "shutdown" "{}"
                        p.Kill()
                    p.Dispose()
                    proc <- None
                | None -> ()
                state <- McpConnectionState.Disconnected
            }

/// Registry managing multiple MCP connections
type McpRegistry() =
    let clients = System.Collections.Concurrent.ConcurrentDictionary<string, IMcpClient>()

    interface IMcpRegistry with
        member _.RegisterAsync (name: string) (transport: McpTransport) =
            task {
                let client =
                    match transport with
                    | McpTransport.Stdio (cmd, args) -> StdioMcpClient(cmd, args) :> IMcpClient
                    | McpTransport.Sse _url ->
                        // SSE client would be implemented here
                        StdioMcpClient("echo", ["not-implemented"]) :> IMcpClient
                    | McpTransport.StreamableHttp (_url, _headers) ->
                        StdioMcpClient("echo", ["not-implemented"]) :> IMcpClient
                let! result = client.ConnectAsync()
                match result with
                | Ok info ->
                    clients.TryAdd(name, client) |> ignore
                    return Ok info
                | Error msg -> return Error msg
            }

        member _.UnregisterAsync(name: string) =
            task {
                match clients.TryRemove(name) with
                | true, client -> do! client.DisconnectAsync()
                | _ -> ()
            }

        member _.GetServers() =
            clients
            |> Seq.map (fun kvp -> (kvp.Key, kvp.Value.State))
            |> Seq.toList

        member _.GetClient(name: string) =
            match clients.TryGetValue(name) with
            | true, client -> Some client
            | _ -> None

        member _.DiscoverToolsAsync() =
            task {
                let results = ResizeArray<McpToolDef>()
                for kvp in clients do
                    let! tools = kvp.Value.ListToolsAsync()
                    results.AddRange(tools)
                return results |> Seq.toList
            }
