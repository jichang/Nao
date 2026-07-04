namespace Nao.Persistence

open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents
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
                entries |> Seq.filter (fun e -> not e.Reverted) |> Seq.toList)
            |> Task.FromResult

        member _.MarkRevertedAsync(record: ExecutionRecord) =
            lock entries (fun () ->
                let idx = entries |> Seq.tryFindIndex (fun e ->
                    e.ToolName = record.ToolName && e.ExecutedAt = record.ExecutedAt)
                match idx with
                | Some i -> entries.[i] <- { entries.[i] with Reverted = true }
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

    let mapRecord (r: DbDataReader) : ExecutionRecord =
        { ToolName = Ado.getString r "tool_name"
          Input = Ado.getString r "tool_input"
          Output = Ado.getString r "tool_output"
          ContentMeta =
            { ContentType = Ado.getString r "content_type"
              Metadata = Json.mapFromJson (Ado.getString r "content_meta") }
          ExecutedAt = Time.fromIso (Ado.getString r "executed_at")
          Reverted = Ado.getBool r "reverted"
          Metadata = Json.mapFromJson (Ado.getString r "metadata") }

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
                          "@ct", box record.ContentMeta.ContentType
                          "@cm", box (Json.mapToJson record.ContentMeta.Metadata)
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
                        "SELECT tool_name, tool_input, tool_output, content_type, content_meta, executed_at, reverted, metadata \
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
                        "SELECT tool_name, tool_input, tool_output, content_type, content_meta, executed_at, reverted, metadata \
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
                        load () |> List.map Dto.ofExecutionDto |> List.filter (fun r -> not r.Reverted))
            }

        member _.MarkRevertedAsync(record: ExecutionRecord) =
            task {
                lock sync (fun () ->
                    let mutable marked = false
                    let updated =
                        load ()
                        |> List.map (fun d ->
                            if not marked && d.ToolName = record.ToolName && d.ExecutedAt = record.ExecutedAt then
                                marked <- true
                                { d with Reverted = true }
                            else
                                d)
                    save updated)
            }
            :> Task

/// Factory helpers for execution journal implementations.
module ExecutionJournals =
    /// ADO.NET-backed journal over any provider supplied via the connection factory.
    let ado (factory: IDbConnectionFactory) : IExecutionJournal = AdoExecutionJournal(factory) :> IExecutionJournal

    /// FileSystem-backed journal rooted at the given directory.
    let file (baseDir: string) : IExecutionJournal = FileExecutionJournal(baseDir) :> IExecutionJournal
