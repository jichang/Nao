namespace Nao.Knowledge.Documents.Worker

open System
open System.Threading
open Nao.Agents
open Nao.Knowledge.Documents

/// Assembly marker used by hosts and tests to locate the document parser worker.
type DocumentParserWorkerAssemblyMarker = class end

[<RequireQualifiedAccess>]
module DocumentParserWorkerHandler =
    let handle (request: WorkerExecutionRequest) =
        task {
            let parserRequest = DocumentParserWorkerProtocol.deserializeRequest request.Input

            let parser =
                DocumentParserRuntime.structured parserRequest.PackageReadLimits
                |> List.tryFind (fun parser -> parser.Descriptor.Parser.ComponentId = parserRequest.ParserId)

            match parser with
            | None -> return Error "Requested document parser is not registered."
            | Some parser ->
                let authorization =
                    request.Principal
                    |> Option.bind (fun principal ->
                        AuthorizationScope.tryCreate
                            principal
                            (parserRequest.GroupId |> Option.map GroupId.parse)
                            (WorkspaceId.parse parserRequest.WorkspaceId)
                            (parserRequest.SessionId |> Option.map SessionId.parse))
                    |> Option.defaultWith (fun () ->
                        raise (UnauthorizedAccessException "Parser worker requires a valid authorization scope."))

                let! response =
                    parser.ParseAsync authorization parserRequest.Content parserRequest.FileName CancellationToken.None

                return Ok(DocumentParserWorkerProtocol.serializeResponse response)
        }

module Program =
    [<EntryPoint>]
    let main _ =
        let handlers =
            Map.ofList [ DocumentParserWorkerProtocol.WorkerId, DocumentParserWorkerHandler.handle ]

        match
            WorkerProtocol.runSingleAsync
                WorkerProtocol.CurrentVersion
                handlers
                System.Console.In
                System.Console.Out
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
        with
        | Ok() -> 0
        | Error error ->
            System.Console.Error.WriteLine error
            1
