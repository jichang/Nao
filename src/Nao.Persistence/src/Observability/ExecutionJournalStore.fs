namespace Nao.Persistence

open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

/// In-memory execution journal (default implementation)
type InMemoryExecutionJournal() =
    let entries = System.Collections.Generic.List<ExecutionRecord>()

    interface IExecutionJournal with
        member _.RecordAsync(record: ExecutionRecord) =
            lock entries (fun () -> entries.Insert(0, record))
            Task.CompletedTask

        member _.GetHistoryAsync() =
            lock entries (fun () -> entries |> Seq.toList)
            |> Task.FromResult

        member _.GetRevertibleAsync() =
            lock entries (fun () ->
                entries |> Seq.filter (fun entry -> not entry.Reverted) |> Seq.toList)
            |> Task.FromResult

        member _.MarkRevertedAsync(record: ExecutionRecord) =
            lock entries (fun () ->
                let index =
                    entries
                    |> Seq.tryFindIndex (fun entry ->
                        entry.ToolName = record.ToolName && entry.ExecutedAt = record.ExecutedAt)
                match index with
                | Some value -> entries.[value] <- { entries.[value] with Reverted = true }
                | None -> ())
            Task.CompletedTask

/// ADO.NET-backed IExecutionJournal. History is returned most-recent-first.
type AdoExecutionJournal(factory: IDbConnectionFactory) =

    let ensureAsync () =
        Ado.executeNonQuery
            factory
            "CREATE TABLE IF NOT EXISTS nao_journal (\
                tool_name TEXT NOT NULL, \
                tool_input TEXT NOT NULL, \
                tool_output TEXT NOT NULL, \
                content_type TEXT NOT NULL, \
                content_meta TEXT NOT NULL, \
                executed_at TEXT NOT NULL, \
                reverted INTEGER NOT NULL, \
                metadata TEXT NOT NULL)"
            []
        :> Task

    let mapRecord (reader: DbDataReader) : ExecutionRecord =
        { ToolName = Ado.getString reader "tool_name"
          Input = Ado.getString reader "tool_input"
          Output = Ado.getString reader "tool_output"
          ExecutedAt = Time.fromIso (Ado.getString reader "executed_at")
          Reverted = Ado.getBool reader "reverted"
          Metadata = Json.mapFromJson (Ado.getString reader "metadata") }

    interface IExecutionJournal with
        member _.RecordAsync(record: ExecutionRecord) =
            task {
                do! ensureAsync ()
                let! _ =
                    Ado.executeNonQuery
                        factory
                        "INSERT INTO nao_journal (tool_name, tool_input, tool_output, content_type, content_meta, executed_at, reverted, metadata) \
                            VALUES (@tn, @ti, @to, @ct, @cm, @ea, @rv, @md)"
                        [ "@tn", box record.ToolName
                          "@ti", box record.Input
                          "@to", box record.Output
                          "@ct", box ""
                          "@cm", box "{}"
                          "@ea", box (Time.toIso record.ExecutedAt)
                          "@rv", Ado.boolValue record.Reverted
                          "@md", box (Json.mapToJson record.Metadata) ]
                return ()
            }
            :> Task

        member _.GetHistoryAsync() =
            task {
                do! ensureAsync ()
                return!
                    Ado.query
                        factory
                        "SELECT tool_name, tool_input, tool_output, executed_at, reverted, metadata \
                            FROM nao_journal ORDER BY executed_at DESC"
                        []
                        mapRecord
            }

        member _.GetRevertibleAsync() =
            task {
                do! ensureAsync ()
                return!
                    Ado.query
                        factory
                        "SELECT tool_name, tool_input, tool_output, executed_at, reverted, metadata \
                            FROM nao_journal WHERE reverted = 0 ORDER BY executed_at DESC"
                        []
                        mapRecord
            }

        member _.MarkRevertedAsync(record: ExecutionRecord) =
            task {
                do! ensureAsync ()
                let! _ =
                    Ado.executeNonQuery
                        factory
                        "UPDATE nao_journal SET reverted = 1 WHERE tool_name = @tn AND executed_at = @ea"
                        [ "@tn", box record.ToolName; "@ea", box (Time.toIso record.ExecutedAt) ]
                return ()
            }
            :> Task

/// FileSystem-backed IExecutionJournal. A single JSON document, newest-first.
type FileExecutionJournal(baseDir: string) =
    let sync = obj ()
    let file = Path.Combine(baseDir, "execution-journal.json")

    let load () : Dto.ExecutionRecordDto list =
        FileJson.read<Dto.ExecutionRecordDto list> file []

    let save (records: Dto.ExecutionRecordDto list) = FileJson.write file records

    interface IExecutionJournal with
        member _.RecordAsync(record: ExecutionRecord) =
            task { lock sync (fun () -> save (Dto.toExecutionDto record :: load ())) } :> Task

        member _.GetHistoryAsync() =
            task { return lock sync (fun () -> load () |> List.map Dto.ofExecutionDto) }

        member _.GetRevertibleAsync() =
            task {
                return
                    lock sync (fun () ->
                        load () |> List.map Dto.ofExecutionDto |> List.filter (fun record -> not record.Reverted))
            }

        member _.MarkRevertedAsync(record: ExecutionRecord) =
            task {
                lock sync (fun () ->
                    let mutable marked = false
                    let updated =
                        load ()
                        |> List.map (fun dto ->
                            if not marked && dto.ToolName = record.ToolName && dto.ExecutedAt = record.ExecutedAt then
                                marked <- true
                                { dto with Reverted = true }
                            else
                                dto)
                    save updated)
            }
            :> Task

/// Factory helpers for execution journal implementations.
module ExecutionJournals =
    /// ADO.NET-backed journal over any provider supplied via the connection factory.
    let ado (factory: IDbConnectionFactory) : IExecutionJournal = AdoExecutionJournal(factory) :> IExecutionJournal

    /// FileSystem-backed journal rooted at the given directory.
    let file (baseDir: string) : IExecutionJournal = FileExecutionJournal(baseDir) :> IExecutionJournal
