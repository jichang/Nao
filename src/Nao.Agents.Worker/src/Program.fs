namespace Nao.Agents.Worker

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Nao.Agents

/// Assembly marker used by hosts and tests to locate the standard worker executable.
type WorkerAssemblyMarker = class end

[<RequireQualifiedAccess>]
module StandardWorkerHandlers =
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
            do! Task.Delay(Int32.Parse request.Input)
            return Ok "completed"
        }

    let processEnvironment (request: WorkerExecutionRequest) =
        Environment.GetEnvironmentVariable request.Input
        |> Option.ofObj
        |> Option.defaultValue "<missing>"
        |> Ok
        |> Task.FromResult

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
                do! child.WaitForExitAsync()
                return Ok "completed"
        }

    let all: Map<string, WorkerAgentHandler> =
        Map.ofList
            [ "process-diagnostic", processDiagnostic
              "process-delay", processDelay
              "process-environment", processEnvironment
              "process-tree", processTree ]

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
                WorkerProtocol.runSingleAsync
                    protocolVersion
                    StandardWorkerHandlers.all
                    Console.In
                    Console.Out
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
            with
            | Ok() -> 0
            | Error error ->
                Console.Error.WriteLine error
                1
