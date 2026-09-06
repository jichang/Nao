namespace Nao.Agents

open System.Threading.Tasks

/// Result of a tool invocation with metadata
type ToolInvocationResult =
    {
        /// Whether the invocation succeeded
        Success: bool
        /// The output content
        Output: string
        /// Error message if failed
        Error: string option
        /// Structured failure returned by the executable tool boundary
        Failure: ToolFailure option
        /// How long the invocation took in milliseconds
        DurationMs: int64
        /// Whether the tool produced side effects
        HadSideEffects: bool
    }

/// Middleware functions that wrap tool execution (pre/post processing).
type ToolMiddleware =
    {
        /// Called before tool execution — can modify input or short-circuit.
        BeforeExecute: string -> string -> Task<Result<string, ToolFailure>>
        /// Called after tool execution — can modify output.
        AfterExecute: string -> ToolInvocationResult -> Task<ToolInvocationResult>
    }

/// Functions for tool discovery and invocation (MCP-inspired).
type ToolProtocol =
    {
        /// List all available tools.
        ListTools: unit -> Task<Tool list>
        /// Get a specific tool by name.
        GetTool: string -> Task<Tool option>
        /// Invoke a tool by name with input.
        InvokeAsync: AgentContext -> string -> string -> Task<ToolInvocationResult>
        /// Check if a tool is available and ready.
        IsAvailable: string -> Task<bool>
    }

/// Routes tool invocations through middleware and protocol
module ToolProtocol =
    open System.Diagnostics

    let private failed duration failure =
        { Success = false
          Output = ""
          Error = Some failure.Message
          Failure = Some failure
          DurationMs = duration
          HadSideEffects = false }

    /// Create a protocol from a list of tools
    let fromTools (tools: Tool list) : ToolProtocol =
        { ListTools = fun () -> Task.FromResult tools

          GetTool = fun name -> tools |> List.tryFind (fun tool -> tool.Name = name) |> Task.FromResult

          InvokeAsync =
            fun (context: AgentContext) (name: string) (input: string) ->
                task {
                    let sw = Stopwatch.StartNew()

                    match tools |> List.tryFind (fun tool -> tool.Name = name) with
                    | Some tool ->
                        try
                            let! result = ExecutionRuntime.runTool context tool input
                            sw.Stop()

                            match result with
                            | Ok output ->
                                return
                                    { Success = true
                                      Output = output
                                      Error = None
                                      Failure = None
                                      DurationMs = sw.ElapsedMilliseconds
                                      HadSideEffects = false }
                            | Error failure ->
                                return failed sw.ElapsedMilliseconds (ToolFailure.ofPlatformFailure failure)
                        with
                        | ExecutionLimitExceededException limit -> return raise (ExecutionLimitExceededException limit)
                        | :? System.OperationCanceledException as error -> return raise error
                        | ex ->
                            sw.Stop()

                            let failure =
                                PlatformFailure.fromException PlatformFailureBoundary.Tool None ex
                                |> ToolFailure.ofPlatformFailure

                            return failed sw.ElapsedMilliseconds failure
                    | None ->
                        sw.Stop()

                        return
                            failed
                                sw.ElapsedMilliseconds
                                ({ Kind = ToolFailureKind.InputContract
                                   Message = sprintf "Tool '%s' not found" name
                                   Retryable = false }
                                : ToolFailure)
                }

          IsAvailable = fun name -> tools |> List.exists (fun tool -> tool.Name = name) |> Task.FromResult }

    /// Wrap a protocol with middleware
    let withMiddleware (middleware: ToolMiddleware) (protocol: ToolProtocol) : ToolProtocol =
        { ListTools = protocol.ListTools
          GetTool = protocol.GetTool
          IsAvailable = protocol.IsAvailable
          InvokeAsync =
            fun (context: AgentContext) (name: string) (input: string) ->
                task {
                    match! middleware.BeforeExecute name input with
                    | Error failure -> return failed 0L failure
                    | Ok modifiedInput ->
                        let! result = protocol.InvokeAsync context name modifiedInput
                        return! middleware.AfterExecute name result
                } }

    /// Create a rate-limiting middleware
    let rateLimitMiddleware (maxCallsPerMinute: int) : ToolMiddleware =
        let calls = System.Collections.Concurrent.ConcurrentQueue<System.DateTimeOffset>()

        { BeforeExecute =
            fun (_name: string) (input: string) ->
                task {
                    let now = System.DateTimeOffset.UtcNow
                    let cutoff = now.AddMinutes(-1.0)
                    // Remove old entries
                    let mutable item = System.DateTimeOffset.MinValue

                    while calls.TryPeek(&item) && item < cutoff do
                        calls.TryDequeue(&item) |> ignore

                    if calls.Count >= maxCallsPerMinute then
                        return
                            Error
                                { Kind = ToolFailureKind.ResourceExhausted
                                  Message = "Rate limit exceeded"
                                  Retryable = true }
                    else
                        calls.Enqueue(now)
                        return Ok input
                }
          AfterExecute = fun (_name: string) (result: ToolInvocationResult) -> Task.FromResult result }
