namespace Nao.Runtime.Orleans

open System
open System.Threading.Tasks
open Nao.Agents
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

        let data =
            if isNull (box m.Data) then
                []
            else
                m.Data
                |> Array.map (fun value ->
                    { AgentContextData.Kind = value.Kind
                      ContentType = value.ContentType
                      Payload = value.Payload })
                |> Array.toList

        { Role = m.Role
          Content = m.Content
          Timestamp = m.Timestamp
          TurnId = m.TurnId
          Steps = steps
          Attachments = m.Attachments |> Array.toList
          Data = data }

    /// Build the event scope for a conversation write. The action id is the turn id carried
    /// by the messages (empty when none) so each write is attributed to the turn that
    /// produced it.
    let buildScope (sessionId: string) (conversationName: string) (messages: PersistedMessage array) : EventScope =
        let turnId =
            messages
            |> Array.tryPick (fun m ->
                if String.IsNullOrEmpty m.TurnId then
                    None
                else
                    Some m.TurnId)
            |> Option.defaultValue ""

        let userId, sid =
            match sessionId.IndexOf('/') with
            | i when i >= 0 -> sessionId.Substring(0, i), sessionId.Substring(i + 1)
            | _ -> sessionId, sessionId

        EventScope.Create(userId, sid, conversationName, "", turnId, sessionId)

    let create (bus: EventBus) (inner: ConversationStore) : ConversationStore =
        let appendAsync (sessionId: string) (conversationName: string) (messages: PersistedMessage array) =
            task {
                do! inner.AppendAsync sessionId conversationName messages

                if messages.Length > 0 then
                    let signal =
                        MessagesAppended(conversationName, messages |> Array.map toMessage |> Array.toList)

                    do!
                        EventBus.publishAsync
                            (ConversationCaptured(buildScope sessionId conversationName messages, signal))
                            bus
            }
            :> Task

        let saveAsync (sessionId: string) (conversationName: string) (messages: PersistedMessage array) =
            task {
                do! inner.SaveAsync sessionId conversationName messages

                let signal =
                    ConversationSaved(conversationName, messages |> Array.map toMessage |> Array.toList)

                do!
                    EventBus.publishAsync
                        (ConversationCaptured(buildScope sessionId conversationName messages, signal))
                        bus
            }
            :> Task

        let loadAsync (sessionId: string) (conversationName: string) =
            inner.LoadAsync sessionId conversationName

        let listConversationsAsync (sessionId: string) = inner.ListConversationsAsync sessionId

        let listSessionsAsync () = inner.ListSessionsAsync()

        let deleteConversationAsync (sessionId: string) (conversationName: string) =
            task {
                do! inner.DeleteConversationAsync sessionId conversationName

                do!
                    (EventBus.publishAsync
                        (ConversationCaptured(
                            buildScope sessionId conversationName [||],
                            ConversationDeleted conversationName
                        ))
                        bus)
            }
            :> Task

        let deleteSessionAsync (sessionId: string) =
            task {
                do! inner.DeleteSessionAsync sessionId

                do!
                    (EventBus.publishAsync
                        (ConversationCaptured(buildScope sessionId "" [||], SessionConversationsDeleted))
                        bus)
            }
            :> Task

        { AppendAsync = appendAsync
          SaveAsync = saveAsync
          LoadAsync = loadAsync
          ListConversationsAsync = listConversationsAsync
          ListSessionsAsync = listSessionsAsync
          DeleteConversationAsync = deleteConversationAsync
          DeleteSessionAsync = deleteSessionAsync }
