namespace Nao.Agents

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
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
    { References: SecretReference list
      ResolveAsync: ExecutionContext -> AgentContext -> SecretReference -> Task<SecretValue option> }

[<RequireQualifiedAccess>]
module ProcessSecretResolver =

    let fromProvider references provider =
        { References = references
          ResolveAsync =
            fun execution agentContext reference ->
                task {
                    let authorization = AgentContext.requireAuthorization agentContext

                    let! resolution =
                        SecretProvider.resolveAsync authorization reference execution.CancellationToken provider

                    return Some(SecretResolution.value resolution)
                } }

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

/// One explicit host path exposed inside a container.
type ContainerMount =
    { SourcePath: string
      TargetPath: string
      ReadOnly: bool }

/// One explicit host device exposed inside a container.
type ContainerDevice =
    { HostPath: string
      ContainerPath: string
      Permissions: string }

/// One bounded in-memory writable filesystem inside a container.
type ContainerTemporaryStorage = { Path: string; MaxBytes: int64 }

/// Runtime network attachment created by a host-owned destination-enforcing egress broker.
[<NoEquality; NoComparison>]
type ContainerEgressBrokerLease =
    { NetworkName: string
      Claims: Map<string, string>
      Release: unit -> unit }

/// Host-owned egress broker that confines one container execution to declared destinations.
[<NoEquality; NoComparison>]
type ContainerEgressBroker =
    { BrokerId: string
      PolicyVersion: string
      AcquireAsync: ExecutionContext -> AgentContext -> Uri list -> Task<Result<ContainerEgressBrokerLease, string>> }

/// Host-selected network policy for a container execution.
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type ContainerNetworkPolicy =
    | Denied
    | Unrestricted
    | DestinationAllowlist of destinations: Uri list * broker: ContainerEgressBroker

/// Host-owned scanner or attestation verifier for one pinned container image.
[<NoEquality; NoComparison>]
type ContainerImageVerifier =
    { VerifierId: string
      PolicyVersion: string
      VerifyAsync: ExecutionContext -> AgentContext -> string -> Task<Result<Map<string, string>, string>> }

/// Host-owned command used to start one containerized execution worker.
[<NoEquality; NoComparison>]
type ContainerExecutionEnvironmentConfig =
    { RuntimeFileName: string
      RuntimeArguments: string list
      ImageReference: string
      SupplyChainMetadata: Map<string, string>
      ImageVerifier: ContainerImageVerifier
      Mounts: ContainerMount list
      Devices: ContainerDevice list
      TemporaryStorage: ContainerTemporaryStorage list
      NetworkPolicy: ContainerNetworkPolicy
      WorkerCommand: string list
      ProtocolVersion: int
      SecretResolver: ProcessSecretResolver option
      OutputCapture: ProcessOutputCapture option }

type ContainerHostCapabilities =
    { RuntimeVersion: string
      OperatingSystem: string
      CgroupVersion: string option }

[<RequireQualifiedAccess>]
type ContainerHostSupport =
    | Supported of ContainerHostCapabilities
    | Unsupported of reasons: string list

exception ContainerHostUnsupportedException of reasons: string list

