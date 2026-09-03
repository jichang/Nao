namespace Nao.Persistence

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

module InMemoryAuditLog =
    let create () : AuditLog =
        let entries = System.Collections.Concurrent.ConcurrentBag<AuditEntry>()
        { RecordAsync = fun entry -> entries.Add entry; Task.FromResult()
          QueryAsync = fun agentId since -> entries |> Seq.filter (fun entry -> entry.AgentId = agentId && entry.Timestamp >= since) |> Seq.sortByDescending (fun entry -> entry.Timestamp) |> Seq.toList |> Task.FromResult
          QueryByExecutionAsync = fun executionId -> entries |> Seq.filter (fun entry -> entry.ExecutionId = Some executionId) |> Seq.sortBy (fun entry -> entry.Timestamp) |> Seq.toList |> Task.FromResult
          GetDeniedCountAsync = fun agentId since -> entries |> Seq.filter (fun entry -> entry.AgentId = agentId && entry.Timestamp >= since && not entry.Permitted) |> Seq.length |> Task.FromResult }

module private AdoAudit =
    let ensureAsync factory =
        Ado.executeNonQuery factory "CREATE TABLE IF NOT EXISTS nao_audit (audit_id TEXT NOT NULL PRIMARY KEY, audit_ts TEXT NOT NULL, agent_name TEXT NOT NULL, agent_desc TEXT NOT NULL, action_json TEXT NOT NULL, audit_input TEXT NULL, audit_output TEXT NULL, permitted INTEGER NOT NULL, permission_level TEXT NOT NULL, violations TEXT NOT NULL, execution_id TEXT NULL, metadata TEXT NOT NULL)" [] :> Task
    let mapEntry (reader: DbDataReader) : AuditEntry =
        { Id = Guid.Parse(Ado.getString reader "audit_id"); Timestamp = Time.fromIso (Ado.getString reader "audit_ts"); AgentId = Ado.getString reader "agent_name"; Action = AuditActionCodec.fromJson (Ado.getString reader "action_json"); Input = Ado.getStringOpt reader "audit_input"; Output = Ado.getStringOpt reader "audit_output"; Permitted = Ado.getBool reader "permitted"; Decision = PermissionDecisionCodec.fromString (Ado.getString reader "permission_level"); ConstitutionViolations = Json.tagsFromJson (Ado.getString reader "violations"); ExecutionId = Ado.getStringOpt reader "execution_id" |> Option.map Guid.Parse; Metadata = Json.mapFromJson (Ado.getString reader "metadata") }
    let columns = "audit_id, audit_ts, agent_name, agent_desc, action_json, audit_input, audit_output, permitted, permission_level, violations, execution_id, metadata"

module AdoAuditLog =
    let create (factory: DbConnectionFactory) : AuditLog =
        let byAgent agentId = Ado.query factory ("SELECT " + AdoAudit.columns + " FROM nao_audit WHERE agent_name = @a") [ "@a", box agentId ] AdoAudit.mapEntry
        { RecordAsync = fun (entry: AuditEntry) ->
              task {
                  do! AdoAudit.ensureAsync factory
                  let value option = match option with Some text -> box text | None -> box DBNull.Value
                  let execution = entry.ExecutionId |> Option.map (fun id -> id.ToString("D")) |> value
                  let! _ = Ado.executeNonQuery factory "INSERT INTO nao_audit (audit_id, audit_ts, agent_name, agent_desc, action_json, audit_input, audit_output, permitted, permission_level, violations, execution_id, metadata) VALUES (@id, @ts, @an, @ad, @ac, @in, @out, @pm, @pl, @vi, @ex, @md)" [ "@id", box (entry.Id.ToString("D")); "@ts", box (Time.toIso entry.Timestamp); "@an", box entry.AgentId; "@ad", box ""; "@ac", box (AuditActionCodec.toJson entry.Action); "@in", value entry.Input; "@out", value entry.Output; "@pm", Ado.boolValue entry.Permitted; "@pl", box (PermissionDecisionCodec.toString entry.Decision); "@vi", box (Json.tagsToJson entry.ConstitutionViolations); "@ex", execution; "@md", box (Json.mapToJson entry.Metadata) ]
                  return ()
              }
          QueryAsync = fun agentId since ->
              task {
                  do! AdoAudit.ensureAsync factory
                  let! entries = byAgent agentId
                  return entries |> List.filter (fun entry -> entry.Timestamp >= since) |> List.sortByDescending (fun entry -> entry.Timestamp)
              }
          QueryByExecutionAsync = fun executionId ->
              task {
                  do! AdoAudit.ensureAsync factory
                  let! entries = Ado.query factory ("SELECT " + AdoAudit.columns + " FROM nao_audit WHERE execution_id = @e") [ "@e", box (executionId.ToString("D")) ] AdoAudit.mapEntry
                  return entries |> List.sortBy (fun entry -> entry.Timestamp)
              }
          GetDeniedCountAsync = fun agentId since ->
              task {
                  do! AdoAudit.ensureAsync factory
                  let! entries = byAgent agentId
                  return entries |> List.filter (fun entry -> entry.Timestamp >= since && not entry.Permitted) |> List.length
              } }

module FileAuditLog =
    let create baseDir : AuditLog =
        let sync = obj ()
        let file = Path.Combine(baseDir, "audit-log.json")
        let load () : Dto.AuditEntryDto list = FileJson.read<Dto.AuditEntryDto list> file []
        let save entries = FileJson.write file entries
        { RecordAsync = fun entry -> task { lock sync (fun () -> save (load () @ [ Dto.toAuditDto entry ])) }
          QueryAsync = fun agentId since -> task { return lock sync (fun () -> load () |> List.map Dto.ofAuditDto |> List.filter (fun entry -> entry.AgentId = agentId && entry.Timestamp >= since) |> List.sortByDescending (fun entry -> entry.Timestamp)) }
          QueryByExecutionAsync = fun executionId -> task { return lock sync (fun () -> load () |> List.map Dto.ofAuditDto |> List.filter (fun entry -> entry.ExecutionId = Some executionId) |> List.sortBy (fun entry -> entry.Timestamp)) }
          GetDeniedCountAsync = fun agentId since -> task { return lock sync (fun () -> load () |> List.map Dto.ofAuditDto |> List.filter (fun entry -> entry.AgentId = agentId && entry.Timestamp >= since && not entry.Permitted) |> List.length) } }

module AuditLogs =
    let ado factory : AuditLog = AdoAuditLog.create factory
    let file baseDir : AuditLog = FileAuditLog.create baseDir
