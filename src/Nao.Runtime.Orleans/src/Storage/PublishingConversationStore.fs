namespace Nao.Runtime.Orleans

open System
open System.Threading.Tasks
open Nao.Agents

/// Tee conversation store: every WRITE is persisted to the wrapped backing store (so history
/// reads stay correct) and ALSO published to the bus as a `ConversationCaptured` event, so the
/// transcript stream flows through the same event pipeline as feedback and observability. The
/// producer (the `SessionGrain`) keeps depending only on `ConversationStore`; swapping the
/// backing store for a database/cloud implementation needs no producer change, and any
/// subscriber can persist or forward the events independently.
module PublishingConversationStore =

    /// Map the runtime's storage record to the transport-neutral event shape.
    let toMessage (m: PersistedMessage) : ConversationMessage =
        let steps =
            m.Steps
            |> Array.map (fun s ->
                ({ Kind = s.Kind
                   Title = s.Title
                   Input = s.Input
                   Output = s.Output }
                : ConversationStep))
            |> Array.toList

        let artifacts =
            m.Artifacts
            |> Array.map (fun value ->
                Artifact.restore
                    (ArtifactSchemaVersion.parse value.SchemaVersion)
                    (ArtifactId.parse value.Id)
                    (IdempotencyKey.parse value.IdempotencyKey)
                    (ArtifactRole.parse value.Role)
                    (ArtifactAddress.parse value.Address)
                    value.Kind
                    value.ContentType
                    value.Payload
                    (ArtifactContentHash.parse value.ContentHash)
                    (ExecutionId.parse value.RecordedBy)
                    value.RecordedAt
                    (value.ParentArtifactIds |> Array.map ArtifactId.parse |> Array.toList))
            |> Array.toList

        { Role = m.Role
          Content = m.Content
          Timestamp = m.Timestamp
          Correlation = m.Correlation
          TurnId = m.TurnId
          Steps = steps
          Attachments = m.Attachments |> Array.toList
          Artifacts = artifacts }

    /// Build the event scope for a conversation write. The action id is the turn id carried
    /// by the messages (empty when none) so each write is attributed to the turn that
    /// produced it.
    let buildScope
        (authorization: AuthorizationScope)
        (conversationName: string)
        (messages: PersistedMessage array)
        : EventScope =
        let turnId =
            messages
            |> Array.tryPick (fun m ->
                if String.IsNullOrEmpty m.TurnId then
                    None
                else
                    Some m.TurnId)
            |> Option.defaultValue ""

        let userId = AuthorizationScope.userId authorization |> UserId.value

        let sessionId =
            AuthorizationScope.sessionId authorization
            |> Option.map SessionId.value
            |> Option.defaultValue ""

        let workspaceId = AuthorizationScope.workspaceId authorization |> WorkspaceId.value

        let sessionKey =
            if String.IsNullOrEmpty sessionId then
                userId
            else
                sprintf "%s/%s" userId sessionId

        let correlation =
            messages
            |> Array.tryHead
            |> Option.map _.Correlation
            |> Option.defaultWith CorrelationContext.root

        EventScope.Create(userId, sessionId, conversationName, workspaceId, turnId, sessionKey, correlation)

    let create (bus: EventBus) (inner: ConversationStore) : ConversationStore =
        let appendAsync
            (authorization: AuthorizationScope)
            (conversationName: string)
            (messages: PersistedMessage array)
            =
            task {
                do! inner.AppendAsync authorization conversationName messages

                if messages.Length > 0 then
                    let signal =
                        MessagesAppended(conversationName, messages |> Array.map toMessage |> Array.toList)

                    do!
                        EventBus.publishAsync
                            (ConversationCaptured(buildScope authorization conversationName messages, signal))
                            bus
            }
            :> Task

        let saveAsync
            (authorization: AuthorizationScope)
            (conversationName: string)
            (messages: PersistedMessage array)
            =
            task {
                do! inner.SaveAsync authorization conversationName messages

                let signal =
                    ConversationSaved(conversationName, messages |> Array.map toMessage |> Array.toList)

                do!
                    EventBus.publishAsync
                        (ConversationCaptured(buildScope authorization conversationName messages, signal))
                        bus
            }
            :> Task

        let loadAsync authorization conversationName =
            inner.LoadAsync authorization conversationName

        let loadByExecutionAsync authorization executionId =
            inner.LoadByExecutionAsync authorization executionId

        let listConversationsAsync authorization =
            inner.ListConversationsAsync authorization

        let deleteConversationAsync authorization conversationName =
            task {
                do! inner.DeleteConversationAsync authorization conversationName

                do!
                    (EventBus.publishAsync
                        (ConversationCaptured(
                            buildScope authorization conversationName [||],
                            ConversationDeleted conversationName
                        ))
                        bus)
            }
            :> Task

        let deleteScopeAsync authorization =
            task {
                do! inner.DeleteScopeAsync authorization

                do!
                    (EventBus.publishAsync
                        (ConversationCaptured(buildScope authorization "" [||], SessionConversationsDeleted))
                        bus)
            }
            :> Task

        { AppendAsync = appendAsync
          SaveAsync = saveAsync
          LoadAsync = loadAsync
          LoadByExecutionAsync = loadByExecutionAsync
          ListConversationsAsync = listConversationsAsync
          DeleteConversationAsync = deleteConversationAsync
          DeleteScopeAsync = deleteScopeAsync }
