namespace Nao.Runtime.Orleans.Grains

open System
open Orleans
open Nao.Agents
open Nao.Agents
open Nao.Runtime.Orleans

/// Serializable record for a conversation message
[<GenerateSerializer>]
type MessageRecord() =
    [<Id(0u)>] member val Role: string = "" with get, set
    [<Id(1u)>] member val Content: string = "" with get, set
    /// Turn this message belongs to.
    [<Id(2u)>] member val TurnId: string = "" with get, set
    /// Process steps for an assistant turn (empty for user messages).
    [<Id(3u)>] member val Steps: ResizeArray<TurnStepRecord> = ResizeArray() with get, set
    /// Names of files attached to a user message (empty for assistant messages).
    [<Id(4u)>] member val Attachments: ResizeArray<string> = ResizeArray() with get, set
    /// Structured data published by tools during this turn.
    [<Id(5u)>] member val Data: ResizeArray<ToolResultDataRecord> = ResizeArray() with get, set

/// Mapping between Orleans serializable records and domain types
module GrainStateMapping =

    let toMessage (record: MessageRecord) : Message =
        let role =
            match record.Role with
            | "System" -> System
            | "Assistant" -> Assistant
            | _ -> User
        { Role = role; Content = record.Content }

    let fromMessage (msg: Message) : MessageRecord =
        let role =
            match msg.Role with
            | System -> "System"
            | User -> "User"
            | Assistant -> "Assistant"
        let r = MessageRecord()
        r.Role <- role
        r.Content <- msg.Content
        r

/// Render recent conversation transcript for an agent turn. The LLM call itself is stateless,
/// so without this a follow-up like "convert it to html" or "use the content above" arrives
/// with no idea what the reference means. We surface recent turns and attachment names so the
/// agent can resolve the reference while the saved transcript stays clean.
module ConversationContextRender =

    /// The last `maxMessages` messages rendered as "Role: content [attached: a, b]" lines.
    /// Returns "" when there is no prior history.
    let recentTranscript (maxMessages: int) (messages: MessageRecord seq) : string =
        let msgs = messages |> Seq.toList
        let recent =
            if maxMessages > 0 && msgs.Length > maxMessages then
                msgs |> List.skip (msgs.Length - maxMessages)
            else msgs
        recent
        |> List.map (fun m ->
            let atts =
                if not (isNull m.Attachments) && m.Attachments.Count > 0 then
                    sprintf " [attached: %s]" (String.Join(", ", m.Attachments))
                else ""
            sprintf "%s: %s%s" m.Role m.Content atts)
        |> fun lines -> String.Join("\n", lines)

    /// Prefix `input` with the recent transcript so the agent has conversational context.
    /// When there is no prior history the input is returned unchanged.
    let withHistory (maxMessages: int) (messages: MessageRecord seq) (input: string) : string =
        let transcript = recentTranscript maxMessages messages
        if String.IsNullOrWhiteSpace transcript then input
        else
            sprintf "Conversation so far (for context — the new request is at the end):\n%s\n\nNew request:\n%s"
                transcript input
