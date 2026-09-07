namespace Nao.Agents

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks

/// Executable environment capability represented as an immutable function record.
type ExecutionEnvironment =
    { ExecuteAsync: ExecutionContext -> AgentContext -> Agent -> string -> Task<Result<string, LimitExceeded>> }

/// Host-owned command used to start one execution worker process.
type ProcessExecutionEnvironmentConfig =
    { FileName: string
      Arguments: string list
      ProtocolVersion: int }

module ExecutionEnvironment =
    /// Create a local (in-process) execution environment
    let local () : ExecutionEnvironment =
        { ExecuteAsync =
            fun ctx agentContext agent input ->
                task {
                    if ctx.CancellationToken.IsCancellationRequested then
                        return raise (OperationCanceledException(ctx.CancellationToken))
                    else
                        match ctx.CheckLimits() with
                        | Some exceeded -> return Error exceeded
                        | None ->
                            let! result = agent.Execute agentContext input |> _.WaitAsync(ctx.CancellationToken)

                            if ctx.CancellationToken.IsCancellationRequested then
                                return raise (OperationCanceledException(ctx.CancellationToken))
                            else
                                match ctx.CheckLimits() with
                                | Some exceeded -> return Error exceeded
                                | None -> return Ok result
                } }

    /// Execute a worker-resolvable agent in a fresh child process over the versioned stdio protocol.
    let processWorker (config: ProcessExecutionEnvironmentConfig) : ExecutionEnvironment =
        if String.IsNullOrWhiteSpace config.FileName then
            invalidArg (nameof config) "Worker executable path must be non-blank."

        { ExecuteAsync =
            fun ctx _ agent input ->
                task {
                    let workerReference =
                        agent.WorkerReference
                        |> Option.defaultWith (fun () ->
                            raise (
                                GovernedExecutionException(
                                    sprintf "Agent '%s' has no process-worker reference." agent.Metadata.Id
                                )
                            ))

                    let startInfo = ProcessStartInfo()
                    startInfo.FileName <- config.FileName
                    startInfo.UseShellExecute <- false
                    startInfo.RedirectStandardInput <- true
                    startInfo.RedirectStandardOutput <- true
                    startInfo.RedirectStandardError <- true
                    startInfo.CreateNoWindow <- true
                    startInfo.Environment.Clear()

                    match ctx.Sandbox.WorkingDirectory with
                    | Some directory when not (Path.IsPathFullyQualified directory) ->
                        raise (GovernedExecutionException "Process working directory must be fully qualified.")
                    | Some directory when not (Directory.Exists directory) ->
                        raise (GovernedExecutionException "Process working directory does not exist.")
                    | Some directory -> startInfo.WorkingDirectory <- Path.GetFullPath directory
                    | None -> ()

                    for KeyValue(name, value) in ctx.Sandbox.EnvironmentVariables do
                        startInfo.Environment.Add(name, value)

                    for argument in config.Arguments do
                        startInfo.ArgumentList.Add argument

                    use worker = new Process(StartInfo = startInfo)

                    if not (worker.Start()) then
                        raise (GovernedExecutionException "Execution worker process did not start.")

                    let fail message : 'Result =
                        raise (GovernedExecutionException message)

                    try
                        let hello = WorkerProtocol.message WorkerProtocol.Hello config.ProtocolVersion
                        do! worker.StandardInput.WriteLineAsync(WorkerProtocol.serialize hello)
                        do! worker.StandardInput.FlushAsync()

                        let! helloLine = worker.StandardOutput.ReadLineAsync(ctx.CancellationToken)

                        let helloResponse =
                            if isNull helloLine then
                                fail "Execution worker exited before the protocol handshake."
                            else
                                match WorkerProtocol.deserialize helloLine with
                                | Ok message -> message
                                | Error error -> fail error

                        if
                            helloResponse.Kind <> WorkerProtocol.Hello
                            || helloResponse.ProtocolVersion <> config.ProtocolVersion
                        then
                            fail (
                                sprintf
                                    "Execution worker protocol mismatch: host=%d worker=%d."
                                    config.ProtocolVersion
                                    helloResponse.ProtocolVersion
                            )

                        let request =
                            WorkerProtocol.request
                                config.ProtocolVersion
                                (workerReference |> WorkerAgentReference.value)
                                ctx
                                input

                        do! worker.StandardInput.WriteLineAsync(WorkerProtocol.serialize request)
                        do! worker.StandardInput.FlushAsync()
                        worker.StandardInput.Close()

                        let! responseLine = worker.StandardOutput.ReadLineAsync(ctx.CancellationToken)

                        let response =
                            if isNull responseLine then
                                fail "Execution worker exited without a result."
                            else
                                match WorkerProtocol.deserialize responseLine with
                                | Ok message -> message
                                | Error error -> fail error

                        if response.ProtocolVersion <> config.ProtocolVersion then
                            fail "Execution worker result used an unsupported protocol version."

                        match response.Kind with
                        | WorkerProtocol.Succeeded -> return Ok response.Output
                        | WorkerProtocol.Failed -> return fail (sprintf "Execution worker failed: %s" response.Error)
                        | kind -> return fail (sprintf "Execution worker returned unexpected message '%s'." kind)
                    finally
                        if not worker.HasExited then
                            worker.Kill(true)

                        worker.WaitForExit()
                } }

    /// Execute with timeout wrapping
    let executeWithTimeout
        (env: ExecutionEnvironment)
        (ctx: ExecutionContext)
        (agentContext: AgentContext)
        (agent: Agent)
        (input: string)
        : Task<Result<string, LimitExceeded>> =
        task {
            let timeout = ctx.Sandbox.Limits.MaxDuration
            use deadline = new System.Threading.CancellationTokenSource(timeout)

            use cts =
                System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken, deadline.Token)

            let linkedCtx =
                { ctx with
                    CancellationToken = cts.Token }

            let linkedAgentContext =
                { agentContext with
                    CancellationToken = cts.Token }

            try
                return! env.ExecuteAsync linkedCtx linkedAgentContext agent input
            with
            | :? OperationCanceledException when ctx.CancellationToken.IsCancellationRequested ->
                return raise (OperationCanceledException(ctx.CancellationToken))
            | :? OperationCanceledException -> return Error LimitExceeded.Duration
        }
