namespace Nao.Eval

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open FSharp.SystemTextJson
open Nao.Agents

/// Durable datasets and their derived evaluation reports.
type EvalArchive =
    { SaveDatasetAsync: EvalDataset -> Task
      SaveReportAsync: EvalReport -> Task
      GetDatasetAsync: string -> Guid -> Task<EvalDataset option>
      GetReportsAsync: string -> Guid -> int -> Task<EvalReport list>
      GetResultsByExecutionAsync: string -> ExecutionId -> Task<EvalResult list>
      DeleteOwnerAsync: string -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: string -> DateTimeOffset -> Task<Result<int, PlatformFailure>> }

[<RequireQualifiedAccess>]
type private EvalArchiveEvent =
    | SaveDataset of EvalDataset
    | SaveReport of EvalReport
    | DeleteOwner of string
    | DeleteExpired of string * DateTimeOffset

type private EvalArchiveDocument =
    { Version: int
      Event: EvalArchiveEvent }

module private EvalArchiveState =
    let failure = PlatformFailure.fromException PlatformFailureBoundary.Storage None

    let validateOwner owner =
        if String.IsNullOrWhiteSpace owner then
            invalidArg (nameof owner) "Evaluation archive owner cannot be blank."

    let validateReport (report: EvalReport) =
        validateOwner report.Owner

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
        let datasets = ConcurrentDictionary<string * Guid, EvalDataset>()
        let reports = ConcurrentDictionary<string * Guid, EvalReport>()

        let delete owner before =
            let mutable deleted = 0

            for dataset in datasets.Values do
                if dataset.Owner = owner && before dataset.CreatedAt then
                    match datasets.TryRemove((dataset.Owner, dataset.Id)) with
                    | true, _ -> deleted <- deleted + 1
                    | false, _ -> ()

            for report in reports.Values do
                if report.Owner = owner && before report.RunAt then
                    match reports.TryRemove((report.Owner, report.Id)) with
                    | true, _ -> deleted <- deleted + 1
                    | false, _ -> ()

            deleted

        let apply event =
            match event with
            | EvalArchiveEvent.SaveDataset dataset -> datasets.[(dataset.Owner, dataset.Id)] <- dataset
            | EvalArchiveEvent.SaveReport report -> reports.[(report.Owner, report.Id)] <- report
            | EvalArchiveEvent.DeleteOwner owner -> delete owner (fun _ -> true) |> ignore
            | EvalArchiveEvent.DeleteExpired(owner, cutoff) ->
                delete owner (fun timestamp -> timestamp < cutoff) |> ignore

        initialEvents |> Seq.iter apply

        let saveDatasetAsync (dataset: EvalDataset) =
            validateOwner dataset.Owner
            let event = EvalArchiveEvent.SaveDataset dataset
            apply event
            persist event
            Task.CompletedTask

        let saveReportAsync report =
            validateReport report
            let event = EvalArchiveEvent.SaveReport report
            apply event
            persist event
            Task.CompletedTask

        let protect owner operation =
            task {
                if String.IsNullOrWhiteSpace owner then
                    return
                        Error(
                            PlatformFailure.create
                                PlatformErrorCategory.InvalidInput
                                "Evaluation archive owner cannot be blank."
                                false
                                None
                        )
                else
                    try
                        let count = operation ()
                        return Ok count
                    with ex ->
                        return Error(failure ex)
            }

        let deleteOwnerAsync owner =
            protect owner (fun () ->
                let count = delete owner (fun _ -> true)
                persist (EvalArchiveEvent.DeleteOwner owner)
                count)

        let deleteExpiredAsync owner cutoff =
            protect owner (fun () ->
                let count = delete owner (fun timestamp -> timestamp < cutoff)
                persist (EvalArchiveEvent.DeleteExpired(owner, cutoff))
                count)

        { SaveDatasetAsync = saveDatasetAsync
          SaveReportAsync = saveReportAsync
          GetDatasetAsync =
            fun owner id ->
                Task.FromResult(
                    datasets.TryGetValue((owner, id))
                    |> function
                        | true, value -> Some value
                        | false, _ -> None
                )
          GetReportsAsync =
            fun owner datasetId limit ->
                Task.FromResult(
                    reports.Values
                    |> Seq.filter (fun report -> report.Owner = owner && report.DatasetId = datasetId)
                    |> Seq.sortByDescending (fun report -> report.RunAt)
                    |> Seq.truncate limit
                    |> Seq.toList
                )
          GetResultsByExecutionAsync =
            fun owner executionId ->
                Task.FromResult(
                    reports.Values
                    |> Seq.filter (fun report -> report.Owner = owner)
                    |> Seq.collect _.Results
                    |> Seq.filter (fun result -> result.ExecutionId = executionId)
                    |> Seq.sortByDescending _.Timestamp
                    |> Seq.toList
                )
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

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
