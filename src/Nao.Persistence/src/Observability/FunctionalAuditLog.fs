namespace Nao.Persistence

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

module private AuditOperations =
    let private failure =
        PlatformFailure.fromException PlatformFailureBoundary.Storage None

    let protect owner operation =
        task {
            if String.IsNullOrWhiteSpace owner then
                return
                    Error(
                        PlatformFailure.create
                            PlatformErrorCategory.InvalidInput
                            "Audit owner cannot be blank."
                            false
                            None
                    )
            else
                try
                    let! count = operation ()
                    return Ok count
                with ex ->
                    return Error(failure ex)
        }

module InMemoryAuditLog =
    let create () : AuditLog =
        let entries = System.Collections.Concurrent.ConcurrentDictionary<Guid, AuditEntry>()

        let delete (predicate: AuditEntry -> bool) =
            entries
            |> Seq.filter (fun pair -> predicate pair.Value)
            |> Seq.sumBy (fun pair -> if entries.TryRemove(pair.Key) |> fst then 1 else 0)
            |> Task.FromResult

        let recordAsync (entry: AuditEntry) =
            entries.[entry.Id] <- entry
            Task.FromResult()

        let queryAsync agentId since =
            entries.Values
            |> Seq.filter (fun entry -> entry.AgentId = agentId && entry.Timestamp >= since)
            |> Seq.sortByDescending (fun entry -> entry.Timestamp)
            |> Seq.toList
            |> Task.FromResult

        let queryByExecutionAsync (executionId: Guid) =
            entries.Values
            |> Seq.filter (fun entry -> entry.ExecutionId = Some executionId)
            |> Seq.sortBy (fun entry -> entry.Timestamp)
            |> Seq.toList
            |> Task.FromResult

        let getDeniedCountAsync agentId since =
            entries.Values
            |> Seq.filter (fun entry -> entry.AgentId = agentId && entry.Timestamp >= since && not entry.Permitted)
            |> Seq.length
            |> Task.FromResult

        let deleteOwnerAsync owner =
            AuditOperations.protect owner (fun () -> delete (fun entry -> entry.AgentId = owner))

        let deleteExpiredAsync owner before =
            AuditOperations.protect owner (fun () ->
                delete (fun entry -> entry.AgentId = owner && entry.Timestamp < before))

        { RecordAsync = recordAsync
          QueryAsync = queryAsync
          QueryByExecutionAsync = queryByExecutionAsync
          GetDeniedCountAsync = getDeniedCountAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module private AdoAudit =
    let ensureAsync factory =
        AdoSchema.ensureVersionedTable
            factory
            "audit"
            "nao_audit"
            "CREATE TABLE IF NOT EXISTS nao_audit (audit_id TEXT NOT NULL PRIMARY KEY, audit_ts TEXT NOT NULL, agent_name TEXT NOT NULL, agent_desc TEXT NOT NULL, action_json TEXT NOT NULL, audit_input TEXT NULL, audit_output TEXT NULL, permitted INTEGER NOT NULL, permission_level TEXT NOT NULL, violations TEXT NOT NULL, execution_id TEXT NULL, metadata TEXT NOT NULL)"

    let mapEntry (reader: DbDataReader) : AuditEntry =
        let id = Ado.getString reader "audit_id"

        try
            { Id = Guid.Parse id
              Timestamp = Time.fromIso (Ado.getString reader "audit_ts")
              AgentId = Ado.getString reader "agent_name"
              Action = AuditActionCodec.fromJson (Ado.getString reader "action_json")
              Input = Ado.getStringOpt reader "audit_input"
              Output = Ado.getStringOpt reader "audit_output"
              Permitted = Ado.getBool reader "permitted"
              Decision = PermissionDecisionCodec.fromString (Ado.getString reader "permission_level")
              ConstitutionViolations = Json.tagsFromJson (Ado.getString reader "violations")
              ExecutionId = Ado.getStringOpt reader "execution_id" |> Option.map Guid.Parse
              Metadata = Json.mapFromJson (Ado.getString reader "metadata") }
        with ex ->
            raise (
                InvalidDataException(sprintf "Audit row '%s' is invalid. Follow docs/migrations before writing." id, ex)
            )

    let columns =
        "audit_id, audit_ts, agent_name, agent_desc, action_json, audit_input, audit_output, permitted, permission_level, violations, execution_id, metadata"

module AdoAuditLog =
    let create (factory: DbConnectionFactory) : AuditLog =
        let validateAsync () =
            task {
                do! AdoAudit.ensureAsync factory

                let! _ = Ado.query factory ("SELECT " + AdoAudit.columns + " FROM nao_audit") [] AdoAudit.mapEntry

                return ()
            }

        let byAgent agentId =
            Ado.query
                factory
                ("SELECT " + AdoAudit.columns + " FROM nao_audit WHERE agent_name = @a")
                [ "@a", box agentId ]
                AdoAudit.mapEntry

        let recordAsync (entry: AuditEntry) =
            task {
                do! validateAsync ()

                let value option =
                    match option with
                    | Some text -> box text
                    | None -> box DBNull.Value

                let execution =
                    entry.ExecutionId |> Option.map (fun id -> id.ToString("D")) |> value

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "INSERT INTO nao_audit (audit_id, audit_ts, agent_name, agent_desc, action_json, audit_input, audit_output, permitted, permission_level, violations, execution_id, metadata) VALUES (@id, @ts, @an, @ad, @ac, @in, @out, @pm, @pl, @vi, @ex, @md)"
                        [ "@id", box (entry.Id.ToString("D"))
                          "@ts", box (Time.toIso entry.Timestamp)
                          "@an", box entry.AgentId
                          "@ad", box ""
                          "@ac", box (AuditActionCodec.toJson entry.Action)
                          "@in", value entry.Input
                          "@out", value entry.Output
                          "@pm", Ado.boolValue entry.Permitted
                          "@pl", box (PermissionDecisionCodec.toString entry.Decision)
                          "@vi", box (Json.tagsToJson entry.ConstitutionViolations)
                          "@ex", execution
                          "@md", box (Json.mapToJson entry.Metadata) ]

                return ()
            }

        let queryAsync agentId since =
            task {
                do! AdoAudit.ensureAsync factory
                let! entries = byAgent agentId

                return
                    entries
                    |> List.filter (fun entry -> entry.Timestamp >= since)
                    |> List.sortByDescending (fun entry -> entry.Timestamp)
            }

        let queryByExecutionAsync (executionId: Guid) =
            task {
                do! AdoAudit.ensureAsync factory

                let! entries =
                    Ado.query
                        factory
                        ("SELECT " + AdoAudit.columns + " FROM nao_audit WHERE execution_id = @e")
                        [ "@e", box (executionId.ToString("D")) ]
                        AdoAudit.mapEntry

                return entries |> List.sortBy (fun entry -> entry.Timestamp)
            }

        let getDeniedCountAsync agentId since =
            task {
                do! AdoAudit.ensureAsync factory
                let! entries = byAgent agentId

                return
                    entries
                    |> List.filter (fun entry -> entry.Timestamp >= since && not entry.Permitted)
                    |> List.length
            }

        let delete sql parameters =
            task {
                do! validateAsync ()
                return! Ado.executeNonQuery factory sql parameters
            }

        let deleteOwnerAsync owner =
            AuditOperations.protect owner (fun () ->
                delete "DELETE FROM nao_audit WHERE agent_name = @a" [ "@a", box owner ])

        let deleteExpiredAsync owner before =
            AuditOperations.protect owner (fun () ->
                delete
                    "DELETE FROM nao_audit WHERE agent_name = @a AND audit_ts < @before"
                    [ "@a", box owner; "@before", box (Time.toIso before) ])

        { RecordAsync = recordAsync
          QueryAsync = queryAsync
          QueryByExecutionAsync = queryByExecutionAsync
          GetDeniedCountAsync = getDeniedCountAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module FileAuditLog =
    [<Literal>]
    let private CurrentSchemaVersion = 1

    let create baseDir : AuditLog =
        let sync = obj ()
        let file = Path.Combine(baseDir, "audit-log.json")

        let load () : Dto.AuditEntryDto list =
            VersionedFileJson.read<Dto.AuditEntryDto list> "Audit log" CurrentSchemaVersion file []

        let save entries =
            VersionedFileJson.write CurrentSchemaVersion file entries

        let delete predicate =
            task {
                return
                    lock sync (fun () ->
                        let entries = load ()
                        let retained = entries |> List.filter (Dto.ofAuditDto >> predicate >> not)
                        save retained
                        entries.Length - retained.Length)
            }

        let recordAsync entry =
            task { lock sync (fun () -> save (load () @ [ Dto.toAuditDto entry ])) }

        let queryAsync agentId since =
            task {
                return
                    lock sync (fun () ->
                        load ()
                        |> List.map Dto.ofAuditDto
                        |> List.filter (fun entry -> entry.AgentId = agentId && entry.Timestamp >= since)
                        |> List.sortByDescending (fun entry -> entry.Timestamp))
            }

        let queryByExecutionAsync executionId =
            task {
                return
                    lock sync (fun () ->
                        load ()
                        |> List.map Dto.ofAuditDto
                        |> List.filter (fun entry -> entry.ExecutionId = Some executionId)
                        |> List.sortBy (fun entry -> entry.Timestamp))
            }

        let getDeniedCountAsync agentId since =
            task {
                return
                    lock sync (fun () ->
                        load ()
                        |> List.map Dto.ofAuditDto
                        |> List.filter (fun entry ->
                            entry.AgentId = agentId && entry.Timestamp >= since && not entry.Permitted)
                        |> List.length)
            }

        let deleteOwnerAsync owner =
            AuditOperations.protect owner (fun () -> delete (fun entry -> entry.AgentId = owner))

        let deleteExpiredAsync owner before =
            AuditOperations.protect owner (fun () ->
                delete (fun entry -> entry.AgentId = owner && entry.Timestamp < before))

        { RecordAsync = recordAsync
          QueryAsync = queryAsync
          QueryByExecutionAsync = queryByExecutionAsync
          GetDeniedCountAsync = getDeniedCountAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module AuditLogs =
    let inMemory () : AuditLog = InMemoryAuditLog.create ()
    let ado factory : AuditLog = AdoAuditLog.create factory
    let file baseDir : AuditLog = FileAuditLog.create baseDir
