namespace Nao.Persistence

open System
open System.IO
open System.Threading.Tasks
open Nao.Agents

module private AuditRedaction =
    let entry (entry: AuditEntry) =
        let redact = SecretRedaction.redact

        let action =
            match entry.Action with
            | AuditAction.LlmCall model -> AuditAction.LlmCall(redact model)
            | AuditAction.ToolInvocation toolName -> AuditAction.ToolInvocation(redact toolName)
            | AuditAction.AgentDelegation agentName -> AuditAction.AgentDelegation(redact agentName)
            | AuditAction.MemoryWrite key -> AuditAction.MemoryWrite(redact key)
            | AuditAction.MemoryRead key -> AuditAction.MemoryRead(redact key)
            | AuditAction.ResourceAccess(kind, resource) -> AuditAction.ResourceAccess(redact kind, redact resource)
            | AuditAction.PermissionCheck capability -> AuditAction.PermissionCheck(redact capability)
            | AuditAction.PolicyEvaluation policyId -> AuditAction.PolicyEvaluation(redact policyId)
            | AuditAction.ConstitutionEvaluation ruleId -> AuditAction.ConstitutionEvaluation(redact ruleId)
            | AuditAction.LifecycleTransition(fromState, toState) ->
                AuditAction.LifecycleTransition(redact fromState, redact toState)

        { entry with
            AgentId = redact entry.AgentId
            Action = action
            Input = SecretRedaction.redactOption entry.Input
            Output = SecretRedaction.redactOption entry.Output
            Metadata = SecretRedaction.redactMap entry.Metadata }

module private AuditState =
    let create (initial: (AuthorizationScope * AuditEntry) seq) : AuditLog =
        let entries =
            System.Collections.Concurrent.ConcurrentDictionary<string * Guid, AuditEntry>()

        let ownerKey = AuthorizationScope.identity >> AuthorizationIdentity.storageKey

        for scope, entry in initial do
            entries.[(ownerKey scope, entry.Id)] <- entry

        let owned scope =
            let owner = ownerKey scope

            entries
            |> Seq.choose (fun pair -> if fst pair.Key = owner then Some pair.Value else None)
            |> Seq.toArray

        let delete scope predicate =
            task {
                let owner = ownerKey scope
                let mutable deleted = 0

                for pair in entries do
                    if fst pair.Key = owner && predicate pair.Value then
                        match entries.TryRemove pair.Key with
                        | true, _ -> deleted <- deleted + 1
                        | false, _ -> ()

                return Ok deleted
            }

        { RecordAsync =
            fun scope entry ->
                let entry = AuditRedaction.entry entry
                entries.[(ownerKey scope, entry.Id)] <- entry
                Task.FromResult()
          QueryAsync =
            fun scope agentId since ->
                owned scope
                |> Seq.filter (fun entry -> entry.AgentId = agentId && entry.Timestamp >= since)
                |> Seq.sortByDescending _.Timestamp
                |> Seq.toList
                |> Task.FromResult
          QueryByExecutionAsync =
            fun scope executionId ->
                owned scope
                |> Seq.filter (fun entry -> entry.ExecutionId = Some executionId)
                |> Seq.sortBy _.Timestamp
                |> Seq.toList
                |> Task.FromResult
          GetDeniedCountAsync =
            fun scope agentId since ->
                owned scope
                |> Seq.filter (fun entry -> entry.AgentId = agentId && entry.Timestamp >= since && not entry.Permitted)
                |> Seq.length
                |> Task.FromResult
          DeleteScopeAsync = fun scope -> delete scope (fun _ -> true)
          DeleteExpiredAsync =
            fun authorization before ->
                delete (AuditRetentionAuthorization.scope authorization) (fun entry -> entry.Timestamp < before) }

module InMemoryAuditLog =
    let create () : AuditLog = AuditState.create Seq.empty

