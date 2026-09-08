namespace Nao.Agents

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading.Tasks

/// Executable environment capability represented as an immutable function record.
type ExecutionEnvironment =
    { ExecuteAsync: ExecutionContext -> AgentContext -> Agent -> string -> Task<Result<string, LimitExceeded>> }

/// Host-owned policy for retaining redacted process output artifacts.
[<NoEquality; NoComparison>]
type ProcessOutputCapture =
    { MaxBytesPerStream: int
      Redact: string -> string }

/// Host-selected process filesystem enforcement implementation.
[<RequireQualifiedAccess>]
type ProcessFileSystemIsolation =
    | Disabled
    | LinuxLandlock

/// Host-selected process identity enforcement implementation.
[<RequireQualifiedAccess>]
type ProcessIdentityIsolation =
    | Disabled
    | LinuxUserNamespace of unshareFileName: string * userId: uint32 * groupId: uint32

/// Host-owned resolver for the secret handles authorized for one process environment.
[<NoEquality; NoComparison>]
type ProcessSecretResolver =
    { Handles: string list
      ResolveAsync: ExecutionContext -> AgentContext -> string -> Task<string option> }

/// Host-owned command used to start one execution worker process.
[<NoEquality; NoComparison>]
type ProcessExecutionEnvironmentConfig =
    { FileName: string
      Arguments: string list
      ProtocolVersion: int
      IdentityIsolation: ProcessIdentityIsolation
      FileSystemIsolation: ProcessFileSystemIsolation
      SecretResolver: ProcessSecretResolver option
      OutputCapture: ProcessOutputCapture option }

