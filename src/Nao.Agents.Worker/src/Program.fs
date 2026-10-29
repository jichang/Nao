namespace Nao.Agents.Worker

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Nao.Agents

/// Assembly marker used by hosts and tests to locate the standard worker executable.
type WorkerAssemblyMarker = class end

[<RequireQualifiedAccess>]
module private LinuxLandlock =
    [<Literal>]
    let private CreateRuleset = 444L

    [<Literal>]
    let private AddRule = 445L

    [<Literal>]
    let private RestrictSelf = 446L

    let private CreateRulesetVersion = 1n

    let private PathBeneathRule = 1n

    [<Literal>]
    let private PrSetNoNewPrivileges = 38

    [<Literal>]
    let private OpenPath = 0x200000

    [<Literal>]
    let private OpenCloseOnExec = 0x80000

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private RulesetAttribute =
        val mutable HandledAccessFileSystem: uint64

        new(handledAccessFileSystem) = { HandledAccessFileSystem = handledAccessFileSystem }

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private PathBeneathAttribute =
        val mutable AllowedAccess: uint64
        val mutable ParentFileDescriptor: int
        val mutable Reserved: uint32

        new(allowedAccess, parentFileDescriptor) =
            { AllowedAccess = allowedAccess
              ParentFileDescriptor = parentFileDescriptor
              Reserved = 0u }

    [<DllImport("libc", EntryPoint = "syscall", SetLastError = true)>]
    extern int64 private systemCall(
        int64 number,
        nativeint argument1,
        nativeint argument2,
        nativeint argument3,
        nativeint argument4,
        nativeint argument5,
        nativeint argument6
    )

    [<DllImport("libc", SetLastError = true)>]
    extern int private prctl(int option, uint64 argument2, uint64 argument3, uint64 argument4, uint64 argument5)

    [<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
    extern int private openPath(string path, int flags)

    [<DllImport("libc", EntryPoint = "close", SetLastError = true)>]
    extern int private closeFileDescriptor(int fileDescriptor)

    let private lastErrorMessage operation =
        let error = Marshal.GetLastPInvokeError()
        sprintf "Linux Landlock %s failed with errno %d." operation error

    let private lastError operation = Error(lastErrorMessage operation)

    let private withStructure<'Value, 'Result> (value: 'Value) (action: nativeint -> 'Result) : 'Result =
        let pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof<'Value>))

        try
            Marshal.StructureToPtr(value, pointer, false)
            action pointer
        finally
            Marshal.FreeHGlobal pointer

    let private accessRights abi =
        let version1 = (1UL <<< 13) - 1UL
        let version2 = if abi >= 2L then 1UL <<< 13 else 0UL
        let version3 = if abi >= 3L then 1UL <<< 14 else 0UL
        let version5 = if abi >= 5L then 1UL <<< 15 else 0UL
        version1 ||| version2 ||| version3 ||| version5

    let private fileAccessRights handledRights =
        let fileRights =
            (1UL <<< 0) ||| (1UL <<< 1) ||| (1UL <<< 2) ||| (1UL <<< 14) ||| (1UL <<< 15)

        handledRights &&& fileRights

    let private runtimeAccessRights handledRights =
        let runtimeRights = (1UL <<< 0) ||| (1UL <<< 2) ||| (1UL <<< 3)
        handledRights &&& runtimeRights

    let restrict (request: WorkerExecutionRequest) =
        if not request.EnforceFileSystemPolicy then
            Ok()
        elif not (OperatingSystem.IsLinux()) then
            Error "Linux Landlock filesystem isolation requires Linux."
        else
            CultureInfo.GetCultureInfo("en-US").CompareInfo.Compare("a", "b") |> ignore

            let abi = systemCall (CreateRuleset, 0n, 0n, CreateRulesetVersion, 0n, 0n, 0n)

            if abi < 1L then
                lastError "ABI query"
            else
                let handledRights = accessRights abi
                let rulesetAttribute = RulesetAttribute handledRights

                withStructure rulesetAttribute (fun rulesetPointer ->
                    let rulesetFileDescriptor =
                        systemCall (
                            CreateRuleset,
                            rulesetPointer,
                            nativeint (Marshal.SizeOf(typeof<RulesetAttribute>)),
                            0n,
                            0n,
                            0n,
                            0n
                        )
                        |> int

                    if rulesetFileDescriptor < 0 then
                        lastError "ruleset creation"
                    else
                        try
                            let mutable failure = None

                            let runtimePaths =
                                [ AppContext.BaseDirectory; RuntimeEnvironment.GetRuntimeDirectory() ]
                                |> List.map Path.GetFullPath
                                |> List.distinct

                            let allowedPaths = runtimePaths @ request.AllowedPaths |> List.distinct

                            for path in allowedPaths do
                                if failure.IsNone then
                                    let pathFileDescriptor = openPath (path, OpenPath ||| OpenCloseOnExec)

                                    if pathFileDescriptor < 0 then
                                        failure <- Some(lastErrorMessage "allowlist path open")
                                    else
                                        try
                                            let allowedRights =
                                                if runtimePaths |> List.contains path then
                                                    runtimeAccessRights handledRights
                                                elif Directory.Exists path then
                                                    handledRights
                                                else
                                                    fileAccessRights handledRights

                                            let pathAttribute = PathBeneathAttribute(allowedRights, pathFileDescriptor)

                                            let addResult =
                                                withStructure pathAttribute (fun pathPointer ->
                                                    systemCall (
                                                        AddRule,
                                                        nativeint rulesetFileDescriptor,
                                                        PathBeneathRule,
                                                        pathPointer,
                                                        0n,
                                                        0n,
                                                        0n
                                                    ))

                                            if addResult < 0L then
                                                failure <- Some(lastErrorMessage "allowlist rule addition")
                                        finally
                                            closeFileDescriptor pathFileDescriptor |> ignore

                            match failure with
                            | Some error -> Error error
                            | None when prctl (PrSetNoNewPrivileges, 1UL, 0UL, 0UL, 0UL) <> 0 ->
                                lastError "no-new-privileges setup"
                            | None ->
                                let restrictResult =
                                    systemCall (RestrictSelf, nativeint rulesetFileDescriptor, 0n, 0n, 0n, 0n, 0n)

                                if restrictResult < 0L then
                                    lastError "self restriction"
                                else
                                    Ok()
                        finally
                            closeFileDescriptor rulesetFileDescriptor |> ignore)

[<RequireQualifiedAccess>]
module StandardWorkerHandlers =
    [<DllImport("libc", EntryPoint = "geteuid")>]
    extern uint32 private getEffectiveUserId()

    [<DllImport("libc", EntryPoint = "getegid")>]
    extern uint32 private getEffectiveGroupId()

    let processDiagnostic (request: WorkerExecutionRequest) =
        sprintf
            "%d|%s|%s|%s|%d"
            Environment.ProcessId
            request.Input
            request.ExecutionId
            request.CorrelationId
            request.Attempt
        |> Ok
        |> Task.FromResult

    let processDelay (request: WorkerExecutionRequest) =
        task {
            do! Task.Delay(Int32.Parse request.Input, request.CancellationToken)
            return Ok "completed"
        }

    let processCpu (request: WorkerExecutionRequest) =
        task {
            let duration = TimeSpan.FromMilliseconds(float (Int32.Parse request.Input))
            let started = Stopwatch.StartNew()
            let mutable value = 0

            while started.Elapsed < duration do
                request.CancellationToken.ThrowIfCancellationRequested()
                value <- value + 1

            return Ok(string value)
        }

    let processBudget (request: WorkerExecutionRequest) =
        let fields = request.Input.Split('|')

        match fields.[0] with
        | "llm" ->
            request.Budget.BeginLlmCall()
            request.Budget.RecordLlmUsage (Int32.Parse fields.[1]) (Decimal.Parse fields.[2])
        | "llm-call" -> request.Budget.BeginLlmCall()
        | "llm-usage" -> request.Budget.RecordLlmUsage (Int32.Parse fields.[1]) (Decimal.Parse fields.[2])
        | "embedding-call" -> request.Budget.BeginEmbeddingCall()
        | "embedding-usage" -> request.Budget.RecordEmbeddingUsage (Int32.Parse fields.[1]) (Decimal.Parse fields.[2])
        | "tool-call" -> request.Budget.BeginToolCall()
        | operation -> invalidArg (nameof request) (sprintf "Unknown budget operation '%s'." operation)

        Ok "completed" |> Task.FromResult

    let processEnvironment (request: WorkerExecutionRequest) =
        Environment.GetEnvironmentVariable request.Input
        |> Option.ofObj
        |> Option.defaultValue "<missing>"
        |> Ok
        |> Task.FromResult

    let processIdentity (_request: WorkerExecutionRequest) =
        sprintf "%d|%d" (getEffectiveUserId ()) (getEffectiveGroupId ())
        |> Ok
        |> Task.FromResult

    let processSecretHash (request: WorkerExecutionRequest) =
        request.Input
        |> SecretReference.parse
        |> request.Secrets.Resolve
        |> SecretValue.useValue id
        |> Text.Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> Ok
        |> Task.FromResult

    let processPrincipalHash (request: WorkerExecutionRequest) =
        sprintf "%A" request.Principal
        |> Text.Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> Ok
        |> Task.FromResult

    let processEcho (request: WorkerExecutionRequest) = Ok request.Input |> Task.FromResult

    let processWorkingDirectory (_request: WorkerExecutionRequest) =
        Environment.CurrentDirectory |> Ok |> Task.FromResult

    let processOutput (request: WorkerExecutionRequest) =
        String('x', Int32.Parse request.Input) |> Ok |> Task.FromResult

    let processStandardError (request: WorkerExecutionRequest) =
        task {
            do! Console.Error.WriteAsync(String('x', Int32.Parse request.Input))
            return Ok "completed"
        }

    let processStandardErrorText (request: WorkerExecutionRequest) =
        task {
            do! Console.Error.WriteAsync(request.Input)
            return Ok "completed"
        }

    let processReadFile (request: WorkerExecutionRequest) =
        try
            File.ReadAllText request.Input
        with :? UnauthorizedAccessException ->
            "<denied>"
        |> Ok
        |> Task.FromResult

    let processFileSystemProbe (request: WorkerExecutionRequest) =
        let paths = request.Input.Split('\n')
        let declaredContent = File.ReadAllText paths.[0]

        let undeclaredResult =
            try
                File.ReadAllText paths.[1]
            with :? UnauthorizedAccessException ->
                "<denied>"

        declaredContent + "|" + undeclaredResult |> Ok |> Task.FromResult

    let processTree (request: WorkerExecutionRequest) =
        task {
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- Environment.ProcessPath
            startInfo.UseShellExecute <- false
            startInfo.ArgumentList.Add "--child-delay"
            startInfo.ArgumentList.Add "30000"
            use child = new Process(StartInfo = startInfo)

            if not (child.Start()) then
                return Error "Child process did not start."
            else
                let pendingPath = request.Input + ".pending"
                File.WriteAllText(pendingPath, string child.Id)
                File.Move(pendingPath, request.Input)
                do! child.WaitForExitAsync(request.CancellationToken)
                return Ok "completed"
        }

    let all: Map<string, WorkerAgentHandler> =
        Map.ofList
            [ ("process-diagnostic", processDiagnostic)
              ("process-budget", processBudget)
              ("process-cpu", processCpu)
              ("process-delay", processDelay)
              ("process-environment", processEnvironment)
              ("process-echo", processEcho)
              ("process-filesystem-probe", processFileSystemProbe)
              ("process-identity", processIdentity)
              ("process-principal-hash", processPrincipalHash)
              ("process-output", processOutput)
              ("process-read-file", processReadFile)
              ("process-secret-hash", processSecretHash)
              ("process-stderr", processStandardError)
              ("process-stderr-text", processStandardErrorText)
              ("process-working-directory", processWorkingDirectory)
              ("process-tree", processTree) ]

    let restricted: Map<string, WorkerAgentHandler> =
        all
        |> Map.map (fun _ handler request ->
            task {
                match LinuxLandlock.restrict request with
                | Error error -> return Error error
                | Ok() ->
                    try
                        return! handler request
                    with
                    | :? ExecutionLimitExceededException as error -> return raise error
                    | _ -> return Error "Worker execution failed under filesystem policy."
            })

module Program =
    [<EntryPoint>]
    let main arguments =
        match arguments |> Array.tryFindIndex ((=) "--child-delay") with
        | Some index when index + 1 < arguments.Length ->
            Thread.Sleep(Int32.Parse arguments.[index + 1])
            0
        | _ ->
            let protocolVersion =
                match arguments |> Array.tryFindIndex ((=) "--protocol-version") with
                | Some index when index + 1 < arguments.Length -> Int32.Parse arguments.[index + 1]
                | _ -> WorkerProtocol.CurrentVersion

            match
                WorkerProtocol.runSingleProcessAsync
                    protocolVersion
                    StandardWorkerHandlers.restricted
                    Console.In
                    Console.Out
                |> _.GetAwaiter().GetResult()
            with
            | Ok() -> 0
            | Error error ->
                Console.Error.WriteLine error
                1
