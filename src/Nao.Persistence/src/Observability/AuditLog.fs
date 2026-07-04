namespace Nao.Persistence

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

/// In-memory audit log for testing (moved out of Nao.Agents)
type InMemoryAuditLog() =
    let entries = System.Collections.Concurrent.ConcurrentBag<AuditEntry>()

    interface IAuditLog with
        member _.RecordAsync(entry: AuditEntry) =
            entries.Add(entry)
            Task.FromResult()

        member _.QueryAsync (agentId: AgentId) (since: DateTimeOffset) =
            entries
            |> Seq.filter (fun e -> e.AgentId = agentId && e.Timestamp >= since)
            |> Seq.sortByDescending (fun e -> e.Timestamp)
            |> Seq.toList
            |> Task.FromResult

        member _.QueryByExecutionAsync(executionId: Guid) =
            entries
            |> Seq.filter (fun e -> e.ExecutionId = Some executionId)
            |> Seq.sortBy (fun e -> e.Timestamp)
            |> Seq.toList
            |> Task.FromResult

        member _.GetDeniedCountAsync (agentId: AgentId) (since: DateTimeOffset) =
            entries
            |> Seq.filter (fun e -> e.AgentId = agentId && e.Timestamp >= since && not e.Permitted)
            |> Seq.length
            |> Task.FromResult

/// ADO.NET-backed IAuditLog. The AuditAction/PermissionLevel unions are encoded
/// to text columns so the schema stays portable across providers.
type AdoAuditLog(factory: IDbConnectionFactory) =

    let ensureAsync () =
        Ado.executeNonQuery
            factory
            "CREATE TABLE IF NOT EXISTS nao_audit (\
                audit_id TEXT NOT NULL PRIMARY KEY, \
                audit_ts TEXT NOT NULL, \
                agent_name TEXT NOT NULL, \
                agent_desc TEXT NOT NULL, \
                action_json TEXT NOT NULL, \
                audit_input TEXT NULL, \
                audit_output TEXT NULL, \
                permitted INTEGER NOT NULL, \
                permission_level TEXT NOT NULL, \
                violations TEXT NOT NULL, \
                execution_id TEXT NULL, \
                metadata TEXT NOT NULL)"
            []
        :> Task

    let mapEntry (r: DbDataReader) : AuditEntry =
        { Id = Guid.Parse(Ado.getString r "audit_id")
          Timestamp = Time.fromIso (Ado.getString r "audit_ts")
          AgentId =
            { Name = Ado.getString r "agent_name"
              Description = Ado.getString r "agent_desc" }
          Action = AuditActionCodec.fromJson (Ado.getString r "action_json")
          Input = Ado.getStringOpt r "audit_input"
          Output = Ado.getStringOpt r "audit_output"
          Permitted = Ado.getBool r "permitted"
          PermissionLevel = PermissionLevelCodec.fromString (Ado.getString r "permission_level")
          ConstitutionViolations = Json.tagsFromJson (Ado.getString r "violations")
          ExecutionId = Ado.getStringOpt r "execution_id" |> Option.map Guid.Parse
          Metadata = Json.mapFromJson (Ado.getString r "metadata") }

    let queryByAgent (agentId: AgentId) : Task<AuditEntry list> =
        Ado.query
            factory
            "SELECT audit_id, audit_ts, agent_name, agent_desc, action_json, audit_input, audit_output, permitted, \
                permission_level, violations, execution_id, metadata \
                FROM nao_audit WHERE agent_name = @a"
            [ "@a", box agentId.Name ]
            mapEntry

    interface IAuditLog with
        member _.RecordAsync(entry: AuditEntry) =
            task {
                do! ensureAsync ()
                let! _ =
                    Ado.executeNonQuery
                        factory
                        "INSERT INTO nao_audit (audit_id, audit_ts, agent_name, agent_desc, action_json, audit_input, \
                            audit_output, permitted, permission_level, violations, execution_id, metadata) \
                            VALUES (@id, @ts, @an, @ad, @ac, @in, @out, @pm, @pl, @vi, @ex, @md)"
                        [ "@id", box (entry.Id.ToString("D"))
                          "@ts", box (Time.toIso entry.Timestamp)
                          "@an", box entry.AgentId.Name
                          "@ad", box entry.AgentId.Description
                          "@ac", box (AuditActionCodec.toJson entry.Action)
                          "@in", (match entry.Input with Some s -> box s | None -> box DBNull.Value)
                          "@out", (match entry.Output with Some s -> box s | None -> box DBNull.Value)
                          "@pm", Ado.boolValue entry.Permitted
                          "@pl", box (PermissionLevelCodec.toString entry.PermissionLevel)
                          "@vi", box (Json.tagsToJson entry.ConstitutionViolations)
                          "@ex", (match entry.ExecutionId with Some g -> box (g.ToString("D")) | None -> box DBNull.Value)
                          "@md", box (Json.mapToJson entry.Metadata) ]
                return ()
            }

        member _.QueryAsync (agentId: AgentId) (since: DateTimeOffset) =
            task {
                do! ensureAsync ()
                let! entries = queryByAgent agentId
                return
                    entries
                    |> List.filter (fun e -> e.AgentId = agentId && e.Timestamp >= since)
                    |> List.sortByDescending (fun e -> e.Timestamp)
            }

        member _.QueryByExecutionAsync(executionId: Guid) =
            task {
                do! ensureAsync ()
                let! entries =
                    Ado.query
                        factory
                        "SELECT audit_id, audit_ts, agent_name, agent_desc, action_json, audit_input, audit_output, permitted, \
                            permission_level, violations, execution_id, metadata \
                            FROM nao_audit WHERE execution_id = @e"
                        [ "@e", box (executionId.ToString("D")) ]
                        mapEntry
                return entries |> List.sortBy (fun e -> e.Timestamp)
            }

        member _.GetDeniedCountAsync (agentId: AgentId) (since: DateTimeOffset) =
            task {
                do! ensureAsync ()
                let! entries = queryByAgent agentId
                return
                    entries
                    |> List.filter (fun e -> e.AgentId = agentId && e.Timestamp >= since && not e.Permitted)
                    |> List.length
            }

