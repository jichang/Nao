namespace Nao.Agents.Worker

open System
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

    let all: Map<string, WorkerAgentHandler> =
        Map.ofList [ "process-diagnostic", processDiagnostic ]

module Program =
    [<EntryPoint>]
    let main arguments =
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