[<RequireQualifiedAccess>]
type AuditEvent =
    | Accepted of AuditEntry
    | DeleteScope
    | DeleteExpired of DateTimeOffset

type AuditDocument = { Version: int; Event: AuditEvent }

module PersistentAuditLog =
    let private decode context lineNumber line =
        try
            let document = FSharpJson.deserialize<AuditDocument> line

            if isNull (box document) || document.Version <> 1 then
                raise (System.Text.Json.JsonException("Expected audit schema version 1."))

            document.Event
        with ex ->
            raise (
                InvalidDataException(
                    sprintf
                        "Audit stream '%s' is invalid at event %d. Follow docs/migrations before writing."
                        context
                        lineNumber,
                    ex
                )
            )

    /// The host supplies a separate stream for each claim-free authorization partition key.
    let create (openStore: string -> EventStore) : AuditLog =
        let sync = obj ()

        let load scope =
            let key = AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey
            let context = "audit/" + key
            let store = openStore key
            let inner = InMemoryAuditLog.create ()

            let events =
                store.LoadAll() |> List.mapi (fun index line -> decode context (index + 1) line)

            let restored = ResizeArray<AuditEntry>()

            for event in events do
                match event with
                | AuditEvent.Accepted entry -> restored.Add entry
                | AuditEvent.DeleteScope -> restored.Clear()
                | AuditEvent.DeleteExpired before -> restored.RemoveAll(fun entry -> entry.Timestamp < before) |> ignore

            for entry in restored do
                inner.RecordAsync scope entry |> _.GetAwaiter().GetResult()

            store, inner

        let read scope operation =
            lock sync (fun () ->
                let _, inner = load scope
                operation inner)

        let mutate scope operation event =
            lock sync (fun () ->
                let store, inner = load scope
                let result = operation inner
                store.Append(FSharpJson.serialize { Version = 1; Event = event })
                result)

        let protect operation =
            task {
                try
                    return Ok(operation ())
                with ex ->
                    return Error(PlatformFailure.fromException PlatformFailureBoundary.Storage None ex)
            }

        { RecordAsync =
            fun scope entry ->
                let entry = AuditRedaction.entry entry

                task {
                    mutate
                        scope
                        (fun inner -> inner.RecordAsync scope entry |> _.GetAwaiter().GetResult())
                        (AuditEvent.Accepted entry)
                }
          QueryAsync = fun scope agentId since -> read scope (fun inner -> inner.QueryAsync scope agentId since)
          QueryByExecutionAsync =
            fun scope executionId -> read scope (fun inner -> inner.QueryByExecutionAsync scope executionId)
          GetDeniedCountAsync =
            fun scope agentId since -> read scope (fun inner -> inner.GetDeniedCountAsync scope agentId since)
          DeleteScopeAsync =
            fun scope ->
                protect (fun () ->
                    mutate
                        scope
                        (fun inner ->
                            inner.DeleteScopeAsync scope
                            |> _.GetAwaiter().GetResult()
                            |> Result.defaultWith (fun failure -> raise (InvalidOperationException failure.Message)))
                        AuditEvent.DeleteScope)
          DeleteExpiredAsync =
            fun authorization before ->
                protect (fun () ->
                    let scope = AuditRetentionAuthorization.scope authorization

                    mutate
                        scope
                        (fun inner ->
                            inner.DeleteExpiredAsync authorization before
                            |> _.GetAwaiter().GetResult()
                            |> Result.defaultWith (fun failure -> raise (InvalidOperationException failure.Message)))
                        (AuditEvent.DeleteExpired before)) }

module AuditLogs =
    let inMemory () : AuditLog = InMemoryAuditLog.create ()

    let ado factory : AuditLog =
        PersistentAuditLog.create (fun key -> EventStore.db factory ("audit/" + key))

    let file baseDir : AuditLog =
        PersistentAuditLog.create (fun key -> EventStore.file (Path.Combine(baseDir, "audit", key + ".jsonl")))