/// FileSystem-backed IAuditLog. A single append-style JSON document.
type FileAuditLog(baseDir: string) =
    let sync = obj ()
    let file = Path.Combine(baseDir, "audit-log.json")

    let load () : Dto.AuditEntryDto list = FileJson.read<Dto.AuditEntryDto list> file []
    let save (entries: Dto.AuditEntryDto list) = FileJson.write file entries

    interface IAuditLog with
        member _.RecordAsync(entry: AuditEntry) =
            task { lock sync (fun () -> save (load () @ [ Dto.toAuditDto entry ])) }

        member _.QueryAsync (agentId: AgentId) (since: DateTimeOffset) =
            task {
                return
                    lock sync (fun () ->
                        load ()
                        |> List.map Dto.ofAuditDto
                        |> List.filter (fun e -> e.AgentId = agentId && e.Timestamp >= since)
                        |> List.sortByDescending (fun e -> e.Timestamp))
            }

        member _.QueryByExecutionAsync(executionId: Guid) =
            task {
                return
                    lock sync (fun () ->
                        load ()
                        |> List.map Dto.ofAuditDto
                        |> List.filter (fun e -> e.ExecutionId = Some executionId)
                        |> List.sortBy (fun e -> e.Timestamp))
            }

        member _.GetDeniedCountAsync (agentId: AgentId) (since: DateTimeOffset) =
            task {
                return
                    lock sync (fun () ->
                        load ()
                        |> List.map Dto.ofAuditDto
                        |> List.filter (fun e -> e.AgentId = agentId && e.Timestamp >= since && not e.Permitted)
                        |> List.length)
            }

/// Factory helpers for audit log implementations.
module AuditLogs =
    /// ADO.NET-backed audit log over any provider supplied via the connection factory.
    let ado (factory: IDbConnectionFactory) : IAuditLog = AdoAuditLog(factory) :> IAuditLog

    /// FileSystem-backed audit log rooted at the given directory.
    let file (baseDir: string) : IAuditLog = FileAuditLog(baseDir) :> IAuditLog