module ExecutionEnvironment =
    let private requireWorkerAuthorization (context: AgentContext) =
        if
            context.ExecutionBoundary = ExecutionBoundary.HarnessRequired
            && context.Authorization.IsNone
        then
            raise (UnauthorizedAccessException "Protected worker execution requires a host-authenticated principal.")

    type ContainerImageVerificationEvidence =
        { VerifierId: string
          PolicyVersion: string
          Claims: Map<string, string> }

    type ContainerImageEvidence =
        { ImageReference: string
          Digest: string
          SupplyChain: Map<string, string>
          Verification: ContainerImageVerificationEvidence }

    type ContainerNetworkEvidence =
        { BrokerId: string
          PolicyVersion: string
          Destinations: string list
          Claims: Map<string, string> }

    let private jsonOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

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

    let private publishCapturedOutput
        (capture: ProcessOutputCapture)
        (agentContext: AgentContext)
        kind
        (buffer: MemoryStream)
        =
        if buffer.Length > 0L then
            let payload =
                Encoding.UTF8.GetString(buffer.ToArray())
                |> capture.Redact
                |> truncateUtf8 capture.MaxBytesPerStream

            Artifact.create (agentContext.NextArtifactKey()) kind "text/plain; charset=utf-8" payload
            |> agentContext.PublishArtifact
            |> _.GetAwaiter().GetResult()

    let detectContainerHost (config: ContainerExecutionEnvironmentConfig) =
        if
            String.IsNullOrWhiteSpace config.RuntimeFileName
            || not (
                Path.IsPathFullyQualified config.RuntimeFileName
                && File.Exists config.RuntimeFileName
            )
        then
            ContainerHostSupport.Unsupported [ "Container runtime executable is unavailable." ]
        else
            try
                let startInfo = ProcessStartInfo()
                startInfo.FileName <- config.RuntimeFileName
                startInfo.UseShellExecute <- false
                startInfo.CreateNoWindow <- true
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                startInfo.Environment.Clear()

                for argument in config.RuntimeArguments @ [ "info"; "--format"; "{{json .}}" ] do
                    startInfo.ArgumentList.Add argument

                use probe = new Process(StartInfo = startInfo)

                if not (probe.Start()) then
                    ContainerHostSupport.Unsupported [ "Container runtime capability probe did not start." ]
                else
                    let output = probe.StandardOutput.ReadToEndAsync()
                    let error = probe.StandardError.ReadToEndAsync()

                    if not (probe.WaitForExit(10000)) then
                        probe.Kill(true)
                        probe.WaitForExit()
                        ContainerHostSupport.Unsupported [ "Container runtime capability probe timed out." ]
                    elif probe.ExitCode <> 0 then
                        let detail = error.GetAwaiter().GetResult().Trim()

                        ContainerHostSupport.Unsupported
                            [ if String.IsNullOrWhiteSpace detail then
                                  "Container runtime daemon is unavailable."
                              else
                                  "Container runtime daemon is unavailable: " + detail ]
                    else
                        use document = JsonDocument.Parse(output.GetAwaiter().GetResult())
                        let root = document.RootElement

                        let tryString (name: string) =
                            let mutable value = Unchecked.defaultof<JsonElement>

                            if root.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then
                                value.GetString() |> Option.ofObj
                            else
                                None

                        let tryBoolean (name: string) =
                            let mutable value = Unchecked.defaultof<JsonElement>

                            if root.TryGetProperty(name, &value) then
                                match value.ValueKind with
                                | JsonValueKind.True -> Some true
                                | JsonValueKind.False -> Some false
                                | _ -> None
                            else
                                None

                        let runtimeVersion = tryString "ServerVersion" |> Option.defaultValue "unknown"
                        let operatingSystem = tryString "OSType" |> Option.defaultValue "unknown"
                        let cgroupVersion = tryString "CgroupVersion"

                        let reasons =
                            [ if not (String.Equals(operatingSystem, "linux", StringComparison.OrdinalIgnoreCase)) then
                                  "Container runtime must provide Linux containers."

                              if tryBoolean "MemoryLimit" <> Some true then
                                  "Container runtime does not report memory-limit support."

                              if tryBoolean "PidsLimit" <> Some true then
                                  "Container runtime does not report process-count limit support." ]

                        if reasons.IsEmpty then
                            ContainerHostSupport.Supported
                                { RuntimeVersion = runtimeVersion
                                  OperatingSystem = operatingSystem
                                  CgroupVersion = cgroupVersion }
                        else
                            ContainerHostSupport.Unsupported reasons
            with error ->
                ContainerHostSupport.Unsupported [ "Container runtime capability probe failed: " + error.Message ]

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

            if resolver.References.Length <> (resolver.References |> List.distinct).Length then
                invalidArg (nameof config) "Process secret references must be unique.")

        { ExecuteAsync =
            fun ctx agentContext agent input ->
                task {
                    requireWorkerAuthorization agentContext

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

                                        for reference in resolver.References do
                                            match! resolver.ResolveAsync ctx agentContext reference with
                                            | Some secret when SecretValue.reference secret = reference ->
                                                resolved <-
                                                    resolved
                                                    |> Map.add
                                                        (SecretReference.value reference)
                                                        (SecretValue.useValue id secret)
                                            | Some _ ->
                                                fail (
                                                    sprintf
                                                        "Secret resolver returned a value for a different reference than '%s'."
                                                        (SecretReference.value reference)
                                                )
                                            | _ ->
                                                fail (
                                                    sprintf
                                                        "Authorized secret reference '%s' could not be resolved."
                                                        (SecretReference.value reference)
                                                )

                                        return resolved
                                    }

                            let request =
                                WorkerProtocol.request
                                    config.ProtocolVersion
                                    (workerReference |> WorkerAgentReference.value)
                                    ctx
                                    agentContext
                                    enforceFileSystemPolicy
                                    allowedPaths
                                    secrets
                                    (SecretRedaction.redact input)

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
                            | WorkerProtocol.Succeeded -> return Ok(SecretRedaction.redact response.Output)
                            | WorkerProtocol.LimitExceeded ->
                                match WorkerProtocol.tryParseLimit response.Limit with
                                | Some limit -> return Error limit
                                | None -> return fail "Execution worker returned an unknown resource limit."
                            | WorkerProtocol.Failed ->
                                return
                                    fail (sprintf "Execution worker failed: %s" (SecretRedaction.redact response.Error))
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
                        | :? IOException as error ->
                            return fail (sprintf "Execution worker protocol IO failed: %s" error.Message)
                    finally
                        monitorCancellation.Cancel()

                        if not worker.HasExited then
                            worker.Kill(true)

                        worker.WaitForExit()
                        resourceMonitor.GetAwaiter().GetResult()
                        stderrDrain.GetAwaiter().GetResult()

                        config.OutputCapture
                        |> Option.iter (fun capture ->
                            publishCapturedOutput capture agentContext "process.stdout" stdoutCapture
                            publishCapturedOutput capture agentContext "process.stderr" stderrCapture)
                } }

    /// Execute a worker-resolvable agent in a fresh Docker-compatible container.
    let containerWorker (config: ContainerExecutionEnvironmentConfig) : ExecutionEnvironment =
        let digestMarker = "@sha256:"

        let digestMarkerIndex =
            config.ImageReference.LastIndexOf(digestMarker, StringComparison.Ordinal)

        let imageDigest =
            if digestMarkerIndex <= 0 then
                ""
            else
                config.ImageReference.Substring(digestMarkerIndex + digestMarker.Length)

        if imageDigest.Length <> 64 || not (imageDigest |> Seq.forall Uri.IsHexDigit) then
            invalidArg (nameof config) "Container image reference must be pinned by a SHA-256 digest."

        if
            config.SupplyChainMetadata
            |> Map.exists (fun key value -> String.IsNullOrWhiteSpace key || String.IsNullOrWhiteSpace value)
        then
            invalidArg (nameof config) "Container supply-chain metadata keys and values must be non-blank."

        if
            String.IsNullOrWhiteSpace config.ImageVerifier.VerifierId
            || String.IsNullOrWhiteSpace config.ImageVerifier.PolicyVersion
        then
            invalidArg (nameof config) "Container image verifier and policy identities must be non-blank."

        match config.NetworkPolicy with
        | ContainerNetworkPolicy.Denied
        | ContainerNetworkPolicy.Unrestricted -> ()
        | ContainerNetworkPolicy.DestinationAllowlist(destinations, broker) ->
            if destinations.IsEmpty then
                invalidArg (nameof config) "Container destination allowlists must not be empty."

            if
                destinations
                |> List.exists (fun destination ->
                    not destination.IsAbsoluteUri
                    || (destination.Scheme <> Uri.UriSchemeHttp
                        && destination.Scheme <> Uri.UriSchemeHttps)
                    || String.IsNullOrWhiteSpace destination.Host
                    || not (String.IsNullOrEmpty destination.UserInfo))
            then
                invalidArg
                    (nameof config)
                    "Container destinations must be absolute HTTP(S) URIs without user information."

            if
                destinations.Length
                <> (destinations |> List.map _.AbsoluteUri |> List.distinct).Length
            then
                invalidArg (nameof config) "Container destinations must be unique."

            if
                String.IsNullOrWhiteSpace broker.BrokerId
                || String.IsNullOrWhiteSpace broker.PolicyVersion
            then
                invalidArg (nameof config) "Container egress broker and policy identities must be non-blank."

            if isNull (box broker.AcquireAsync) then
                invalidArg (nameof config) "Container egress broker acquisition function must be provided."

        let isAbsoluteContainerPath (path: string) =
            not (String.IsNullOrWhiteSpace path)
            && path.StartsWith("/", StringComparison.Ordinal)

        config.Mounts
        |> List.iter (fun mount ->
            if not (Path.IsPathFullyQualified mount.SourcePath) then
                invalidArg (nameof config) "Container mount source paths must be fully qualified."

            if not (File.Exists mount.SourcePath || Directory.Exists mount.SourcePath) then
                invalidArg (nameof config) "Container mount source paths must exist."

            if mount.SourcePath.Contains(',') || mount.TargetPath.Contains(',') then
                invalidArg (nameof config) "Container mount paths cannot contain commas."

            if not (isAbsoluteContainerPath mount.TargetPath) then
                invalidArg (nameof config) "Container mount target paths must be absolute.")

        config.Devices
        |> List.iter (fun device ->
            if not (Path.IsPathFullyQualified device.HostPath && File.Exists device.HostPath) then
                invalidArg (nameof config) "Container device host paths must be existing fully qualified files."

            if not (isAbsoluteContainerPath device.ContainerPath) then
                invalidArg (nameof config) "Container device paths must be absolute."

            if device.HostPath.Contains(':') || device.ContainerPath.Contains(':') then
                invalidArg (nameof config) "Container device paths cannot contain colons."

            if
                String.IsNullOrWhiteSpace device.Permissions
                || device.Permissions
                   |> Seq.exists (fun permission -> permission <> 'r' && permission <> 'w' && permission <> 'm')
                || device.Permissions.Length <> (device.Permissions |> Seq.distinct |> Seq.length)
            then
                invalidArg (nameof config) "Container device permissions may contain only r, w, and m.")

        config.TemporaryStorage
        |> List.iter (fun storage ->
            if not (isAbsoluteContainerPath storage.Path) then
                invalidArg (nameof config) "Container temporary-storage paths must be absolute."

            if storage.Path.Contains(':') || storage.Path.Contains(',') then
                invalidArg (nameof config) "Container temporary-storage paths cannot contain colons or commas."

            if storage.MaxBytes <= 0L then
                invalidArg (nameof config) "Container temporary-storage limits must be positive.")

        let targets =
            (config.Mounts |> List.map _.TargetPath)
            @ (config.Devices |> List.map _.ContainerPath)
            @ (config.TemporaryStorage |> List.map _.Path)

        if targets.Length <> (targets |> List.distinct).Length then
            invalidArg (nameof config) "Container mount, device, and temporary-storage targets must be unique."

        match detectContainerHost config with
        | ContainerHostSupport.Supported _ -> ()
        | ContainerHostSupport.Unsupported reasons -> raise (ContainerHostUnsupportedException reasons)

        { ExecuteAsync =
            fun ctx agentContext agent input ->
                task {
                    requireWorkerAuthorization agentContext

                    let containerIdentifier = Guid.NewGuid().ToString("N")
                    let containerName = "nao-" + containerIdentifier

                    let containerIdPath =
                        Path.Combine(Path.GetTempPath(), "nao-container-" + containerIdentifier + ".cid")

                    let environmentArguments =
                        ctx.Sandbox.EnvironmentVariables
                        |> Map.toList
                        |> List.collect (fun (name, _) -> [ "--env"; name ])

                    let workingDirectoryArguments =
                        ctx.Sandbox.WorkingDirectory
                        |> Option.map (fun directory ->
                            if not (isAbsoluteContainerPath directory) then
                                raise (GovernedExecutionException "Container working directory must be absolute.")

                            [ "--workdir"; directory ])
                        |> Option.defaultValue []

                    let mountArguments =
                        config.Mounts
                        |> List.collect (fun mount ->
                            let readOnly = if mount.ReadOnly then ",readonly" else ""

                            [ "--mount"
                              sprintf "type=bind,src=%s,dst=%s%s" mount.SourcePath mount.TargetPath readOnly ])

                    let deviceArguments =
                        config.Devices
                        |> List.collect (fun device ->
                            [ "--device"
                              sprintf "%s:%s:%s" device.HostPath device.ContainerPath device.Permissions ])

                    let temporaryStorageArguments =
                        config.TemporaryStorage
                        |> List.collect (fun storage ->
                            [ "--tmpfs"
                              sprintf
                                  "%s:rw,noexec,nosuid,nodev,size=%d,uid=65532,gid=65532,mode=0700"
                                  storage.Path
                                  storage.MaxBytes ])

                    let staticNetworkArguments =
                        match ctx.Sandbox.AllowNetwork, config.NetworkPolicy with
                        | false, _
                        | true, ContainerNetworkPolicy.Denied -> Some [ "--network=none" ]
                        | true, ContainerNetworkPolicy.Unrestricted -> Some []
                        | true, ContainerNetworkPolicy.DestinationAllowlist _ -> None

                    let memoryArguments =
                        ctx.Sandbox.Limits.MaxMemoryBytes
                        |> Option.map (fun maximumBytes ->
                            if maximumBytes <= 0L then
                                raise (GovernedExecutionException "Container memory limit must be positive.")

                            [ sprintf "--memory=%d" maximumBytes; sprintf "--memory-swap=%d" maximumBytes ])
                        |> Option.defaultValue []

                    let cpuArguments =
                        ctx.Sandbox.Limits.MaxCpuTime
                        |> Option.map (fun maximumTime ->
                            if maximumTime <= TimeSpan.Zero then
                                raise (GovernedExecutionException "Container CPU-time limit must be positive.")

                            let seconds = max 1L (int64 (Math.Ceiling maximumTime.TotalSeconds))
                            [ sprintf "--ulimit=cpu=%d:%d" seconds seconds ])
                        |> Option.defaultValue []

                    let processArguments =
                        ctx.Sandbox.Limits.MaxProcessCount
                        |> Option.map (fun maximumProcesses ->
                            if maximumProcesses <= 0 then
                                raise (GovernedExecutionException "Container process limit must be positive.")

                            [ sprintf "--pids-limit=%d" maximumProcesses ])
                        |> Option.defaultValue []

                    let! verificationClaims =
                        task {
                            match! config.ImageVerifier.VerifyAsync ctx agentContext config.ImageReference with
                            | Error reason ->
                                return
                                    raise (GovernedExecutionException("Container image verification failed: " + reason))
                            | Ok claims when
                                claims.IsEmpty
                                || (claims
                                    |> Map.exists (fun key value ->
                                        String.IsNullOrWhiteSpace key || String.IsNullOrWhiteSpace value))
                                ->
                                return
                                    raise (
                                        GovernedExecutionException(
                                            "Container image verification returned no valid claims."
                                        )
                                    )
                            | Ok claims -> return claims
                        }

                    let! egressLease =
                        task {
                            match ctx.Sandbox.AllowNetwork, config.NetworkPolicy with
                            | true, ContainerNetworkPolicy.DestinationAllowlist(destinations, broker) ->
                                match! broker.AcquireAsync ctx agentContext destinations with
                                | Error reason ->
                                    return
                                        raise (GovernedExecutionException("Container egress broker failed: " + reason))
                                | Ok lease when isNull (box lease.Release) ->
                                    return
                                        raise (
                                            GovernedExecutionException(
                                                "Container egress broker returned no lease release function."
                                            )
                                        )
                                | Ok lease when
                                    String.IsNullOrWhiteSpace lease.NetworkName
                                    || lease.NetworkName.StartsWith("-", StringComparison.Ordinal)
                                    || lease.NetworkName |> Seq.exists Char.IsWhiteSpace
                                    || [ "bridge"; "default"; "host"; "none" ]
                                       |> List.exists (fun builtIn ->
                                           String.Equals(
                                               lease.NetworkName,
                                               builtIn,
                                               StringComparison.OrdinalIgnoreCase
                                           ))
                                    ->
                                    lease.Release()

                                    return
                                        raise (
                                            GovernedExecutionException(
                                                "Container egress broker returned an invalid dedicated network name."
                                            )
                                        )
                                | Ok lease when
                                    lease.Claims.IsEmpty
                                    || (lease.Claims
                                        |> Map.exists (fun key value ->
                                            String.IsNullOrWhiteSpace key || String.IsNullOrWhiteSpace value))
                                    ->
                                    lease.Release()

                                    return
                                        raise (
                                            GovernedExecutionException(
                                                "Container egress broker returned no valid enforcement claims."
                                            )
                                        )
                                | Ok lease -> return Some lease
                            | _ -> return None
                        }

                    let networkArguments =
                        staticNetworkArguments
                        |> Option.defaultWith (fun () -> [ "--network"; egressLease.Value.NetworkName ])

                    let releaseEgressLease () =
                        egressLease |> Option.iter (fun lease -> lease.Release())

                    let arguments =
                        config.RuntimeArguments
                        @ [ "run"
                            "--rm"
                            "--interactive"
                            "--cidfile"
                            containerIdPath
                            "--name"
                            containerName
                            "--user"
                            "65532:65532"
                            "--read-only"
                            "--cap-drop=ALL"
                            "--security-opt=no-new-privileges" ]
                        @ networkArguments
                        @ memoryArguments
                        @ cpuArguments
                        @ processArguments
                        @ environmentArguments
                        @ workingDirectoryArguments
                        @ mountArguments
                        @ deviceArguments
                        @ temporaryStorageArguments
                        @ [ config.ImageReference ]
                        @ config.WorkerCommand

                    let imageEvidence =
                        { ImageReference = config.ImageReference
                          Digest = "sha256:" + imageDigest
                          SupplyChain = config.SupplyChainMetadata
                          Verification =
                            { VerifierId = config.ImageVerifier.VerifierId
                              PolicyVersion = config.ImageVerifier.PolicyVersion
                              Claims = verificationClaims } }

                    let imageArtifact =
                        try
                            JsonSerializer.Serialize(imageEvidence, jsonOptions)
                            |> Artifact.create (agentContext.NextArtifactKey()) "container.image" "application/json"
                        with error ->
                            releaseEgressLease ()
                            reraise ()

                    let networkArtifact =
                        try
                            match config.NetworkPolicy, egressLease with
                            | ContainerNetworkPolicy.DestinationAllowlist(destinations, broker), Some lease ->
                                let evidence: ContainerNetworkEvidence =
                                    { BrokerId = broker.BrokerId
                                      PolicyVersion = broker.PolicyVersion
                                      Destinations = destinations |> List.map _.AbsoluteUri
                                      Claims = lease.Claims }

                                JsonSerializer.Serialize(evidence, jsonOptions)
                                |> Artifact.create
                                    (agentContext.NextArtifactKey())
                                    "container.network"
                                    "application/json"
                                |> Some
                            | _ -> None
                        with _ ->
                            releaseEgressLease ()
                            reraise ()

                    let environment =
                        try
                            processWorker
                                { FileName = config.RuntimeFileName
                                  Arguments = arguments
                                  ProtocolVersion = config.ProtocolVersion
                                  IdentityIsolation = ProcessIdentityIsolation.Disabled
                                  FileSystemIsolation = ProcessFileSystemIsolation.Disabled
                                  SecretResolver = config.SecretResolver
                                  OutputCapture = config.OutputCapture }
                        with error ->
                            releaseEgressLease ()
                            reraise ()

                    let containerContext =
                        { ctx with
                            Sandbox =
                                { ctx.Sandbox with
                                    WorkingDirectory = None } }

                    try
                        do! agentContext.PublishArtifact imageArtifact

                        match networkArtifact with
                        | Some artifact -> do! agentContext.PublishArtifact artifact
                        | None -> ()

                        return! environment.ExecuteAsync containerContext agentContext agent input
                    finally
                        try
                            let cleanupStartInfo = ProcessStartInfo()
                            cleanupStartInfo.FileName <- config.RuntimeFileName
                            cleanupStartInfo.UseShellExecute <- false
                            cleanupStartInfo.CreateNoWindow <- true
                            cleanupStartInfo.RedirectStandardOutput <- true
                            cleanupStartInfo.RedirectStandardError <- true
                            cleanupStartInfo.Environment.Clear()

                            for argument in config.RuntimeArguments @ [ "rm"; "--force"; "--volumes"; containerName ] do
                                cleanupStartInfo.ArgumentList.Add argument

                            use cleanup = new Process(StartInfo = cleanupStartInfo)
                            cleanup.Start() |> ignore
                            cleanup.WaitForExit()
                        finally
                            try
                                File.Delete containerIdPath
                            finally
                                releaseEgressLease ()
                } }

    let tryContainerWorker config =
        try
            Ok(containerWorker config)
        with ContainerHostUnsupportedException reasons ->
            Error reasons

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
