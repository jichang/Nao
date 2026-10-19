namespace Nao.Eval

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open FSharp.SystemTextJson
open Nao.Agents

/// Durable evaluation data. Every operation is scoped by host-authenticated authority.
type EvalArchive =
    { SaveDatasetAsync: AuthorizationScope -> EvalDataset -> Task
      SaveReportAsync: AuthorizationScope -> EvalReport -> Task
      GetDatasetAsync: AuthorizationScope -> Guid -> Task<EvalDataset option>
      GetReportsAsync: AuthorizationScope -> Guid -> int -> Task<EvalReport list>
      GetResultsByExecutionAsync: AuthorizationScope -> ExecutionId -> Task<EvalResult list>
      DeleteOwnerAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: AuthorizationScope -> DateTimeOffset -> Task<Result<int, PlatformFailure>> }

[<RequireQualifiedAccess>]
type private EvalArchiveEvent =
    | SaveDataset of EvalDataset
    | SaveReport of EvalReport
    | DeleteOwner of AuthorizationIdentity
    | DeleteExpired of AuthorizationIdentity * DateTimeOffset

type private EvalArchiveDocument =
    { Version: int
      Event: EvalArchiveEvent }

module private EvalArchiveState =
    let failure (error: exn) =
        PlatformFailure.fromException PlatformFailureBoundary.Storage None error

    let validateReport (report: EvalReport) =
        if report.SchemaVersion <> EvalReport.CurrentSchemaVersion then
            invalidArg (nameof report) "Unsupported evaluation report schema version."

        if report.Results |> List.exists (fun result -> result.Owner <> report.Owner) then
            invalidArg (nameof report) "Every evaluation result must match its report owner."

        if
            report.Results
            |> List.exists (fun result -> result.DatasetId <> report.DatasetId)
        then
            invalidArg (nameof report) "Every evaluation result must match its report dataset."

        if report.Results |> List.exists (fun result -> result.RunId <> report.Id) then
            invalidArg (nameof report) "Every evaluation result must match its report run."

    let create persist initialEvents : EvalArchive =
        let gate = obj ()
        let datasets = Dictionary<AuthorizationIdentity * Guid, EvalDataset>()
        let reports = Dictionary<AuthorizationIdentity * Guid, EvalReport>()

        let delete owner before =
            let mutable deleted = 0

            for dataset in datasets.Values |> Seq.toArray do
                if EvalDataset.owner dataset = owner && before (EvalDataset.createdAt dataset) then
                    if datasets.Remove((EvalDataset.owner dataset, EvalDataset.id dataset)) then
                        deleted <- deleted + 1

            for report in reports.Values |> Seq.toArray do
                if report.Owner = owner && before report.RunAt then
                    if reports.Remove((report.Owner, report.Id)) then
                        deleted <- deleted + 1

            deleted

        let apply event =
            match event with
            | EvalArchiveEvent.SaveDataset dataset ->
                datasets.[(EvalDataset.owner dataset, EvalDataset.id dataset)] <- dataset
            | EvalArchiveEvent.SaveReport report ->
                validateReport report

                match datasets.TryGetValue((report.Owner, report.DatasetId)) with
                | true, dataset when
                    EvalDataset.revision dataset = report.DatasetRevision
                    && EvalDataset.contentSha256 dataset = report.DatasetContentSha256
                    ->
                    ()
                | true, _ ->
                    invalidArg
                        (nameof report)
                        "Evaluation report dataset revision and content hash must match the archived dataset."
                | false, _ ->
                    invalidArg (nameof report) "Evaluation reports require their exact dataset revision to be archived."

                reports.[(report.Owner, report.Id)] <- report
            | EvalArchiveEvent.DeleteOwner owner -> delete owner (fun _ -> true) |> ignore
            | EvalArchiveEvent.DeleteExpired(owner, cutoff) ->
                delete owner (fun timestamp -> timestamp < cutoff) |> ignore

        initialEvents |> Seq.iter apply

        let saveDatasetAsync authorization (dataset: EvalDataset) =
            task {
                AuthorizationScope.requireOwner (EvalDataset.owner dataset) authorization

                lock gate (fun () ->
                    let event = EvalArchiveEvent.SaveDataset dataset
                    persist event
                    apply event)
            }
            :> Task

        let saveReportAsync authorization (report: EvalReport) =
            task {
                AuthorizationScope.requireOwner report.Owner authorization
                validateReport report

                lock gate (fun () ->
                    let event = EvalArchiveEvent.SaveReport report
                    persist event
                    apply event)
            }
            :> Task

        let deleteAsync authorization before event =
            task {
                let owner = AuthorizationScope.identity authorization

                try
                    return
                        lock gate (fun () ->
                            persist (event owner)
                            delete owner before)
                        |> Ok
                with
                | :? IOException as error -> return Error(failure error)
                | :? UnauthorizedAccessException as error -> return Error(failure error)
            }

        { SaveDatasetAsync = saveDatasetAsync
          SaveReportAsync = saveReportAsync
          GetDatasetAsync =
            fun authorization id ->
                Task.FromResult(
                    lock gate (fun () ->
                        datasets.TryGetValue((AuthorizationScope.identity authorization, id))
                        |> function
                            | true, value -> Some value
                            | false, _ -> None)
                )
          GetReportsAsync =
            fun authorization datasetId limit ->
                let owner = AuthorizationScope.identity authorization

                Task.FromResult(
                    lock gate (fun () ->
                        reports.Values
                        |> Seq.filter (fun report -> report.Owner = owner && report.DatasetId = datasetId)
                        |> Seq.sortByDescending (fun report -> report.RunAt)
                        |> Seq.truncate limit
                        |> Seq.toList)
                )
          GetResultsByExecutionAsync =
            fun authorization executionId ->
                let owner = AuthorizationScope.identity authorization

                Task.FromResult(
                    lock gate (fun () ->
                        reports.Values
                        |> Seq.filter (fun report -> report.Owner = owner)
                        |> Seq.collect _.Results
                        |> Seq.filter (fun result -> result.ExecutionId = executionId)
                        |> Seq.sortByDescending _.Timestamp
                        |> Seq.toList)
                )
          DeleteOwnerAsync = fun authorization -> deleteAsync authorization (fun _ -> true) EvalArchiveEvent.DeleteOwner
          DeleteExpiredAsync =
            fun authorization cutoff ->
                deleteAsync authorization (fun timestamp -> timestamp < cutoff) (fun owner ->
                    EvalArchiveEvent.DeleteExpired(owner, cutoff)) }

module EvalArchives =
    let inMemory () =
        EvalArchiveState.create ignore Seq.empty

    let file path =
        let options = JsonSerializerOptions(WriteIndented = false)

        options.Converters.Add(
            JsonFSharpConverter(JsonUnionEncoding.InternalTag ||| JsonUnionEncoding.UnwrapFieldlessTags)
        )

        let deserialize (line: string) =
            let document = JsonSerializer.Deserialize<EvalArchiveDocument>(line, options)

            if document.Version <> 1 then
                invalidOp (sprintf "Unsupported evaluation archive document version %d." document.Version)

            document.Event

        let initialEvents =
            if File.Exists path then
                File.ReadLines(path) |> Seq.map deserialize |> Seq.toArray
            else
                Array.empty

        let directory = Path.GetDirectoryName path

        if not (String.IsNullOrWhiteSpace directory) then
            Directory.CreateDirectory directory |> ignore

        let gate = obj ()

        let persist event =
            let document = { Version = 1; Event = event }
            let line = JsonSerializer.Serialize(document, options)
            lock gate (fun () -> File.AppendAllText(path, line + Environment.NewLine))

        EvalArchiveState.create persist initialEvents
