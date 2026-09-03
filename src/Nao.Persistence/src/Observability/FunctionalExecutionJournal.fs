namespace Nao.Persistence

open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

module InMemoryExecutionJournal =
    let create () : ExecutionJournal =
        let entries = System.Collections.Generic.List<ExecutionRecord>()
        { RecordAsync = fun record ->
              lock entries (fun () -> entries.Insert(0, record))
              Task.CompletedTask
          GetHistoryAsync = fun () -> lock entries (fun () -> entries |> Seq.toList) |> Task.FromResult
          GetRevertibleAsync = fun () -> lock entries (fun () -> entries |> Seq.filter (fun entry -> not entry.Reverted) |> Seq.toList) |> Task.FromResult
          MarkRevertedAsync = fun record ->
              lock entries (fun () ->
                  match entries |> Seq.tryFindIndex (fun entry -> entry.ToolName = record.ToolName && entry.ExecutedAt = record.ExecutedAt) with
                  | Some index -> entries.[index] <- { entries.[index] with Reverted = true }
                  | None -> ())
              Task.CompletedTask }

module AdoExecutionJournal =
    let create (factory: DbConnectionFactory) : ExecutionJournal =
        let ensureAsync () =
            Ado.executeNonQuery factory "CREATE TABLE IF NOT EXISTS nao_journal (tool_name TEXT NOT NULL, tool_input TEXT NOT NULL, tool_output TEXT NOT NULL, content_type TEXT NOT NULL, content_meta TEXT NOT NULL, executed_at TEXT NOT NULL, reverted INTEGER NOT NULL, metadata TEXT NOT NULL)" [] :> Task
        let mapRecord (reader: DbDataReader) : ExecutionRecord =
            { ToolName = Ado.getString reader "tool_name"; Input = Ado.getString reader "tool_input"; Output = Ado.getString reader "tool_output"; ExecutedAt = Time.fromIso (Ado.getString reader "executed_at"); Reverted = Ado.getBool reader "reverted"; Metadata = Json.mapFromJson (Ado.getString reader "metadata") }
        { RecordAsync = fun record ->
              task {
                  do! ensureAsync ()
                  let! _ = Ado.executeNonQuery factory "INSERT INTO nao_journal (tool_name, tool_input, tool_output, content_type, content_meta, executed_at, reverted, metadata) VALUES (@tn, @ti, @to, @ct, @cm, @ea, @rv, @md)" [ "@tn", box record.ToolName; "@ti", box record.Input; "@to", box record.Output; "@ct", box ""; "@cm", box "{}"; "@ea", box (Time.toIso record.ExecutedAt); "@rv", Ado.boolValue record.Reverted; "@md", box (Json.mapToJson record.Metadata) ]
                  return ()
              } :> Task
          GetHistoryAsync = fun () ->
              task {
                  do! ensureAsync ()
                  return! Ado.query factory "SELECT tool_name, tool_input, tool_output, executed_at, reverted, metadata FROM nao_journal ORDER BY executed_at DESC" [] mapRecord
              }
          GetRevertibleAsync = fun () ->
              task {
                  do! ensureAsync ()
                  return! Ado.query factory "SELECT tool_name, tool_input, tool_output, executed_at, reverted, metadata FROM nao_journal WHERE reverted = 0 ORDER BY executed_at DESC" [] mapRecord
              }
          MarkRevertedAsync = fun record ->
              task {
                  do! ensureAsync ()
                  let! _ = Ado.executeNonQuery factory "UPDATE nao_journal SET reverted = 1 WHERE tool_name = @tn AND executed_at = @ea" [ "@tn", box record.ToolName; "@ea", box (Time.toIso record.ExecutedAt) ]
                  return ()
              } :> Task }

module FileExecutionJournal =
    let create baseDir : ExecutionJournal =
        let sync = obj ()
        let file = Path.Combine(baseDir, "execution-journal.json")
        let load () : Dto.ExecutionRecordDto list = FileJson.read<Dto.ExecutionRecordDto list> file []
        let save records = FileJson.write file records
        { RecordAsync = fun record -> task { lock sync (fun () -> save (Dto.toExecutionDto record :: load ())) } :> Task
          GetHistoryAsync = fun () -> task { return lock sync (fun () -> load () |> List.map Dto.ofExecutionDto) }
          GetRevertibleAsync = fun () -> task { return lock sync (fun () -> load () |> List.map Dto.ofExecutionDto |> List.filter (fun record -> not record.Reverted)) }
          MarkRevertedAsync = fun record ->
              task {
                  lock sync (fun () ->
                      let mutable marked = false
                      load ()
                      |> List.map (fun dto -> if not marked && dto.ToolName = record.ToolName && dto.ExecutedAt = record.ExecutedAt then marked <- true; { dto with Reverted = true } else dto)
                      |> save)
              } :> Task }

module ExecutionJournals =
    let ado factory : ExecutionJournal = AdoExecutionJournal.create factory
    let file baseDir : ExecutionJournal = FileExecutionJournal.create baseDir
