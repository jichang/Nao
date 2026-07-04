namespace Nao.Assistant

open System
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open global.Avalonia.FuncUI.Elmish.ElmishHook
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open global.Elmish
open Nao.Assistant
open Nao.Assistant.ExecutionTrace

/// A single chat session tab view
module SessionView =

    type Message =
        { MessageId: Guid
          Role: string
          Content: string
          Timestamp: DateTime
          /// Server turn id this message belongs to ("" for unsynced/local messages).
          /// Used to attach inline file/task chips to the message that produced them.
          TurnId: string
          /// The whole process behind an assistant answer (empty for user messages).
          Steps: ExecutionTrace.Step list
          /// Sentiment ("positive"/"negative") once feedback has been submitted for this message.
          FeedbackGiven: string option
          /// File names attached to this message, rendered as chips (content not shown).
          Attachments: string list }

    /// Lifecycle of the session's conversation history.
    type HistoryStatus =
        | NeedsLoad
        | Loading
        | Loaded

    /// Whether the session is currently awaiting a chat response.
    type ChatStatus =
        | Idle
        | Sending

    /// State of the optional feedback popup overlay for a single response.
    type FeedbackPrompt =
        { MessageId: Guid
          /// "positive" or "negative".
          Sentiment: string
          Comment: string
          Submitting: bool }

    type SessionState =
        { Id: string
          ServerSessionId: string option
          Title: string
          Messages: Message list
          Input: string
          Chat: ChatStatus
          History: HistoryStatus
          /// Live process steps streamed while the current turn is being processed
          /// (shown as an in-progress assistant bubble); cleared once the turn completes.
          LiveSteps: ExecutionTrace.Step list
          /// Active feedback popup, if the user is currently rating a response.
          Feedback: FeedbackPrompt option
          /// Agent definitions the user can pick from for this session.
          AvailableAgents: string list
          /// The agent this session will use (only applied before the first message).
          SelectedAgent: string
          /// Whether the agent list has been fetched yet.
          AgentsLoaded: bool
          /// A file attached to the next message: (fileName, content).
          AttachedFile: (string * string) option
          /// Files stored for this session (uploads + tool/agent generated).
          Files: SessionFileDto list
          /// Async tasks spawned during this session (with live status).
          Tasks: TaskDto list
          /// Whether the "Files & Tasks" panel is expanded.
          FilesPanelOpen: bool }

    let createNew () =
        { Id = Guid.NewGuid().ToString("N").[..7]
          ServerSessionId = None
          Title = "New Session"
          Messages = []
          Input = ""
          Chat = Idle
          History = Loaded
          LiveSteps = []
          Feedback = None
          AvailableAgents = []
          SelectedAgent = "document"
          AgentsLoaded = false
          AttachedFile = None
          Files = []
          Tasks = []
          FilesPanelOpen = false }

    type Msg =
        | TriggerHistoryLoad
        | HistoryLoaded of Result<Message list, string>
        | InputChanged of string
        | SendPressed
        | SendCompleted of Result<string * string, string>
        | StepsUpdated of ExecutionTrace.Step list
        | FeedbackRequested of messageId: Guid * sentiment: string
        | FeedbackCommentChanged of string
        | FeedbackCancelled
        | FeedbackSubmitPressed
        | FeedbackCompleted of Result<Guid * string, string>
        | TriggerAgentsLoad
        | AgentsListLoaded of string list
        | AgentSelected of string
        | AttachFilePressed
        | FileAttached of string * string
        | AttachmentCleared
        | FilesTasksLoaded of SessionFileDto list * TaskDto list
        | RefreshFilesTasks
        | ToggleFilesPanel
        | DownloadFile of fileId: string * name: string

    /// Legacy marker: older sessions embedded an attached file inside the message text.
    /// Newer messages carry attachment names as structured metadata instead, but we still
    /// parse the marker out of historic messages so their file body is never rendered.
    let private attachmentMarker = "[[NAO_ATTACHMENT:"

    /// Split a legacy stored message back into (visibleText, attachmentNames), hiding the body.
    let private parseAttachments (raw: string) : string * string list =
        if String.IsNullOrEmpty raw || not (raw.Contains attachmentMarker) then
            raw, []
        else
            let visible = raw.Substring(0, raw.IndexOf attachmentMarker).TrimEnd()
            let names =
                System.Text.RegularExpressions.Regex.Matches(raw, @"\[\[NAO_ATTACHMENT:(.*?)\]\]")
                |> Seq.cast<System.Text.RegularExpressions.Match>
                |> Seq.map (fun m -> m.Groups.[1].Value)
                |> Seq.toList
            visible, names

    let private toUiMessage (m: MessageDto) : Message =
        let role =
            match (if isNull m.Role then "" else m.Role).ToLowerInvariant() with
            | "user" -> "You"
            | "assistant" -> "Nao"
            | r -> r
        let steps =
            if isNull (box m.Steps) then []
            else
                m.Steps
                |> Array.map (fun s ->
                    { Kind = (if isNull s.Kind then "" else s.Kind)
                      Name = (if isNull s.Title then "" else s.Title)
                      Args = (if isNull s.Input then "" else s.Input)
                      Result = (if isNull s.Output then "" else s.Output) } : ExecutionTrace.Step)
                |> Array.toList
        let content = if isNull m.Content then "" else m.Content
        let structured = if isNull (box m.Attachments) then [] else m.Attachments |> Array.toList
        // Prefer structured attachment metadata; fall back to the legacy embedded marker.
        let visible, attachments =
            if not structured.IsEmpty then content, structured
            else parseAttachments content
        { MessageId = Guid.NewGuid()
          Role = role
          Content = visible
          Timestamp = DateTime.Now
          TurnId = (if isNull m.TurnId then "" else m.TurnId)
          Steps = steps
          FeedbackGiven = None
          Attachments = attachments }

    /// Build a fresh chat message with a unique id and no feedback yet.
    let private mkMessage (role: string) (content: string) : Message =
        { MessageId = Guid.NewGuid()
          Role = role
          Content = content
          Timestamp = DateTime.Now
          TurnId = ""
          Steps = []
          FeedbackGiven = None
          Attachments = [] }

    let private cmdOfSub (sub: ('msg -> unit) -> unit) : Cmd<'msg> =
        [ sub ]

    let private loadHistoryCmd (client: NaoClient) (sessionId: string) : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            Task.Run(fun () ->
                task {
                    try
                        let! msgs = client.LoadSessionHistoryAsync(sessionId)
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (HistoryLoaded (Ok (msgs |> List.map toUiMessage))))
                    with ex ->
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (HistoryLoaded (Error ex.Message)))
                }
                :> Task)
            |> ignore)

    let private sendCmd (client: NaoClient) (model: SessionState) (text: string) (attachments: (string * string) list) : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            Task.Run(fun () ->
                task {
                    try
                        let! sessionId =
                            task {
                                match model.ServerSessionId with
                                | Some id -> return id
                                | None ->
                                    return! client.CreateSessionAsync(
                                        { SessionStartRequest.Default with AgentName = model.SelectedAgent })
                            }

                        // Stream the live process steps as the server reports them, so the UI
                        // can show "what's been done so far" while the answer is produced.
                        let onSteps (steps: TurnStepDto list) =
                            let uiSteps =
                                steps
                                |> List.map (fun s ->
                                    { Kind = s.Kind; Name = s.Title; Args = s.Input; Result = s.Output } : ExecutionTrace.Step)
                            Dispatcher.UIThread.Post(fun () -> dispatch (StepsUpdated uiSteps))

                        let! response = client.ChatAsync(sessionId, text, attachments, onSteps)
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (SendCompleted (Ok (sessionId, response))))
                    with ex ->
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (SendCompleted (Error ex.Message)))
                }
                :> Task)
            |> ignore)

    let private loadAgentsCmd (client: NaoClient) : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            Task.Run(fun () ->
                task {
                    try
                        let! agents = client.ListAgentsAsync()
                        let names = agents |> List.map (fun a -> a.Name)
                        Dispatcher.UIThread.Post(fun () -> dispatch (AgentsListLoaded names))
                    with _ -> ()
                }
                :> Task)
            |> ignore)

    let private attachFileCmd : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            // The file picker must run on the UI thread (it needs the top-level window).
            Dispatcher.UIThread.Post(fun () ->
                (task {
                    try
                        let! picked = UiContext.pickTextFileAsync ()
                        match picked with
                        | Some (name, content) -> dispatch (FileAttached (name, content))
                        | None -> ()
                    with _ -> ()
                }
                :> Task)
                |> ignore))

    let private loadFilesTasksCmd (client: NaoClient) (sessionKey: string) : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            Task.Run(fun () ->
                task {
                    try
                        let! files = client.ListSessionFilesAsync(sessionKey)
                        let! tasks = client.ListSessionTasksAsync(sessionKey)
                        Dispatcher.UIThread.Post(fun () -> dispatch (FilesTasksLoaded (files, tasks)))
                    with _ -> ()
                }
                :> Task)
            |> ignore)

    /// Re-poll files/tasks after a short delay (used to track in-progress async tasks).
    let private pollFilesTasksCmd (client: NaoClient) (sessionKey: string) : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            Task.Run(fun () ->
                task {
                    do! Task.Delay 1500
                    try
                        let! files = client.ListSessionFilesAsync(sessionKey)
                        let! tasks = client.ListSessionTasksAsync(sessionKey)
                        Dispatcher.UIThread.Post(fun () -> dispatch (FilesTasksLoaded (files, tasks)))
                    with _ -> ()
                }
                :> Task)
            |> ignore)

    let private downloadFileCmd (client: NaoClient) (sessionKey: string) (fileId: string) (name: string) : Cmd<Msg> =
        cmdOfSub (fun _ ->
            Task.Run(fun () ->
                task {
                    try
                        let! bytes = client.DownloadSessionFileAsync(sessionKey, fileId)
                        Dispatcher.UIThread.Post(fun () ->
                            (UiContext.saveBytesAsync name bytes :> Task) |> ignore)
                    with _ -> ()
                }
                :> Task)
            |> ignore)

    let private submitFeedbackCmd
        (client: NaoClient)
        (sessionId: string)
        (messageId: Guid)
        (sentiment: string)
        (comment: string)
        : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            Task.Run(fun () ->
                task {
                    try
                        let comment = if String.IsNullOrWhiteSpace comment then None else Some comment
                        let! _ = client.SubmitFeedbackAsync(sessionId, sentiment, ?comment = comment)
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (FeedbackCompleted (Ok (messageId, sentiment))))
                    with ex ->
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (FeedbackCompleted (Error ex.Message)))
                }
                :> Task)
            |> ignore)

    let update (client: NaoClient) (msg: Msg) (model: SessionState) : SessionState * Cmd<Msg> =
        match msg with
        | TriggerHistoryLoad ->
            match model.ServerSessionId with
            | Some sessionId when model.History = NeedsLoad ->
                { model with History = Loading },
                Cmd.batch [ loadHistoryCmd client sessionId; loadFilesTasksCmd client sessionId ]
            | _ ->
                model, Cmd.none

        | HistoryLoaded (Ok restored) ->
            { model with
                Messages = restored
                History = Loaded },
            Cmd.none

        | HistoryLoaded (Error err) ->
            let loadError =
                mkMessage "System" (sprintf "Failed to load history: %s" err)
            { model with
                Messages = model.Messages @ [ loadError ]
                History = Loaded },
            Cmd.none

        | InputChanged text ->
            { model with Input = text }, Cmd.none

        | SendPressed ->
            let canSend =
                model.Chat = Idle
                && model.History = Loaded
                && not (String.IsNullOrWhiteSpace model.Input)

            if not canSend then
                model, Cmd.none
            else
                let text = model.Input
                let attachments =
                    match model.AttachedFile with
                    | Some (name, content) -> [ name, content ]
                    | None -> []
                let attachmentNames = attachments |> List.map fst
                let userMsg = { mkMessage "You" text with Attachments = attachmentNames }
                { model with
                    Messages = model.Messages @ [ userMsg ]
                    Input = ""
                    Chat = Sending
                    LiveSteps = []
                    AttachedFile = None },
                sendCmd client model text attachments

        | SendCompleted (Ok (sessionId, response)) ->
            let replyMsg = mkMessage "Nao" response
            // Reload the persisted transcript so the reply carries its full process
            // (tool/sub-agent steps), which the chat call itself doesn't return. Also
            // refresh the session's files/tasks so any newly generated output appears.
            { model with
                ServerSessionId = Some sessionId
                Messages = model.Messages @ [ replyMsg ]
                LiveSteps = []
                Chat = Idle },
            Cmd.batch [ loadHistoryCmd client sessionId; loadFilesTasksCmd client sessionId ]

        | SendCompleted (Error err) ->
            let errorMsg = mkMessage "System" (sprintf "Error: %s" err)
            { model with Messages = model.Messages @ [ errorMsg ]; LiveSteps = []; Chat = Idle }, Cmd.none

        | StepsUpdated steps ->
            { model with LiveSteps = steps }, Cmd.none

        | FeedbackRequested (messageId, sentiment) ->
            { model with
                Feedback = Some { MessageId = messageId; Sentiment = sentiment; Comment = ""; Submitting = false } },
            Cmd.none

        | FeedbackCommentChanged text ->
            match model.Feedback with
            | Some fb -> { model with Feedback = Some { fb with Comment = text } }, Cmd.none
            | None -> model, Cmd.none

        | FeedbackCancelled ->
            { model with Feedback = None }, Cmd.none

        | FeedbackSubmitPressed ->
            match model.Feedback, model.ServerSessionId with
            | Some fb, Some sessionId when not fb.Submitting ->
                { model with Feedback = Some { fb with Submitting = true } },
                submitFeedbackCmd client sessionId fb.MessageId fb.Sentiment fb.Comment
            | Some _, None ->
                // No server-side session yet (nothing to attach feedback to) — just close.
                { model with Feedback = None }, Cmd.none
            | _ -> model, Cmd.none

        | FeedbackCompleted (Ok (messageId, sentiment)) ->
            let messages =
                model.Messages
                |> List.map (fun m ->
                    if m.MessageId = messageId then { m with FeedbackGiven = Some sentiment } else m)
            { model with Messages = messages; Feedback = None }, Cmd.none

        | FeedbackCompleted (Error err) ->
            let errorMsg = mkMessage "System" (sprintf "Failed to send feedback: %s" err)
            { model with Messages = model.Messages @ [ errorMsg ]; Feedback = None }, Cmd.none

        | TriggerAgentsLoad ->
            if model.AgentsLoaded then model, Cmd.none
            else model, loadAgentsCmd client

        | AgentsListLoaded names ->
            { model with AvailableAgents = names; AgentsLoaded = true }, Cmd.none

        | AgentSelected name ->
            { model with SelectedAgent = name }, Cmd.none

        | AttachFilePressed ->
            model, attachFileCmd

        | FileAttached (name, content) ->
            { model with AttachedFile = Some (name, content) }, Cmd.none

        | AttachmentCleared ->
            { model with AttachedFile = None }, Cmd.none

        | FilesTasksLoaded (files, tasks) ->
            // Keep polling while any task is still in flight so progress updates live.
            let anyRunning =
                tasks |> List.exists (fun t -> t.Status = AsyncTasks.Status.Pending || t.Status = AsyncTasks.Status.Running)
            let poll =
                match model.ServerSessionId with
                | Some key when anyRunning -> pollFilesTasksCmd client key
                | _ -> Cmd.none
            { model with Files = files; Tasks = tasks }, poll

        | RefreshFilesTasks ->
            match model.ServerSessionId with
            | Some key -> model, loadFilesTasksCmd client key
            | None -> model, Cmd.none

        | ToggleFilesPanel ->
            { model with FilesPanelOpen = not model.FilesPanelOpen }, Cmd.none

        | DownloadFile (fileId, name) ->
            match model.ServerSessionId with
            | Some key -> model, downloadFileCmd client key fileId name
            | None -> model, Cmd.none

    // ─── Files & Tasks rendering ───

    /// Human-readable byte size.
    let private fmtSize (bytes: int64) =
        if bytes < 1024L then sprintf "%d B" bytes
        elif bytes < 1024L * 1024L then sprintf "%.1f KB" (float bytes / 1024.0)
        else sprintf "%.1f MB" (float bytes / 1048576.0)

    /// Display label + accent brush for a task status.
    let private taskStatusInfo (status: string) : string * IBrush =
        match status with
        | s when s = AsyncTasks.Status.Completed -> "Completed", Theme.success
        | s when s = AsyncTasks.Status.Failed -> "Failed", Theme.danger
        | s when s = AsyncTasks.Status.Running -> "Running", Theme.accent
        | _ -> "Pending", Theme.warning

    /// A small clickable chip (inline file/task tag).
    let private chip (icon: string) (label: string) (accentBrush: IBrush) (onClick: unit -> unit) : Avalonia.FuncUI.Types.IView =
        Button.create [
            Button.margin (0.0, 4.0, 4.0, 0.0)
            Button.padding (8.0, 4.0)
            Button.background Theme.surfaceInset
            Button.borderThickness 1.0
            Button.borderBrush Theme.border
            Button.cornerRadius 6.0
            Button.onClick (fun _ -> onClick ())
            Button.content (
                StackPanel.create [
                    StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 6.0
                    StackPanel.children [
                        TextBlock.create [ TextBlock.text icon; TextBlock.fontSize 12.0; TextBlock.foreground accentBrush ]
                        TextBlock.create [ TextBlock.text label; TextBlock.fontSize 12.0; TextBlock.foreground Theme.textSecondary ]
                    ]
                ])
        ]

    /// Inline file/task chips for the assistant message of a given turn.
    let private inlineChips (dispatch: Msg -> unit) (files: SessionFileDto list) (tasks: TaskDto list) (turnId: string) : Avalonia.FuncUI.Types.IView list =
        if String.IsNullOrEmpty turnId then []
        else
            let turnTasks = tasks |> List.filter (fun t -> t.TurnId = turnId)
            let resultIds =
                turnTasks
                |> List.choose (fun t -> if String.IsNullOrEmpty t.ResultFileId then None else Some t.ResultFileId)
                |> Set.ofList
            // Generated files that aren't already represented by a task chip.
            let genFiles =
                files
                |> List.filter (fun f -> f.Source = "generated" && f.TurnId = turnId && not (resultIds.Contains f.Id))
            let taskChips =
                turnTasks
                |> List.map (fun t ->
                    let label, brush = taskStatusInfo t.Status
                    let onClick () =
                        if t.Status = AsyncTasks.Status.Completed && not (String.IsNullOrEmpty t.ResultFileId) then
                            let name =
                                files |> List.tryFind (fun f -> f.Id = t.ResultFileId)
                                |> Option.map (fun f -> f.DisplayName) |> Option.defaultValue t.Title
                            dispatch (DownloadFile(t.ResultFileId, name))
                        else dispatch ToggleFilesPanel
                    chip "\u2699" (sprintf "%s — %s" t.Title label) brush onClick)
            let fileChips =
                genFiles
                |> List.map (fun f ->
                    chip "\U0001F4C4" f.DisplayName Theme.accent (fun () -> dispatch (DownloadFile(f.Id, f.DisplayName))))
            if taskChips.IsEmpty && fileChips.IsEmpty then []
            else
                [ WrapPanel.create [
                      WrapPanel.margin (0.0, 4.0, 0.0, 0.0)
                      WrapPanel.children (taskChips @ fileChips) ] ]

    /// One file row inside the Files & Tasks panel.
    let private fileRow (dispatch: Msg -> unit) (f: SessionFileDto) : Avalonia.FuncUI.Types.IView =
        Border.create [
            Border.padding (8.0, 6.0)
            Border.cornerRadius 6.0
            Border.background Theme.surfaceInset
            Border.child (
                Grid.create [
                    Grid.columnDefinitions "*,Auto"
                    Grid.children [
                        StackPanel.create [
                            Grid.column 0
                            StackPanel.children [
                                TextBlock.create [
                                    TextBlock.text (sprintf "\U0001F4C4 %s" f.DisplayName)
                                    TextBlock.fontSize 12.0
                                    TextBlock.foreground Theme.textPrimary
                                    TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                ]
                                TextBlock.create [
                                    TextBlock.text (sprintf "%s · %s" f.Source (fmtSize f.Size))
                                    TextBlock.fontSize 10.0
                                    TextBlock.foreground Theme.textMuted
                                ]
                            ]
                        ]
                        Button.create [
                            Grid.column 1
                            Button.content (Localization.current ()).Download
                            Button.fontSize 11.0
                            Button.verticalAlignment VerticalAlignment.Center
                            Button.onClick (fun _ -> dispatch (DownloadFile(f.Id, f.DisplayName)))
                        ]
                    ]
                ]
            )
        ]

    /// The collapsible files panel shown above the conversation.
    let private filesTasksPanel (dispatch: Msg -> unit) (model: SessionState) : Avalonia.FuncUI.Types.IView =
        Border.create [
            Border.borderThickness (0.0, 0.0, 0.0, 1.0)
            Border.borderBrush Theme.borderSubtle
            Border.background Theme.surface
            Border.child (
                StackPanel.create [
                    StackPanel.children [
                        Button.create [
                            Button.horizontalAlignment HorizontalAlignment.Stretch
                            Button.horizontalContentAlignment HorizontalAlignment.Left
                            Button.background Theme.transparent
                            Button.borderThickness 0.0
                            Button.padding (12.0, 8.0)
                            Button.onClick (fun _ -> dispatch ToggleFilesPanel)
                            Button.content (
                                TextBlock.create [
                                    TextBlock.text (
                                        let t = Localization.current ()
                                        sprintf "%s  %s  \u00b7  %d %s"
                                            (if model.FilesPanelOpen then "\u25BE" else "\u25B8")
                                            t.FilesAndTasks
                                            model.Files.Length t.FilesWord)
                                    TextBlock.fontSize 12.0
                                    TextBlock.foreground Theme.textSecondary
                                ])
                        ]
                        if model.FilesPanelOpen then
                            ScrollViewer.create [
                                ScrollViewer.maxHeight 220.0
                                ScrollViewer.content (
                                    StackPanel.create [
                                        StackPanel.margin (12.0, 0.0, 12.0, 10.0)
                                        StackPanel.spacing 6.0
                                        StackPanel.children [
                                            if model.Files.IsEmpty then
                                                TextBlock.create [
                                                    TextBlock.text (Localization.current ()).NoFilesOrTasks
                                                    TextBlock.fontSize 12.0
                                                    TextBlock.foreground Theme.textMuted
                                                ]
                                            for f in model.Files do
                                                fileRow dispatch f
                                        ]
                                    ])
                            ]
                    ]
                ]
            )
        ]

    let view (dispatch: Msg -> unit) (model: SessionState) : Avalonia.FuncUI.Types.IView =
        let canSend =
            model.Chat = Idle
            && model.History = Loaded

        let mainContent : Avalonia.FuncUI.Types.IView =
          Grid.create [
            Grid.rowDefinitions "Auto,*,Auto"
            Grid.children [
                // Collapsible "Files & Tasks" panel at the top.
                Grid.create [
                    Grid.row 0
                    Grid.children [ filesTasksPanel dispatch model ]
                ]

                // Composer (input area) at the bottom — the ChatInput business
                // component, styled consistently with the rest of the app.
                Grid.create [
                    Grid.row 2
                    Grid.children [
                        ChatInput.render
                            { Text = model.Input
                              Placeholder = (Localization.current ()).ComposerPlaceholder
                              CanSend = canSend
                              AttachedFileName = model.AttachedFile |> Option.map fst
                              Agents = model.AvailableAgents
                              SelectedAgent = model.SelectedAgent
                              AgentSelectable = model.ServerSessionId.IsNone
                              OnTextChanged = fun text -> dispatch (InputChanged text)
                              OnAttach = fun () -> dispatch AttachFilePressed
                              OnClearAttachment = fun () -> dispatch AttachmentCleared
                              OnAgentSelected = fun s -> dispatch (AgentSelected s)
                              OnSend = fun () -> dispatch SendPressed }
                    ]
                ]

                // Messages area
                ScrollViewer.create [
                    Grid.row 1
                    ScrollViewer.verticalScrollBarVisibility Primitives.ScrollBarVisibility.Auto
                    ScrollViewer.content (
                        StackPanel.create [
                            // Padding lives on the content (not the ScrollViewer) so it is
                            // part of the scrollable extent — otherwise the bottom of the
                            // last message is clipped when scrolled to the end.
                            StackPanel.margin (12.0, 8.0)
                            StackPanel.orientation Orientation.Vertical
                            StackPanel.spacing 8.0
                            StackPanel.children [
                                if model.History = Loading then
                                    StackPanel.create [
                                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                                        StackPanel.margin (0.0, 40.0, 0.0, 0.0)
                                        StackPanel.spacing 10.0
                                        StackPanel.children [
                                            ProgressBar.create [
                                                ProgressBar.width 220.0
                                                ProgressBar.height 8.0
                                                ProgressBar.isIndeterminate true
                                            ]
                                            TextBlock.create [
                                                TextBlock.text (Localization.current ()).LoadingHistory
                                                TextBlock.foreground Theme.textSecondary
                                                TextBlock.horizontalAlignment HorizontalAlignment.Center
                                            ]
                                        ]
                                    ]
                                elif model.Messages.IsEmpty then
                                    TextBlock.create [
                                        TextBlock.text (Localization.current ()).StartConversation
                                        TextBlock.foreground Theme.textMuted
                                        TextBlock.horizontalAlignment HorizontalAlignment.Center
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                        TextBlock.margin (0.0, 40.0, 0.0, 0.0)
                                    ]
                                else
                                    for msg in model.Messages do
                                        MessageBubble.render
                                            { Role = msg.Role
                                              IsUser = (msg.Role = "You")
                                              Content = msg.Content
                                              Attachments = msg.Attachments
                                              Header =
                                                // The process (what Nao did) reads above the answer.
                                                if msg.Role = "Nao" && not msg.Steps.IsEmpty then
                                                    [ ExecutionTrace.renderExpandable false msg.Steps ]
                                                else []
                                              Footer =
                                                if msg.Role = "Nao" then
                                                    MessageActions.render
                                                        { FeedbackGiven = msg.FeedbackGiven
                                                          OnFeedback = fun sentiment -> dispatch (FeedbackRequested (msg.MessageId, sentiment)) }
                                                    @ inlineChips dispatch model.Files model.Tasks msg.TurnId
                                                else [] }

                                    // While a turn is processing, show a live in-progress assistant
                                    // bubble: the steps done so far, then a "Working..." indicator.
                                    // It is replaced by the persisted answer once the turn completes.
                                    if model.Chat = Sending then
                                        MessageBubble.render
                                            { Role = "Nao"
                                              IsUser = false
                                              Content = ""
                                              Attachments = []
                                              Header =
                                                if not model.LiveSteps.IsEmpty then
                                                    [ ExecutionTrace.render model.LiveSteps ]
                                                else []
                                              Footer =
                                                [ StackPanel.create [
                                                      StackPanel.orientation Orientation.Horizontal
                                                      StackPanel.spacing 8.0
                                                      StackPanel.margin (0.0, 6.0, 0.0, 0.0)
                                                      StackPanel.children [
                                                          ProgressBar.create [
                                                              ProgressBar.width 120.0
                                                              ProgressBar.height 4.0
                                                              ProgressBar.isIndeterminate true
                                                          ]
                                                          TextBlock.create [
                                                              TextBlock.text (Localization.current ()).Working
                                                              TextBlock.fontSize 11.0
                                                              TextBlock.foreground Theme.textSecondary
                                                              TextBlock.verticalAlignment VerticalAlignment.Center
                                                          ]
                                                      ]
                                                  ] ] }
                            ]
                        ]
                    )
                ]
            ]
          ]

        Panel.create [
            Panel.children [
                mainContent
                if model.Feedback.IsSome then
                    let fb = model.Feedback.Value
                    FeedbackDialog.render
                        { Sentiment = fb.Sentiment
                          Comment = fb.Comment
                          Submitting = fb.Submitting
                          OnCommentChanged = fun t -> dispatch (FeedbackCommentChanged t)
                          OnCancel = fun () -> dispatch FeedbackCancelled
                          OnSubmit = fun () -> dispatch FeedbackSubmitPressed }
            ]
        ]

