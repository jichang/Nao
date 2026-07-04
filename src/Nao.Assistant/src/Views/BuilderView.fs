namespace Nao.Assistant

open System
open System.Threading.Tasks
open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open global.Elmish
open Nao.Assistant

/// "Workshop" view — list, generate (via the LLM), and save custom tools and agents,
/// plus upload reusable knowledge files for retrieval-augmented answers.
module BuilderView =

    type Tab =
        | ToolsTab
        | AgentsTab
        | KnowledgeTab

    /// A draft definition awaiting the user's review before it is saved.
    type Draft =
        { Kind: string // "tool" | "agent"
          Name: string
          Json: string }

    type BuilderState =
        { Active: Tab
          Tools: DefinitionInfoDto list
          Agents: DefinitionInfoDto list
          Knowledge: KnowledgeFileDto list
          ToolReq: string
          AgentReq: string
          Draft: Draft option
          Busy: bool
          Status: string
          Loaded: bool }

    let init () : BuilderState =
        { Active = ToolsTab
          Tools = []
          Agents = []
          Knowledge = []
          ToolReq = ""
          AgentReq = ""
          Draft = None
          Busy = false
          Status = ""
          Loaded = false }

    type Msg =
        | Activate of Tab
        | TriggerLoad
        | ToolsLoaded of DefinitionInfoDto list
        | AgentsLoaded of DefinitionInfoDto list
        | KnowledgeLoaded of KnowledgeFileDto list
        | ToolReqChanged of string
        | AgentReqChanged of string
        | GenerateTool
        | GenerateAgent
        | DraftGenerated of Draft
        | DraftJsonChanged of string
        | SaveDraft
        | DraftSaved of string
        | DiscardDraft
        | UploadKnowledgePressed
        | KnowledgeUploaded of string
        | DeleteKnowledge of string
        | KnowledgeDeleted
        | Failed of string

    let private cmdOfSub (sub: ('msg -> unit) -> unit) : Cmd<'msg> = [ sub ]

    let private run (work: (Msg -> unit) -> Task) : Cmd<Msg> =
        cmdOfSub (fun dispatch ->
            Task.Run(fun () ->
                (task {
                    try
                        do! work dispatch
                    with ex ->
                        Dispatcher.UIThread.Post(fun () -> dispatch (Failed ex.Message))
                })
                :> Task)
            |> ignore)

    let private post (dispatch: Msg -> unit) (msg: Msg) =
        Dispatcher.UIThread.Post(fun () -> dispatch msg)

    let private loadAllCmd (client: NaoClient) : Cmd<Msg> =
        run (fun dispatch ->
            task {
                let! tools = client.ListToolsAsync()
                post dispatch (ToolsLoaded tools)
                let! agents = client.ListAgentsAsync()
                post dispatch (AgentsLoaded agents)
                let! knowledge = client.ListKnowledgeAsync()
                post dispatch (KnowledgeLoaded knowledge)
            }
            :> Task)

    let private loadKnowledgeCmd (client: NaoClient) : Cmd<Msg> =
        run (fun dispatch ->
            task {
                let! knowledge = client.ListKnowledgeAsync()
                post dispatch (KnowledgeLoaded knowledge)
            }
            :> Task)

    let update (client: NaoClient) (msg: Msg) (model: BuilderState) : BuilderState * Cmd<Msg> =
        match msg with
        | Activate tab ->
            { model with Active = tab }, Cmd.none

        | TriggerLoad ->
            if model.Loaded then model, Cmd.none
            else { model with Loaded = true }, loadAllCmd client

        | ToolsLoaded tools ->
            { model with Tools = tools }, Cmd.none

        | AgentsLoaded agents ->
            { model with Agents = agents }, Cmd.none

        | KnowledgeLoaded knowledge ->
            { model with Knowledge = knowledge }, Cmd.none

        | ToolReqChanged text ->
            { model with ToolReq = text }, Cmd.none

        | AgentReqChanged text ->
            { model with AgentReq = text }, Cmd.none

        | GenerateTool ->
            if model.Busy || String.IsNullOrWhiteSpace model.ToolReq then model, Cmd.none
            else
                { model with Busy = true; Status = "Generating tool..." },
                run (fun dispatch ->
                    task {
                        let! dto = client.GenerateToolAsync(model.ToolReq)
                        post dispatch (DraftGenerated { Kind = "tool"; Name = dto.Name; Json = dto.Json })
                    }
                    :> Task)

        | GenerateAgent ->
            if model.Busy || String.IsNullOrWhiteSpace model.AgentReq then model, Cmd.none
            else
                { model with Busy = true; Status = "Generating agent..." },
                run (fun dispatch ->
                    task {
                        let! dto = client.GenerateAgentAsync(model.AgentReq)
                        post dispatch (DraftGenerated { Kind = "agent"; Name = dto.Name; Json = dto.Json })
                    }
                    :> Task)

        | DraftGenerated draft ->
            { model with Draft = Some draft; Busy = false; Status = "Review the draft, then Save." }, Cmd.none

        | DraftJsonChanged text ->
            match model.Draft with
            | Some d -> { model with Draft = Some { d with Json = text } }, Cmd.none
            | None -> model, Cmd.none

        | SaveDraft ->
            match model.Draft with
            | Some d when not model.Busy ->
                { model with Busy = true; Status = "Saving..." },
                run (fun dispatch ->
                    task {
                        let! path =
                            if d.Kind = "tool"
                            then client.RegisterToolJsonAsync(d.Name, d.Json)
                            else client.RegisterAgentJsonAsync(d.Name, d.Json)
                        post dispatch (DraftSaved path)
                    }
                    :> Task)
            | _ -> model, Cmd.none

        | DraftSaved _ ->
            { model with Draft = None; Busy = false; Status = "Saved. Reloading..."; Loaded = false },
            loadAllCmd client

        | DiscardDraft ->
            { model with Draft = None; Status = "" }, Cmd.none

        | UploadKnowledgePressed ->
            { model with Status = "Choosing file..." },
            run (fun dispatch ->
                task {
                    let! picked = UiContext.pickTextFileAsync ()
                    match picked with
                    | Some (name, content) ->
                        do! client.UploadKnowledgeAsync(name, content)
                        post dispatch (KnowledgeUploaded name)
                    | None -> post dispatch (KnowledgeUploaded "")
                }
                :> Task)

        | KnowledgeUploaded name ->
            let status = if String.IsNullOrEmpty name then "" else sprintf "Uploaded %s." name
            { model with Status = status }, loadKnowledgeCmd client

        | DeleteKnowledge name ->
            { model with Status = sprintf "Deleting %s..." name },
            run (fun dispatch ->
                task {
                    let! _ = client.DeleteKnowledgeAsync(name)
                    post dispatch KnowledgeDeleted
                }
                :> Task)

        | KnowledgeDeleted ->
            { model with Status = "Deleted." }, loadKnowledgeCmd client

        | Failed err ->
            { model with Busy = false; Status = sprintf "Error: %s" err }, Cmd.none

    // ─── View helpers ───

    let private cardBg : IBrush = Theme.surfaceRaised
    let private subtle : IBrush = Theme.textSecondary

    let private definitionRow (d: DefinitionInfoDto) : Avalonia.FuncUI.Types.IView =
        Border.create [
            Border.padding (10.0, 8.0)
            Border.cornerRadius 10.0
            Border.background cardBg
            Border.child (
                StackPanel.create [
                    StackPanel.spacing 2.0
                    StackPanel.children [
                        StackPanel.create [
                            StackPanel.orientation Orientation.Horizontal
                            StackPanel.spacing 8.0
                            StackPanel.children [
                                TextBlock.create [
                                    TextBlock.text d.Name
                                    TextBlock.fontWeight FontWeight.SemiBold
                                    TextBlock.foreground Brushes.White
                                ]
                                Border.create [
                                    Border.background Theme.surfaceRaised
                                    Border.cornerRadius 4.0
                                    Border.padding (5.0, 1.0)
                                    Border.child (
                                        TextBlock.create [
                                            TextBlock.text d.Source
                                            TextBlock.fontSize 10.0
                                            TextBlock.foreground subtle
                                        ])
                                ]
                            ]
                        ]
                        TextBlock.create [
                            TextBlock.text d.Description
                            TextBlock.fontSize 12.0
                            TextBlock.textWrapping TextWrapping.Wrap
                            TextBlock.foreground subtle
                        ]
                    ]
                ])
        ]

    let private draftEditor (dispatch: Msg -> unit) (model: BuilderState) : Avalonia.FuncUI.Types.IView list =
        match model.Draft with
        | Some d ->
            [ Border.create [
                Border.padding (12.0, 10.0)
                Border.cornerRadius 12.0
                Border.background Theme.surfaceInset
                Border.borderThickness 1.0
                Border.borderBrush Theme.accentSoft
                Border.child (
                    StackPanel.create [
                        StackPanel.spacing 8.0
                        StackPanel.children [
                            TextBlock.create [
                                TextBlock.text (sprintf "%s %s: %s" (Localization.current ()).GeneratedLabel d.Kind d.Name)
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.foreground Brushes.White
                            ]
                            TextBox.create [
                                TextBox.text d.Json
                                TextBox.acceptsReturn true
                                TextBox.minHeight 200.0
                                TextBox.fontFamily (FontFamily("Consolas, Menlo, monospace"))
                                TextBox.fontSize 12.0
                                TextBox.onTextChanged (fun t -> dispatch (DraftJsonChanged t))
                            ]
                            StackPanel.create [
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 8.0
                                StackPanel.children [
                                    Button.create [
                                        Button.content (Localization.current ()).Save
                                        Button.isEnabled (not model.Busy)
                                        Button.onClick (fun _ -> dispatch SaveDraft)
                                    ]
                                    Button.create [
                                        Button.content (Localization.current ()).Discard
                                        Button.background Brushes.Transparent
                                        Button.onClick (fun _ -> dispatch DiscardDraft)
                                    ]
                                ]
                            ]
                        ]
                    ])
              ] ]
        | None -> []

    let private generatePanel
        (title: string)
        (watermark: string)
        (reqValue: string)
        (onChange: string -> Msg)
        (onGenerate: Msg)
        (dispatch: Msg -> unit)
        (model: BuilderState)
        : Avalonia.FuncUI.Types.IView =
        Border.create [
            Border.padding (12.0, 10.0)
            Border.cornerRadius 12.0
            Border.background cardBg
            Border.child (
                StackPanel.create [
                    StackPanel.spacing 8.0
                    StackPanel.children [
                        TextBlock.create [
                            TextBlock.text title
                            TextBlock.fontWeight FontWeight.SemiBold
                            TextBlock.foreground Brushes.White
                        ]
                        TextBox.create [
                            TextBox.text reqValue
                            TextBox.placeHolderText watermark
                            TextBox.acceptsReturn true
                            TextBox.minHeight 70.0
                            TextBox.textWrapping TextWrapping.Wrap
                            TextBox.onTextChanged (fun t -> dispatch (onChange t))
                        ]
                        Button.create [
                            Button.content (if model.Busy then (Localization.current ()).Working else (Localization.current ()).Generate)
                            Button.isEnabled (not model.Busy)
                            Button.onClick (fun _ -> dispatch onGenerate)
                        ]
                    ]
                ])
        ]

    let private tabButton (label: string) (tab: Tab) (active: Tab) (dispatch: Msg -> unit) : Avalonia.FuncUI.Types.IView =
        Button.create [
            Button.content label
            Button.padding (12.0, 4.0)
            Button.background (
                if tab = active then Theme.surfaceRaised
                else Theme.transparent)
            Button.foreground (if tab = active then Theme.textPrimary else subtle)
            Button.onClick (fun _ -> dispatch (Activate tab))
        ]

    let private sectionLabel (text: string) : Avalonia.FuncUI.Types.IView =
        TextBlock.create [
            TextBlock.text text
            TextBlock.fontWeight FontWeight.SemiBold
            TextBlock.foreground Brushes.White
            TextBlock.margin (0.0, 6.0, 0.0, 0.0)
        ]

    /// One uploaded knowledge file with its chunk/byte stats and a Delete action.
    let private knowledgeRow (dispatch: Msg -> unit) (k: KnowledgeFileDto) : Avalonia.FuncUI.Types.IView =
        Border.create [
            Border.padding (10.0, 8.0)
            Border.cornerRadius 6.0
            Border.background cardBg
            Border.child (
                DockPanel.create [
                    DockPanel.lastChildFill true
                    DockPanel.children [
                        Button.create [
                            Button.dock Dock.Right
                            Button.content (Localization.current ()).Delete
                            Button.fontSize 11.0
                            Button.background Theme.transparent
                            Button.foreground Theme.danger
                            Button.onClick (fun _ -> dispatch (DeleteKnowledge k.Name))
                        ]
                        StackPanel.create [
                            StackPanel.spacing 2.0
                            StackPanel.children [
                                TextBlock.create [
                                    TextBlock.text k.Name
                                    TextBlock.fontWeight FontWeight.SemiBold
                                    TextBlock.foreground Brushes.White
                                ]
                                TextBlock.create [
                                    TextBlock.text (sprintf "%d chunk(s) · %d bytes" k.Chunks k.SizeBytes)
                                    TextBlock.fontSize 11.0
                                    TextBlock.foreground subtle
                                ]
                            ]
                        ]
                    ]
                ])
        ]

    /// The intro/upload panel at the top of the Knowledge tab.
    let private knowledgePanel (dispatch: Msg -> unit) : Avalonia.FuncUI.Types.IView =
        let t = Localization.current ()
        Border.create [
            Border.padding (12.0, 10.0)
            Border.cornerRadius 12.0
            Border.background cardBg
            Border.child (
                StackPanel.create [
                    StackPanel.spacing 8.0
                    StackPanel.children [
                        TextBlock.create [
                            TextBlock.text t.KnowledgeBase
                            TextBlock.fontWeight FontWeight.SemiBold
                            TextBlock.foreground Brushes.White
                        ]
                        TextBlock.create [
                            TextBlock.text t.KnowledgeBaseIntro
                            TextBlock.fontSize 12.0
                            TextBlock.textWrapping TextWrapping.Wrap
                            TextBlock.foreground subtle
                        ]
                        Button.create [
                            Button.content (sprintf "\U0001F4C1 %s" t.UploadFile)
                            Button.onClick (fun _ -> dispatch UploadKnowledgePressed)
                        ]
                    ]
                ])
        ]

    let private toolsTab (dispatch: Msg -> unit) (model: BuilderState) : Avalonia.FuncUI.Types.IView list =
        let t = Localization.current ()
        [ generatePanel
            t.GenerateToolTitle
            t.GenerateToolHint
            model.ToolReq ToolReqChanged GenerateTool dispatch model ]
        @ draftEditor dispatch model
        @ [ sectionLabel t.AvailableTools ]
        @ (model.Tools |> List.map definitionRow)

    let private agentsTab (dispatch: Msg -> unit) (model: BuilderState) : Avalonia.FuncUI.Types.IView list =
        let t = Localization.current ()
        [ generatePanel
            t.GenerateAgentTitle
            t.GenerateAgentHint
            model.AgentReq AgentReqChanged GenerateAgent dispatch model ]
        @ draftEditor dispatch model
        @ [ sectionLabel t.AvailableAgents ]
        @ (model.Agents |> List.map definitionRow)

    let private knowledgeTab (dispatch: Msg -> unit) (model: BuilderState) : Avalonia.FuncUI.Types.IView list =
        [ knowledgePanel dispatch ]
        @ (model.Knowledge |> List.map (knowledgeRow dispatch))

    let view (dispatch: Msg -> unit) (model: BuilderState) : Avalonia.FuncUI.Types.IView =
        let body : Avalonia.FuncUI.Types.IView list =
            match model.Active with
            | ToolsTab -> toolsTab dispatch model
            | AgentsTab -> agentsTab dispatch model
            | KnowledgeTab -> knowledgeTab dispatch model

        ScrollViewer.create [
            ScrollViewer.content (
                StackPanel.create [
                    StackPanel.orientation Orientation.Vertical
                    StackPanel.spacing 12.0
                    StackPanel.maxWidth 720.0
                    // Padding lives on the content (not the ScrollViewer) so it is part of
                    // the scrollable extent — otherwise the bottom item is clipped at the end.
                    StackPanel.margin (20.0, 16.0)
                    StackPanel.children [
                        TextBlock.create [
                            TextBlock.text (Localization.current ()).WorkshopTitle
                            TextBlock.fontSize 22.0
                            TextBlock.fontWeight FontWeight.Bold
                            TextBlock.foreground Theme.textPrimary
                        ]
                        StackPanel.create [
                            StackPanel.orientation Orientation.Horizontal
                            StackPanel.spacing 4.0
                            StackPanel.children [
                                tabButton (Localization.current ()).Tools ToolsTab model.Active dispatch
                                tabButton (Localization.current ()).Agents AgentsTab model.Active dispatch
                                tabButton (Localization.current ()).Knowledge KnowledgeTab model.Active dispatch
                            ]
                        ]
                        if not (String.IsNullOrWhiteSpace model.Status) then
                            TextBlock.create [
                                TextBlock.text model.Status
                                TextBlock.fontSize 12.0
                                TextBlock.foreground Theme.info
                            ]
                        yield! body
                    ]
                ]
            )
        ]
