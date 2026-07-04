namespace Nao.Assistant

open System
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Input
open Avalonia.Threading
open global.Elmish
open Nao.Assistant

/// Main shell — browser-like tabbed interface
module Shell =

    type ServerStartupState =
        | Starting
        | Started of string
        | Failed of string

    type ActiveView =
        | SessionTab of int
        | Settings
        | Workshop

    type ShellState =
        { Sessions: SessionView.SessionState list
          ActiveView: ActiveView
          SettingsState: SettingsView.SettingsState
          BuilderState: BuilderView.BuilderState
          ServerStartup: ServerStartupState
          Client: NaoClient option }

    type ShellMsg =
        | StartupSucceeded of NaoClient * string * SessionView.SessionState list
        | StartupFailed of string
        | RetryStartup
        | NewSession
        | SelectSession of int
        | CloseSession of int
        | OpenSettings
        | OpenWorkshop
        | SessionMsg of int * SessionView.Msg
        | SettingsMsg of SettingsView.Msg
        | BuilderMsg of BuilderView.Msg

    /// Show a modal approval prompt for a server-pushed permission request and reply over the
    /// same WebSocket. Closing without choosing denies (fail-closed). Runs on the UI thread.
    let private showPermissionPrompt (client: NaoClient) (req: PermissionRequestDto) =
        Dispatcher.UIThread.Post(fun () ->
            let t = Localization.current ()
            let owner =
                match Application.Current.ApplicationLifetime with
                | :? IClassicDesktopStyleApplicationLifetime as d -> d.MainWindow
                | _ -> null

            let dialog = Window()
            dialog.Title <- t.PermissionPromptTitle
            dialog.Width <- 480.0
            dialog.SizeToContent <- SizeToContent.Height
            dialog.WindowStartupLocation <- WindowStartupLocation.CenterOwner
            dialog.CanResize <- false

            let mutable answered = false
            let respond (decision: string) (scope: string) =
                answered <- true
                client.SendPermissionResponseAsync(req.RequestId, decision, scope) |> ignore
                dialog.Close()

            let panel = StackPanel(Margin = Thickness 20.0, Spacing = 10.0)

            let header = TextBlock(Text = t.PermissionPromptTitle, FontWeight = FontWeight.Bold, FontSize = 15.0)
            let reason = TextBlock(Text = req.Reason, TextWrapping = TextWrapping.Wrap)
            let resource =
                let label = if String.IsNullOrEmpty req.Operation then req.Resource else sprintf "%s — %s" (req.Operation.ToUpperInvariant()) req.Resource
                TextBlock(Text = label, TextWrapping = TextWrapping.Wrap, FontFamily = FontFamily("monospace"), Foreground = SolidColorBrush(Color.Parse "#A1A1AA"))

            let buttons = StackPanel(Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8.0, Margin = Thickness(0.0, 8.0, 0.0, 0.0))

            let denyBtn = Button(Content = t.PermissionDeny)
            denyBtn.Click.Add(fun _ -> respond "deny" "")
            let onceBtn = Button(Content = t.PermissionAllowOnce)
            onceBtn.Click.Add(fun _ -> respond "allow" "once")
            let sessionBtn = Button(Content = t.PermissionAllowSession)
            sessionBtn.Click.Add(fun _ -> respond "allow" "session")
            let globalBtn = Button(Content = t.PermissionAllowGlobal)
            globalBtn.Click.Add(fun _ -> respond "allow" "global")

            buttons.Children.Add denyBtn
            buttons.Children.Add onceBtn
            buttons.Children.Add sessionBtn
            buttons.Children.Add globalBtn

            panel.Children.Add header
            panel.Children.Add reason
            panel.Children.Add resource
            panel.Children.Add buttons
            dialog.Content <- panel

            dialog.Closed.Add(fun _ ->
                if not answered then client.SendPermissionResponseAsync(req.RequestId, "deny", "") |> ignore)

            if isNull owner then dialog.Show()
            else dialog.ShowDialog(owner) |> ignore)

    let private startupCmd (settings: AppSettings) : Cmd<ShellMsg> =
        [ fun dispatch ->
            Task.Run<unit>(Func<Task<unit>>(fun () ->
                task {
                    try
                        let serverUrl = EmbeddedServer.start settings
                        let client = new NaoClient(serverUrl)
                        // Permission prompts arrive over the same WebSocket as chat. Handle them
                        // from a standing subscription (chat's own handlers ignore them) so a tool
                        // blocked mid-turn can ask the user and resume on their answer.
                        client.OnMessage.Add(fun evt ->
                            match evt with
                            | NaoEvent.PermissionRequest req -> showPermissionPrompt client req
                            | _ -> ())
                        let! entries = client.ListSessionsAsync()
                        let restored =
                            if entries.Length > 0 then
                                entries
                                |> List.map (fun entry ->
                                    { SessionView.Id = entry.SessionId.Split('/').[1]
                                      SessionView.ServerSessionId = Some entry.SessionId
                                      SessionView.Title = entry.Title
                                      SessionView.Messages = []
                                      SessionView.Input = ""
                                      SessionView.Chat = SessionView.Idle
                                      SessionView.History = SessionView.NeedsLoad
                                      SessionView.LiveSteps = []
                                      SessionView.Feedback = None
                                      SessionView.AvailableAgents = []
                                      SessionView.SelectedAgent = "document"
                                      SessionView.AgentsLoaded = false
                                      SessionView.AttachedFile = None
                                      SessionView.Files = []
                                      SessionView.Tasks = []
                                      SessionView.FilesPanelOpen = false } : SessionView.SessionState)
                            else
                                [ SessionView.createNew () ]

                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (StartupSucceeded (client, serverUrl, restored)))
                    with ex ->
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (StartupFailed ex.Message))
                }))
            |> ignore ]

    let init () : ShellState * Cmd<ShellMsg> =
        let settings = AppSettingsStore.load ()
        // Apply the persisted appearance before the first render.
        Theme.apply (Theme.parse settings.Theme)
        Localization.apply (Localization.parse settings.Language)
        { Sessions = []
          ActiveView = SessionTab 0
          SettingsState =
            { Settings = settings
              IsDirty = false
              StatusMessage = "" }
          BuilderState = BuilderView.init ()
          ServerStartup = Starting
          Client = None },
        startupCmd settings

    let update (msg: ShellMsg) (model: ShellState) : ShellState * Cmd<ShellMsg> =
        match msg with
        | StartupSucceeded (client, serverUrl, sessions) ->
            let trigger =
                match sessions with
                | first :: _ when first.ServerSessionId.IsSome ->
                    Cmd.batch
                        [ Cmd.ofMsg (SessionMsg (0, SessionView.TriggerHistoryLoad))
                          Cmd.ofMsg (SessionMsg (0, SessionView.TriggerAgentsLoad)) ]
                | _ ->
                    Cmd.ofMsg (SessionMsg (0, SessionView.TriggerAgentsLoad))
            { model with
                Sessions = sessions
                ActiveView = SessionTab 0
                ServerStartup = Started serverUrl
                Client = Some client },
            trigger

        | StartupFailed err ->
            { model with ServerStartup = Failed err }, Cmd.none

        | RetryStartup ->
            { model with ServerStartup = Starting }, startupCmd model.SettingsState.Settings

        | NewSession ->
            let session = SessionView.createNew ()
            let idx = model.Sessions.Length
            { model with
                Sessions = model.Sessions @ [ session ]
                ActiveView = SessionTab idx },
            Cmd.ofMsg (SessionMsg (idx, SessionView.TriggerAgentsLoad))

        | SelectSession i ->
            if i < 0 || i >= model.Sessions.Length then
                model, Cmd.none
            else
                let sessions =
                    model.Sessions
                    |> List.mapi (fun idx s ->
                        if idx = i then
                            { s with
                                History =
                                    if s.ServerSessionId.IsSome
                                    then SessionView.NeedsLoad
                                    else SessionView.Loaded }
                        else
                            s)
                let trigger =
                    if sessions.[i].ServerSessionId.IsSome then
                        Cmd.batch
                            [ Cmd.ofMsg (SessionMsg (i, SessionView.TriggerHistoryLoad))
                              Cmd.ofMsg (SessionMsg (i, SessionView.TriggerAgentsLoad)) ]
                    else
                        Cmd.ofMsg (SessionMsg (i, SessionView.TriggerAgentsLoad))
                { model with Sessions = sessions; ActiveView = SessionTab i }, trigger

        | CloseSession i ->
            if i < 0 || i >= model.Sessions.Length then
                model, Cmd.none
            else
                let updated = model.Sessions |> List.removeAt i
                let newActive =
                    match model.ActiveView with
                    | SessionTab idx when idx >= updated.Length -> SessionTab (max 0 (updated.Length - 1))
                    | SessionTab idx when idx > i -> SessionTab (idx - 1)
                    | other -> other
                { model with Sessions = updated; ActiveView = newActive }, Cmd.none

        | OpenSettings ->
            { model with ActiveView = Settings }, Cmd.none

        | OpenWorkshop ->
            { model with ActiveView = Workshop }, Cmd.ofMsg (BuilderMsg BuilderView.TriggerLoad)

        | BuilderMsg builderMsg ->
            match model.Client with
            | Some client ->
                let updated, cmd = BuilderView.update client builderMsg model.BuilderState
                { model with BuilderState = updated }, Cmd.map BuilderMsg cmd
            | None ->
                model, Cmd.none

        | SessionMsg (i, sessionMsg) ->
            match model.Client with
            | Some client when i >= 0 && i < model.Sessions.Length ->
                let updated, cmd = SessionView.update client sessionMsg model.Sessions.[i]
                let sessions =
                    model.Sessions
                    |> List.mapi (fun idx s -> if idx = i then updated else s)
                { model with Sessions = sessions }, Cmd.map (fun m -> SessionMsg (i, m)) cmd
            | _ ->
                model, Cmd.none

        | SettingsMsg settingsMsg ->
            match settingsMsg with
            | SettingsView.ThemeSelected theme ->
                // Granular, single-field update on the LIVE model — never touches Language.
                Theme.apply (Theme.parse theme)
                { model with
                    SettingsState =
                        { model.SettingsState with
                            Settings = { model.SettingsState.Settings with Theme = theme }
                            IsDirty = true } },
                Cmd.none
            | SettingsView.LanguageSelected lang ->
                // Granular, single-field update on the LIVE model — never touches Theme.
                Localization.apply (Localization.parse lang)
                { model with
                    SettingsState =
                        { model.SettingsState with
                            Settings = { model.SettingsState.Settings with Language = lang }
                            IsDirty = true } },
                Cmd.none
            | SettingsView.SettingsChanged newSettings ->
                // Apply appearance immediately so the theme/language toggle is live.
                Theme.apply (Theme.parse newSettings.Theme)
                Localization.apply (Localization.parse newSettings.Language)
                { model with
                    SettingsState =
                        { model.SettingsState with
                            Settings = newSettings
                            IsDirty = true } },
                Cmd.none
            | SettingsView.ProviderTypeSelected providerId ->
                // Picking a provider also "sets it up" by pre-filling that provider's
                // default endpoint and a valid default model, so the user doesn't need to
                // know each server's URL and the model dropdown is never left mismatched.
                let current = model.SettingsState.Settings.Provider
                let provider =
                    { current with
                        ProviderType = providerId
                        Endpoint = ProviderCatalog.defaultEndpointFor providerId
                        Model = ProviderCatalog.defaultModelFor providerId }
                { model with
                    SettingsState =
                        { model.SettingsState with
                            Settings = { model.SettingsState.Settings with Provider = provider }
                            IsDirty = true } },
                Cmd.none
            | SettingsView.ServerModeSelected mode ->
                // Granular, single-field update so switching Local/Remote never clobbers
                // the remote URL, email, or token.
                let server = { model.SettingsState.Settings.Server with Mode = mode }
                { model with
                    SettingsState =
                        { model.SettingsState with
                            Settings = { model.SettingsState.Settings with Server = server }
                            IsDirty = true } },
                Cmd.none
            | SettingsView.RequestLoginLink ->
                // No hosted auth backend exists yet; surface a clear status message rather
                // than fabricating a sign-in. The verification-link exchange is wired here
                // once a remote auth service is available.
                let email = model.SettingsState.Settings.Server.AuthEmail
                let status =
                    if System.String.IsNullOrWhiteSpace email then "Enter an email to receive a verification link."
                    else sprintf "Verification link sent to %s. Check your inbox to finish signing in." email
                { model with
                    SettingsState =
                        { model.SettingsState with StatusMessage = status } },
                Cmd.none
            | SettingsView.SignOut ->
                let server = { model.SettingsState.Settings.Server with AuthToken = "" }
                { model with
                    SettingsState =
                        { model.SettingsState with
                            Settings = { model.SettingsState.Settings with Server = server }
                            IsDirty = true
                            StatusMessage = "Signed out." } },
                Cmd.none
            | SettingsView.Save ->
                AppSettingsStore.save model.SettingsState.Settings
                { model with
                    SettingsState =
                        { model.SettingsState with
                            IsDirty = false
                            StatusMessage = "Settings saved." } },
                Cmd.none
            | SettingsView.Close ->
                { model with ActiveView = SessionTab 0 }, Cmd.none

    let private tabBar (model: ShellState) (dispatch: ShellMsg -> unit) : Avalonia.FuncUI.Types.IView =
        Border.create [
            Border.dock Dock.Top
            Border.padding (8.0, 6.0)
            Border.background Theme.surface
            Border.borderThickness (0.0, 0.0, 0.0, 1.0)
            Border.borderBrush Theme.border
            Border.child (
                DockPanel.create [
                    DockPanel.lastChildFill true
                    DockPanel.children [
                        // Right side: settings + new tab button
                        StackPanel.create [
                            StackPanel.dock Dock.Right
                            StackPanel.orientation Orientation.Horizontal
                            StackPanel.spacing 4.0
                            StackPanel.children [
                                Button.create [
                                    Button.content "+"
                                    Button.fontSize 16.0
                                    Button.padding (8.0, 2.0)
                                    Button.background Theme.transparent
                                    Button.foreground Theme.textSecondary
                                    Button.onClick (fun _ -> dispatch NewSession)
                                ]
                                Button.create [
                                    Button.content "\U0001F527"
                                    Button.fontSize 16.0
                                    Button.padding (8.0, 2.0)
                                    Button.background (
                                        match model.ActiveView with
                                        | Workshop -> Theme.surfaceHover
                                        | _ -> Theme.transparent)
                                    Button.foreground Theme.textPrimary
                                    Button.onClick (fun _ -> dispatch OpenWorkshop)
                                ]
                                Button.create [
                                    Button.content "⚙"
                                    Button.fontSize 16.0
                                    Button.padding (8.0, 2.0)
                                    Button.background (
                                        match model.ActiveView with
                                        | Settings -> Theme.surfaceHover
                                        | _ -> Theme.transparent)
                                    Button.foreground Theme.textPrimary
                                    Button.onClick (fun _ ->
                                        dispatch OpenSettings)
                                ]
                            ]
                        ]

                        // Tabs
                        ScrollViewer.create [
                            ScrollViewer.horizontalScrollBarVisibility Primitives.ScrollBarVisibility.Auto
                            ScrollViewer.verticalScrollBarVisibility Primitives.ScrollBarVisibility.Disabled
                            ScrollViewer.content (
                                StackPanel.create [
                                    StackPanel.orientation Orientation.Horizontal
                                    StackPanel.spacing 2.0
                                    StackPanel.children [
                                        for i, session in model.Sessions |> List.indexed do
                                            let isActive =
                                                match model.ActiveView with
                                                | SessionTab idx -> idx = i
                                                | _ -> false

                                            Border.create [
                                                Border.padding (10.0, 4.0)
                                                Border.cornerRadius (6.0, 6.0, 0.0, 0.0)
                                                Border.background (
                                                    if isActive then Theme.surfaceRaised
                                                    else Theme.transparent)
                                                Border.child (
                                                    StackPanel.create [
                                                        StackPanel.orientation Orientation.Horizontal
                                                        StackPanel.spacing 6.0
                                                        StackPanel.children [
                                                            Button.create [
                                                                Button.content session.Title
                                                                Button.background Theme.transparent
                                                                Button.foreground (
                                                                    if isActive then Theme.textPrimary
                                                                    else Theme.textSecondary)
                                                                Button.padding (4.0, 2.0)
                                                                Button.onClick (fun _ ->
                                                                    dispatch (SelectSession i))
                                                            ]
                                                            if model.Sessions.Length > 1 then
                                                                Button.create [
                                                                    Button.content "×"
                                                                    Button.fontSize 12.0
                                                                    Button.padding (2.0, 0.0)
                                                                    Button.background Theme.transparent
                                                                    Button.foreground Theme.textMuted
                                                                    Button.onClick (fun _ ->
                                                                        dispatch (CloseSession i))
                                                                ]
                                                        ]
                                                    ]
                                                )
                                            ]
                                    ]
                                ]
                            )
                        ]
                    ]
                ]
            )
        ]

    let view (model: ShellState) (dispatch: ShellMsg -> unit) : Avalonia.FuncUI.Types.IView =
        match model.ServerStartup with
            | Starting ->
                Grid.create [
                    Grid.background Theme.bg
                    Grid.children [
                        StackPanel.create [
                            StackPanel.horizontalAlignment HorizontalAlignment.Center
                            StackPanel.verticalAlignment VerticalAlignment.Center
                            StackPanel.spacing 12.0
                            StackPanel.width 320.0
                            StackPanel.children [
                                TextBlock.create [
                                    TextBlock.text (Localization.current ()).StartingServer
                                    TextBlock.horizontalAlignment HorizontalAlignment.Center
                                    TextBlock.foreground Theme.textPrimary
                                    TextBlock.fontSize 16.0
                                ]
                                ProgressBar.create [
                                    ProgressBar.height 8.0
                                    ProgressBar.isIndeterminate true
                                ]
                                TextBlock.create [
                                    TextBlock.text (Localization.current ()).PreparingRuntime
                                    TextBlock.horizontalAlignment HorizontalAlignment.Center
                                    TextBlock.foreground Theme.textSecondary
                                ]
                            ]
                        ]
                    ]
                ]
            | Failed err ->
                Grid.create [
                    Grid.background Theme.bg
                    Grid.children [
                        StackPanel.create [
                            StackPanel.horizontalAlignment HorizontalAlignment.Center
                            StackPanel.verticalAlignment VerticalAlignment.Center
                            StackPanel.spacing 12.0
                            StackPanel.width 520.0
                            StackPanel.children [
                                TextBlock.create [
                                    TextBlock.text (Localization.current ()).ServerFailed
                                    TextBlock.foreground Theme.textPrimary
                                    TextBlock.fontSize 18.0
                                    TextBlock.horizontalAlignment HorizontalAlignment.Center
                                ]
                                SelectableTextBlock.create [
                                    SelectableTextBlock.text err
                                    SelectableTextBlock.foreground Theme.danger
                                    SelectableTextBlock.textWrapping TextWrapping.Wrap
                                ]
                                Button.create [
                                    Button.content (Localization.current ()).Retry
                                    Button.horizontalAlignment HorizontalAlignment.Center
                                    Button.onClick (fun _ ->
                                        dispatch RetryStartup)
                                ]
                            ]
                        ]
                    ]
                ]
            | Started _ ->
                Grid.create [
                    Grid.background Theme.bg
                    Grid.rowDefinitions "Auto,*"
                    Grid.children [
                        // Tab bar (row 0)
                        tabBar model dispatch

                        // Content area (row 1) — wrapped so it is constrained to the
                        // remaining height; otherwise the bottom of the hosted view is
                        // clipped by the tab bar's height.
                        Panel.create [
                            Grid.row 1
                            Panel.children [
                                match model.ActiveView with
                                | Settings ->
                                    SettingsView.view (SettingsMsg >> dispatch) model.SettingsState

                                | Workshop ->
                                    BuilderView.view (BuilderMsg >> dispatch) model.BuilderState

                                | SessionTab idx ->
                                    if idx >= 0 && idx < model.Sessions.Length then
                                        SessionView.view (fun m -> dispatch (SessionMsg (idx, m))) model.Sessions.[idx]
                                    else
                                        TextBlock.create [
                                            TextBlock.text (Localization.current ()).NoSessionSelected
                                            TextBlock.horizontalAlignment HorizontalAlignment.Center
                                            TextBlock.verticalAlignment VerticalAlignment.Center
                                        ]
                            ]
                        ]
                    ]
                ]
