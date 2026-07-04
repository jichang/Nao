namespace Nao.Runtime.Orleans

open System
open System.Threading.Tasks
open Nao.Agents
open Nao.Agents

/// Tee conversation store: every WRITE is persisted to the wrapped backing store (so history
/// reads stay correct) and ALSO published to the bus as a `ConversationCaptured` event, so the
/// transcript stream flows through the same event pipeline as feedback and observability. The
/// producer (the `SessionGrain`) keeps depending only on `IConversationStore`; swapping the
/// backing store for a database/cloud implementation needs no producer change, and any
/// subscriber can persist or forward the events independently.
type PublishingConversationStore(bus: IEventBus, inner: IConversationStore) =

    /// Map the runtime's storage record to the transport-neutral event shape.
    let toMessage (m: PersistedMessage) : ConversationMessage =
        { Role = m.Role
          Content = m.Content
          Timestamp = m.Timestamp
          TurnId = m.TurnId
          Steps =
            m.Steps
            |> Array.map (fun s ->
                ({ Kind = s.Kind; Title = s.Title; Input = s.Input; Output = s.Output } : ConversationStep))
            |> Array.toList
          Attachments = m.Attachments |> Array.toList }

    /// Build the event scope for a conversation write. The action id is the turn id carried
    /// by the messages (empty when none) so each write is attributed to the turn that
    /// produced it.
    let buildScope (sessionId: string) (conversationName: string) (messages: PersistedMessage array) : EventScope =
        let turnId =
            messages
            |> Array.tryPick (fun m -> if String.IsNullOrEmpty m.TurnId then None else Some m.TurnId)
            |> Option.defaultValue ""
        let userId, sid =
            match sessionId.IndexOf('/') with
            | i when i >= 0 -> sessionId.Substring(0, i), sessionId.Substring(i + 1)
            | _ -> sessionId, sessionId
        EventScope.Create(userId, sid, conversationName, "", turnId, sessionId)

    interface IConversationStore with
        member _.AppendAsync (sessionId: string) (conversationName: string) (messages: PersistedMessage array) =
            task {
                do! inner.AppendAsync sessionId conversationName messages
                if messages.Length > 0 then
                    let signal = MessagesAppended(conversationName, messages |> Array.map toMessage |> Array.toList)
                    do! bus.PublishAsync(ConversationCaptured(buildScope sessionId conversationName messages, signal))
            }
            :> Task

        member _.SaveAsync (sessionId: string) (conversationName: string) (messages: PersistedMessage array) =
            task {
                do! inner.SaveAsync sessionId conversationName messages
                let signal = ConversationSaved(conversationName, messages |> Array.map toMessage |> Array.toList)
                do! bus.PublishAsync(ConversationCaptured(buildScope sessionId conversationName messages, signal))
            }
            :> Task

        member _.LoadAsync (sessionId: string) (conversationName: string) =
            inner.LoadAsync sessionId conversationName

        member _.ListConversationsAsync(sessionId: string) =
            inner.ListConversationsAsync sessionId

        member _.ListSessionsAsync() = inner.ListSessionsAsync()

        member _.DeleteConversationAsync (sessionId: string) (conversationName: string) =
            task {
                do! inner.DeleteConversationAsync sessionId conversationName
                do! bus.PublishAsync(
                        ConversationCaptured(
                            buildScope sessionId conversationName [||],
                            ConversationDeleted conversationName))
            }
            :> Task

        member _.DeleteSessionAsync(sessionId: string) =
            task {
                do! inner.DeleteSessionAsync sessionId
                do! bus.PublishAsync(
                        ConversationCaptured(buildScope sessionId "" [||], SessionConversationsDeleted))
            }
            :> Task
