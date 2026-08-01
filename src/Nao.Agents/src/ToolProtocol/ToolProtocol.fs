namespace Nao.Agents

open System.Threading.Tasks

/// Result of a tool invocation with metadata
type ToolInvocationResult =
    { /// Whether the invocation succeeded
      Success: bool
      /// The output content
      Output: string
      /// Error message if failed
      Error: string option
      /// How long the invocation took in milliseconds
      DurationMs: int64
      /// Whether the tool produced side effects
      HadSideEffects: bool }

/// Middleware that wraps tool execution (pre/post processing)
type IToolMiddleware =
    /// Called before tool execution — can modify input or short-circuit
    abstract member BeforeExecute: string -> string -> Task<Result<string, string>>
    /// Called after tool execution — can modify output
    abstract member AfterExecute: string -> ToolInvocationResult -> Task<ToolInvocationResult>

/// Protocol for tool discovery and invocation (MCP-inspired)
type IToolProtocol =
    /// List all available tools with their schemas
    abstract member ListTools: unit -> Task<ToolSchema list>
    /// Get a specific tool schema by name
    abstract member GetTool: string -> Task<ToolSchema option>
    /// Invoke a tool by name with input
    abstract member InvokeAsync: string -> string -> Task<ToolInvocationResult>
    /// Check if a tool is available and ready
    abstract member IsAvailable: string -> Task<bool>

/// Strategy for selecting which tool to use
[<RequireQualifiedAccess>]
type ToolSelectionStrategy =
    /// Let the LLM decide based on descriptions
    | LlmDriven
    /// Match by keyword patterns
    | PatternMatch of patterns: Map<string, string list>
    /// Custom selection function
    | Custom of selector: (string -> ToolSchema list -> Task<ToolSchema option>)

/// Routes tool invocations through middleware and protocol
module ToolProtocol =
    open System.Diagnostics

    /// Create a protocol from a list of tools
    let fromTools (tools: Tool list) : IToolProtocol =
        let schemas = tools |> List.map ToolSchema.fromTool

        { new IToolProtocol with
            member _.ListTools() = Task.FromResult schemas

            member _.GetTool(name) =
                let (n, ver) = VersionRef.parse name
                schemas
                |> List.tryFind (fun schema -> schema.Name = n && schema.Version = ver)
                |> Task.FromResult

            member _.InvokeAsync (name: string) (input: string) =
                task {
                    let sw = Stopwatch.StartNew()
                    let (n, ver) = VersionRef.parse name
                    match tools |> List.tryFind (fun tool -> tool.Name = n && tool.Version = ver) with
                    | Some tool ->
                        try
                            let! result = tool.InvokeAsync(ToolContext.allowAll, input)
                            sw.Stop()
                            return
                                { Success = true
                                  Output = result
                                  Error = None
                                  DurationMs = sw.ElapsedMilliseconds
                                  HadSideEffects = false }
                        with ex ->
                            sw.Stop()
                            return
                                { Success = false
                                  Output = ""
                                  Error = Some ex.Message
                                  DurationMs = sw.ElapsedMilliseconds
                                  HadSideEffects = false }
                    | None ->
                        sw.Stop()
                        return
                            { Success = false
                              Output = ""
                              Error = Some (sprintf "Tool '%s' not found" name)
                              DurationMs = sw.ElapsedMilliseconds
                              HadSideEffects = false }
                }

            member _.IsAvailable(name: string) =
                let (n, ver) = VersionRef.parse name
                tools |> List.exists (fun tool -> tool.Name = n && tool.Version = ver) |> Task.FromResult }

    /// Wrap a protocol with middleware
    let withMiddleware (middleware: IToolMiddleware) (protocol: IToolProtocol) : IToolProtocol =
        { new IToolProtocol with
            member _.ListTools() = protocol.ListTools()
            member _.GetTool(name) = protocol.GetTool(name)
            member _.IsAvailable(name) = protocol.IsAvailable(name)
            member _.InvokeAsync (name: string) (input: string) =
                task {
                    match! middleware.BeforeExecute name input with
                    | Error msg ->
                        return
                            { Success = false
                              Output = ""
                              Error = Some msg
                              DurationMs = 0L
                              HadSideEffects = false }
                    | Ok modifiedInput ->
                        let! result = protocol.InvokeAsync name modifiedInput
                        return! middleware.AfterExecute name result
                } }

    /// Create a rate-limiting middleware
    let rateLimitMiddleware (maxCallsPerMinute: int) : IToolMiddleware =
        let calls = System.Collections.Concurrent.ConcurrentQueue<System.DateTimeOffset>()
        { new IToolMiddleware with
            member _.BeforeExecute (_name: string) (input: string) =
                task {
                    let now = System.DateTimeOffset.UtcNow
                    let cutoff = now.AddMinutes(-1.0)
                    // Remove old entries
                    let mutable item = System.DateTimeOffset.MinValue
                    while calls.TryPeek(&item) && item < cutoff do
                        calls.TryDequeue(&item) |> ignore
                    if calls.Count >= maxCallsPerMinute then
                        return Error "Rate limit exceeded"
                    else
                        calls.Enqueue(now)
                        return Ok input
                }
            member _.AfterExecute (_name: string) (result: ToolInvocationResult) : Task<ToolInvocationResult> =
                Task.FromResult result }