module ExecutionEnvironment =
    let private truncateUtf8 maximumBytes (value: string) =
        let normalized = value |> Option.ofObj |> Option.defaultValue ""
        let bytes = Encoding.UTF8.GetBytes normalized

        if bytes.Length <= maximumBytes then
            normalized
        else
            let strictUtf8 = UTF8Encoding(false, true)
            let mutable length = maximumBytes
            let mutable decoded = None

            while decoded.IsNone do
                try
                    decoded <- Some(strictUtf8.GetString(bytes, 0, length))
                with :? DecoderFallbackException ->
                    length <- length - 1

            decoded.Value

    let private readLineAsync
        (stream: Stream)
        (recordOutput: ReadOnlyMemory<byte> -> bool)
        cancellationToken
        : Task<Result<string option, LimitExceeded>> =
        task {
            use buffer = new MemoryStream()
            let next = Array.zeroCreate<byte> 1
            let mutable complete = false
            let mutable endOfStream = false
            let mutable exceeded = false

            while not complete && not endOfStream && not exceeded do
                let! read = stream.ReadAsync(next.AsMemory(), cancellationToken)

                if read = 0 then
                    endOfStream <- true
                elif recordOutput (ReadOnlyMemory<byte>(next, 0, read)) then
                    exceeded <- true
                elif next.[0] = byte '\n' then
                    complete <- true
                elif next.[0] <> byte '\r' then
                    buffer.WriteByte next.[0]

            if exceeded then
                return Error LimitExceeded.Output
            elif endOfStream && buffer.Length = 0L then
                return Ok None
            else
                return Ok(Some(Encoding.UTF8.GetString(buffer.ToArray())))
        }

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

        config.OutputCapture
        |> Option.iter (fun capture ->
            if capture.MaxBytesPerStream <= 0 then
                invalidArg (nameof config) "Process output capture limit must be positive."

            if isNull (box capture.Redact) then
                invalidArg (nameof config) "Process output capture redactor must be provided.")

        config.SecretResolver
        |> Option.iter (fun resolver ->
            if isNull (box resolver.ResolveAsync) then
                invalidArg (nameof config) "Process secret resolver must be provided."

            if resolver.Handles |> List.exists String.IsNullOrWhiteSpace then
                invalidArg (nameof config) "Process secret handles must be non-blank."

            if resolver.Handles.Length <> (resolver.Handles |> List.distinct).Length then
                invalidArg (nameof config) "Process secret handles must be unique.")

        { ExecuteAsync =
            fun ctx agentContext agent input ->
                task {
                    let workerReference =
                        agent.WorkerReference
                        |> Option.defaultWith (fun () ->
                            raise (
                                GovernedExecutionException(
                                    sprintf "Agent '%s' has no process-worker reference." agent.Metadata.Id
                                )
                            ))

                    let enforceFileSystemPolicy, allowedPaths =
                        match config.FileSystemIsolation with
                        | ProcessFileSystemIsolation.Disabled -> false, []
                        | ProcessFileSystemIsolation.LinuxLandlock when not (OperatingSystem.IsLinux()) ->
                            raise (GovernedExecutionException "Linux Landlock filesystem isolation requires Linux.")
                        | ProcessFileSystemIsolation.LinuxLandlock when
                            not (Path.IsPathFullyQualified config.FileName && File.Exists config.FileName)
                            ->
                            raise (
                                GovernedExecutionException(
                                    "Linux Landlock filesystem isolation requires an existing fully qualified worker executable path."
                                )
                            )
                        | ProcessFileSystemIsolation.LinuxLandlock ->
                            let declaredPaths =
                                if ctx.Sandbox.AllowFileSystem then
                                    ctx.Sandbox.AllowedPaths
                                    |> List.map (fun path ->
                                        if not (Path.IsPathFullyQualified path) then
                                            raise (
                                                GovernedExecutionException(
                                                    "Process filesystem allowlist paths must be fully qualified."
                                                )
                                            )

                                        let fullPath = Path.GetFullPath path

                                        if not (File.Exists fullPath || Directory.Exists fullPath) then
                                            raise (
                                                GovernedExecutionException(
                                                    "Process filesystem allowlist path does not exist."
                                                )
                                            )

                                        fullPath)
                                else
                                    []

                            true, declaredPaths |> List.distinct

                    let processFileName, processArguments =
                        match config.IdentityIsolation with
                        | ProcessIdentityIsolation.Disabled -> config.FileName, config.Arguments
                        | ProcessIdentityIsolation.LinuxUserNamespace _ when not (OperatingSystem.IsLinux()) ->
                            raise (GovernedExecutionException "Linux user-namespace identity isolation requires Linux.")
                        | ProcessIdentityIsolation.LinuxUserNamespace(unshareFileName, _, _) when
                            not (
                                Path.IsPathFullyQualified unshareFileName
                                && File.Exists unshareFileName
                                && Path.IsPathFullyQualified config.FileName
                                && File.Exists config.FileName
                            )
                            ->
                            raise (
                                GovernedExecutionException(
                                    "Linux user-namespace identity isolation requires existing fully qualified unshare and worker executable paths."
                                )
                            )
                        | ProcessIdentityIsolation.LinuxUserNamespace(unshareFileName, userId, groupId) ->
                            unshareFileName,
                            [ "--user"
                              sprintf "--map-user=%d" userId
                              sprintf "--map-group=%d" groupId
                              "--"
                              config.FileName ]
                            @ config.Arguments

                    let startInfo = ProcessStartInfo()
                    startInfo.FileName <- processFileName
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

                    for argument in processArguments do
                        startInfo.ArgumentList.Add argument

                    use worker = new Process(StartInfo = startInfo)

                    if not (worker.Start()) then
                        raise (GovernedExecutionException "Execution worker process did not start.")

                    use monitorCancellation = new System.Threading.CancellationTokenSource()
                    let mutable memoryExceeded = 0
                    let mutable cpuExceeded = 0
                    let mutable outputBytes = 0L
                    let mutable outputExceeded = 0
                    use stdoutCapture = new MemoryStream()
                    use stderrCapture = new MemoryStream()

                    let captureOutput (captureBuffer: MemoryStream) (bytes: ReadOnlyMemory<byte>) =
                        config.OutputCapture
                        |> Option.iter (fun capture ->
                            let remaining = int64 capture.MaxBytesPerStream - captureBuffer.Length

                            if remaining > 0L then
                                let count = min bytes.Length (int remaining)
                                captureBuffer.Write(bytes.Span.Slice(0, count)))

                    let recordOutput captureBuffer bytes =
                        captureOutput captureBuffer bytes

                        match ctx.Sandbox.Limits.MaxOutputBytes with
                        | None -> false
                        | Some maximumBytes ->
                            let total = System.Threading.Interlocked.Add(&outputBytes, int64 bytes.Length)

                            if total > maximumBytes then
                                System.Threading.Interlocked.Exchange(&outputExceeded, 1) |> ignore

                                if not worker.HasExited then
                                    worker.Kill(true)

                                true
                            else
                                false

                    let stderrDrain =
                        task {
                            let buffer = Array.zeroCreate<byte> 4096
                            let mutable complete = false

                            try
                                while not complete do
                                    let! read =
                                        worker.StandardError.BaseStream.ReadAsync(
                                            buffer.AsMemory(),
                                            monitorCancellation.Token
                                        )

                                    if read = 0 then
                                        complete <- true
                                    elif recordOutput stderrCapture (ReadOnlyMemory<byte>(buffer, 0, read)) then
                                        complete <- true
                            with
                            | :? OperationCanceledException -> ()
                            | :? IOException when worker.HasExited -> ()
                        }

                    let resourceMonitor =
                        match ctx.Sandbox.Limits.MaxMemoryBytes, ctx.Sandbox.Limits.MaxCpuTime with
                        | None, None -> Task.CompletedTask
                        | maximumMemoryBytes, maximumCpuTime ->
                            task {
                                try
                                    while not monitorCancellation.IsCancellationRequested do
                                        worker.Refresh()

                                        let memoryLimitExceeded =
                                            maximumMemoryBytes
                                            |> Option.exists (fun maximumBytes -> worker.WorkingSet64 > maximumBytes)

                                        let cpuLimitExceeded =
                                            maximumCpuTime
                                            |> Option.exists (fun maximumTime ->
                                                worker.TotalProcessorTime > maximumTime)

                                        if memoryLimitExceeded then
                                            System.Threading.Interlocked.Exchange(&memoryExceeded, 1) |> ignore
                                            worker.Kill(true)
                                            monitorCancellation.Cancel()
                                        elif cpuLimitExceeded then
                                            System.Threading.Interlocked.Exchange(&cpuExceeded, 1) |> ignore
                                            worker.Kill(true)
                                            monitorCancellation.Cancel()
                                        else
                                            do! Task.Delay(TimeSpan.FromMilliseconds 10.0, monitorCancellation.Token)
                                with
                                | :? OperationCanceledException -> ()
                                | :? InvalidOperationException when worker.HasExited -> ()
                            }

                    let fail message : 'Result =
                        raise (GovernedExecutionException message)

                    try
                        try
                            let hello = WorkerProtocol.message WorkerProtocol.Hello config.ProtocolVersion
                            do! worker.StandardInput.WriteLineAsync(WorkerProtocol.serialize hello)
                            do! worker.StandardInput.FlushAsync()

                            let! helloResult =
                                readLineAsync
                                    worker.StandardOutput.BaseStream
                                    (recordOutput stdoutCapture)
                                    ctx.CancellationToken

                            let helloLine =
                                match helloResult with
                                | Error limit -> raise (ExecutionLimitExceededException limit)
                                | Ok line -> line |> Option.toObj

                            if System.Threading.Volatile.Read(&memoryExceeded) = 1 then
                                raise (ExecutionLimitExceededException LimitExceeded.Memory)

                            if System.Threading.Volatile.Read(&cpuExceeded) = 1 then
                                raise (ExecutionLimitExceededException LimitExceeded.Cpu)

                            if System.Threading.Volatile.Read(&outputExceeded) = 1 then
                                raise (ExecutionLimitExceededException LimitExceeded.Output)

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

                            let! secrets =
                                match config.SecretResolver with
                                | None -> Task.FromResult Map.empty
                                | Some resolver ->
                                    task {
                                        let mutable resolved = Map.empty

                                        for handle in resolver.Handles do
                                            match! resolver.ResolveAsync ctx agentContext handle with
                                            | Some secret when not (isNull secret) ->
                                                resolved <- resolved |> Map.add handle secret
                                            | _ ->
                                                fail (
                                                    sprintf
                                                        "Authorized secret handle '%s' could not be resolved."
                                                        handle
                                                )

                                        return resolved
                                    }

                            let request =
                                WorkerProtocol.request
                                    config.ProtocolVersion
                                    (workerReference |> WorkerAgentReference.value)
                                    ctx
                                    enforceFileSystemPolicy
                                    allowedPaths
                                    secrets
                                    input

                            do! worker.StandardInput.WriteLineAsync(WorkerProtocol.serialize request)
                            do! worker.StandardInput.FlushAsync()
                            worker.StandardInput.Close()

                            let! responseResult =
                                readLineAsync
                                    worker.StandardOutput.BaseStream
                                    (recordOutput stdoutCapture)
                                    ctx.CancellationToken

                            let responseLine =
                                match responseResult with
                                | Error limit -> raise (ExecutionLimitExceededException limit)
                                | Ok line -> line |> Option.toObj

                            if System.Threading.Volatile.Read(&memoryExceeded) = 1 then
                                raise (ExecutionLimitExceededException LimitExceeded.Memory)

                            if System.Threading.Volatile.Read(&cpuExceeded) = 1 then
                                raise (ExecutionLimitExceededException LimitExceeded.Cpu)

                            if System.Threading.Volatile.Read(&outputExceeded) = 1 then
                                raise (ExecutionLimitExceededException LimitExceeded.Output)

                            let response =
                                if isNull responseLine then
                                    fail "Execution worker exited without a result."
                                else
                                    match WorkerProtocol.deserialize responseLine with
                                    | Ok message -> message
                                    | Error error -> fail error

                            if response.ProtocolVersion <> config.ProtocolVersion then
                                fail "Execution worker result used an unsupported protocol version."

                            ctx.RecordWorkerUsage(WorkerProtocol.usage response)

                            match response.Kind with
                            | WorkerProtocol.Succeeded -> return Ok response.Output
                            | WorkerProtocol.LimitExceeded ->
                                match WorkerProtocol.tryParseLimit response.Limit with
                                | Some limit -> return Error limit
                                | None -> return fail "Execution worker returned an unknown resource limit."
                            | WorkerProtocol.Failed ->
                                return fail (sprintf "Execution worker failed: %s" response.Error)
                            | kind -> return fail (sprintf "Execution worker returned unexpected message '%s'." kind)
                        with
                        | ExecutionLimitExceededException LimitExceeded.Memory -> return Error LimitExceeded.Memory
                        | ExecutionLimitExceededException LimitExceeded.Cpu -> return Error LimitExceeded.Cpu
                        | ExecutionLimitExceededException LimitExceeded.Output -> return Error LimitExceeded.Output
                        | :? IOException when System.Threading.Volatile.Read(&memoryExceeded) = 1 ->
                            return Error LimitExceeded.Memory
                        | :? IOException when System.Threading.Volatile.Read(&cpuExceeded) = 1 ->
                            return Error LimitExceeded.Cpu
                        | :? IOException when System.Threading.Volatile.Read(&outputExceeded) = 1 ->
                            return Error LimitExceeded.Output
                    finally
                        monitorCancellation.Cancel()

                        if not worker.HasExited then
                            worker.Kill(true)

                        worker.WaitForExit()
                        resourceMonitor.GetAwaiter().GetResult()
                        stderrDrain.GetAwaiter().GetResult()

                        config.OutputCapture
                        |> Option.iter (fun capture ->
                            let publish kind (buffer: MemoryStream) =
                                if buffer.Length > 0L then
                                    let payload =
                                        Encoding.UTF8.GetString(buffer.ToArray())
                                        |> capture.Redact
                                        |> truncateUtf8 capture.MaxBytesPerStream

                                    Artifact.create
                                        (agentContext.NextArtifactKey())
                                        kind
                                        "text/plain; charset=utf-8"
                                        payload
                                    |> agentContext.PublishArtifact
                                    |> _.GetAwaiter().GetResult()

                            publish "process.stdout" stdoutCapture
                            publish "process.stderr" stderrCapture)
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
