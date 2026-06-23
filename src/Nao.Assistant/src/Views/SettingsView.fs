namespace Nao.Assistant

open Avalonia.Controls
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Layout
open Avalonia.Media

/// Settings view — global app + orchestrator configuration
module SettingsView =

    type SettingsState =
        { Settings: AppSettings
          IsDirty: bool
          StatusMessage: string }

    type Msg =
        | SettingsChanged of AppSettings
        // Granular appearance changes — each touches ONE field on the live model so the
        // theme and language combos can never overwrite each other (a full-snapshot
        // message built from a stale render closure used to revert the other setting).
        | ThemeSelected of string
        | LanguageSelected of string
        // Switching provider also pre-fills that provider's default endpoint, so picking
        // vLLM/llama.cpp/etc. "sets it up" without the user knowing each server's URL.
        | ProviderTypeSelected of string
        // Connection mode: bundled embedded engine vs a remote server cluster, plus the
        // verification-link sign-in flow remote mode requires.
        | ServerModeSelected of string
        | RequestLoginLink
        | SignOut
        | Save
        | Close

    /// Native language display names — language-independent, so this list never changes
    /// across renders. Bound as a STABLE module-level value so Avalonia never rebuilds the
    /// language ComboBox's ItemsSource (which would transiently clear the selection).
    let private languageNames : string list =
        Localization.all |> List.map Localization.displayName

    /// Header row: title on the left, Close button on the right.
    let private header (dispatch: Msg -> unit) : IView =
        let t = Localization.current ()
        DockPanel.create [
            DockPanel.children [
                Button.create [
                    Button.dock Dock.Right
                    Button.content t.Close
                    Button.onClick (fun _ -> dispatch Close)
                ]
                TextBlock.create [
                    TextBlock.text t.SettingsTitle
                    TextBlock.fontSize 22.0
                    TextBlock.fontWeight FontWeight.Bold
                    TextBlock.verticalAlignment VerticalAlignment.Center
                ]
            ]
        ]

    /// Appearance: theme (dark/light) and UI language. Applied live by the Shell when the
    /// settings change, and persisted on Save.
    let private appearanceSection (dispatch: Msg -> unit) (s: AppSettings) : IView =
        let t = Localization.current ()
        FormControls.section t.Appearance [
            FormControls.row t.Theme 120.0 [
                ComboBox.create [
                    // Bind by INDEX (0 = Dark, 1 = Light) so switching language — which
                    // changes the localized item labels — does NOT reset the selection.
                    ComboBox.dataItems [ t.ThemeDark; t.ThemeLight ]
                    ComboBox.selectedIndex (if Theme.parse s.Theme = Theme.Light then 1 else 0)
                    ComboBox.width 160.0
                    ComboBox.onSelectedIndexChanged (fun idx ->
                        let theme = if idx = 1 then "Light" else "Dark"
                        let current = if Theme.parse s.Theme = Theme.Light then "Light" else "Dark"
                        if theme <> current then dispatch (ThemeSelected theme))
                ]
            ]
            FormControls.row t.Language 120.0 [
                ComboBox.create [
                    // Native names never change with the selected language, so bind the
                    // SELECTED ITEM (by stable string value). Avalonia preserves the
                    // selection by value across renders — unlike selectedIndex, which an
                    // ItemsSource refresh can transiently reset to -1 (empty combo).
                    ComboBox.dataItems languageNames
                    ComboBox.selectedItem (box (Localization.displayName (Localization.parse s.Language)))
                    ComboBox.width 160.0
                    ComboBox.onSelectedItemChanged (fun item ->
                        match item with
                        | :? string as name ->
                            let lang =
                                Localization.all
                                |> List.tryFind (fun l -> Localization.displayName l = name)
                                |> Option.defaultValue Localization.English
                            if Localization.code lang <> Localization.code (Localization.parse s.Language) then
                                dispatch (LanguageSelected (Localization.code lang))
                        | _ -> ())
                ]
            ]
        ]

    let private providerSection (dispatch: Msg -> unit) (s: AppSettings) : IView =
        let t = Localization.current ()
        let change f = dispatch (SettingsChanged (f s))
        FormControls.section t.LlmProvider [
            // A dropdown of the providers we actually support, so the type can never be an
            // invalid free-text value. Bound by stable label string (like the language
            // combo) so re-renders never transiently clear the selection.
            FormControls.row t.FieldType 100.0 [
                ComboBox.create [
                    ComboBox.dataItems ProviderCatalog.labels
                    ComboBox.selectedItem (box (ProviderCatalog.labelFor s.Provider.ProviderType))
                    ComboBox.width 200.0
                    ComboBox.onSelectedItemChanged (fun item ->
                        match item with
                        | :? string as label ->
                            let id = ProviderCatalog.idForLabel label
                            if id <> s.Provider.ProviderType then dispatch (ProviderTypeSelected id)
                        | _ -> ())
                ]
            ]
            FormControls.textRow t.FieldEndpoint 100.0 s.Provider.Endpoint 300.0 (fun v ->
                change (fun s -> { s with Provider = { s.Provider with Endpoint = v } }))
            // A dropdown of known models for the selected provider, so the model can never
            // be an arbitrary/typo'd value. Bound by stable label string like the type combo.
            FormControls.row t.FieldModel 100.0 [
                ComboBox.create [
                    ComboBox.dataItems (ProviderCatalog.modelsFor s.Provider.ProviderType)
                    ComboBox.selectedItem (box s.Provider.Model)
                    ComboBox.width 200.0
                    ComboBox.onSelectedItemChanged (fun item ->
                        match item with
                        | :? string as v ->
                            if v <> s.Provider.Model then
                                change (fun s -> { s with Provider = { s.Provider with Model = v } })
                        | _ -> ())
                ]
            ]
        ]

    let private orchestratorSection (dispatch: Msg -> unit) (s: AppSettings) : IView =
        let t = Localization.current ()
        let change f = dispatch (SettingsChanged (f s))
        FormControls.section t.Orchestrator [
            FormControls.row t.MaxRounds 120.0 [
                NumericUpDown.create [
                    NumericUpDown.value (decimal s.Orchestrator.MaxRounds)
                    NumericUpDown.minimum 1M
                    NumericUpDown.maximum 50M
                    NumericUpDown.increment 1M
                    NumericUpDown.width 100.0
                    NumericUpDown.onValueChanged (fun v ->
                        if v.HasValue then
                            change (fun s -> { s with Orchestrator = { s.Orchestrator with MaxRounds = int v.Value } }))
                ]
            ]
            FormControls.row t.Temperature 120.0 [
                NumericUpDown.create [
                    NumericUpDown.value (decimal s.Orchestrator.Temperature)
                    NumericUpDown.minimum 0M
                    NumericUpDown.maximum 2M
                    NumericUpDown.increment 0.1M
                    NumericUpDown.width 100.0
                    NumericUpDown.onValueChanged (fun v ->
                        if v.HasValue then
                            change (fun s -> { s with Orchestrator = { s.Orchestrator with Temperature = float v.Value } }))
                ]
            ]
            FormControls.row t.ContextWindow 120.0 [
                ComboBox.create [
                    ComboBox.dataItems [ "LastN"; "TokenBudget"; "SummarizeAfter" ]
                    ComboBox.selectedItem (box s.Orchestrator.WindowStrategy)
                    ComboBox.width 160.0
                    ComboBox.onSelectedItemChanged (fun item ->
                        match item with
                        | :? string as v ->
                            change (fun s -> { s with Orchestrator = { s.Orchestrator with WindowStrategy = v } })
                        | _ -> ())
                ]
                NumericUpDown.create [
                    NumericUpDown.value (decimal s.Orchestrator.WindowSize)
                    NumericUpDown.minimum 1M
                    NumericUpDown.maximum 100M
                    NumericUpDown.increment 5M
                    NumericUpDown.width 100.0
                    NumericUpDown.onValueChanged (fun v ->
                        if v.HasValue then
                            change (fun s -> { s with Orchestrator = { s.Orchestrator with WindowSize = int v.Value } }))
                ]
            ]
            TextBlock.create [
                TextBlock.text t.SystemPrompt
                TextBlock.margin (0.0, 4.0, 0.0, 0.0)
            ]
            TextBox.create [
                TextBox.text s.Orchestrator.SystemPrompt
                TextBox.acceptsReturn true
                TextBox.minHeight 80.0
                TextBox.textWrapping TextWrapping.Wrap
                TextBox.onTextChanged (fun v ->
                    change (fun s -> { s with Orchestrator = { s.Orchestrator with SystemPrompt = v } }))
            ]
        ]

    /// Connection: bundled local engine vs a remote server cluster. Remote mode unlocks
    /// cluster-backed performance and hosted features, and requires signing in via a
    /// verification link sent to the user's email.
    let private serverSection (dispatch: Msg -> unit) (s: AppSettings) : IView =
        let t = Localization.current ()
        let change f = dispatch (SettingsChanged (f s))
        let isRemote = s.Server.Mode = "Remote"
        let signedIn = not (System.String.IsNullOrWhiteSpace s.Server.AuthToken)
        let statusText =
            if signedIn then sprintf "%s %s" t.ServerSignedInAs s.Server.AuthEmail
            else t.ServerNotSignedIn
        let rows =
            [ yield FormControls.row t.ServerMode 120.0 [
                ComboBox.create [
                    // Bind by INDEX (0 = Local, 1 = Remote) so a language switch — which
                    // re-localizes the item labels — does NOT reset the selection.
                    ComboBox.dataItems [ t.ServerLocal; t.ServerRemote ]
                    ComboBox.selectedIndex (if isRemote then 1 else 0)
                    ComboBox.width 200.0
                    ComboBox.onSelectedIndexChanged (fun idx ->
                        let mode = if idx = 1 then "Remote" else "Local"
                        if mode <> s.Server.Mode then dispatch (ServerModeSelected mode))
                ]
              ]
              if isRemote then
                  yield FormControls.textRow t.ServerRemoteUrl 120.0 s.Server.RemoteUrl 300.0 (fun v ->
                      change (fun s -> { s with Server = { s.Server with RemoteUrl = v } }))
                  yield FormControls.textRow t.ServerEmail 120.0 s.Server.AuthEmail 300.0 (fun v ->
                      change (fun s -> { s with Server = { s.Server with AuthEmail = v } }))
                  yield FormControls.row "" 120.0 [
                      (if signedIn then
                          Button.create [
                              Button.content t.ServerSignOut
                              Button.onClick (fun _ -> dispatch SignOut)
                          ] :> IView
                       else
                          Button.create [
                              Button.content t.ServerSendLink
                              // Can't send a verification link without an address.
                              Button.isEnabled (not (System.String.IsNullOrWhiteSpace s.Server.AuthEmail))
                              Button.onClick (fun _ -> dispatch RequestLoginLink)
                          ] :> IView)
                      TextBlock.create [
                          TextBlock.text statusText
                          TextBlock.verticalAlignment VerticalAlignment.Center
                          TextBlock.foreground (if signedIn then Theme.success else Theme.textMuted)
                      ] :> IView
                  ]
              yield FormControls.hint t.ServerHint ]
        FormControls.section t.Server rows

    let private workspaceSection (dispatch: Msg -> unit) (s: AppSettings) : IView =
        let t = Localization.current ()
        FormControls.section t.Workspace [
            FormControls.row t.PathLabel 100.0 [
                TextBox.create [
                    TextBox.text s.WorkspacePath
                    TextBox.width 350.0
                    TextBox.watermark t.WorkspaceWatermark
                    TextBox.onTextChanged (fun v ->
                        dispatch (SettingsChanged { s with WorkspacePath = v }))
                ]
            ]
            FormControls.hint t.WorkspaceHint
        ]

    /// Footer row: status message on the left, Save button on the right.
    let private footer (dispatch: Msg -> unit) (model: SettingsState) : IView =
        DockPanel.create [
            DockPanel.children [
                Button.create [
                    Button.dock Dock.Right
                    Button.content (Localization.current ()).Save
                    Button.isEnabled model.IsDirty
                    Button.onClick (fun _ -> dispatch Save)
                ]
                TextBlock.create [
                    TextBlock.text model.StatusMessage
                    TextBlock.foreground Theme.success
                    TextBlock.verticalAlignment VerticalAlignment.Center
                ]
            ]
        ]

    let view (dispatch: Msg -> unit) (model: SettingsState) : IView =
        let s = model.Settings
        ScrollViewer.create [
            ScrollViewer.content (
                StackPanel.create [
                    StackPanel.orientation Orientation.Vertical
                    StackPanel.spacing 16.0
                    StackPanel.maxWidth 600.0
                    // Padding lives on the content (not the ScrollViewer) so it is part of
                    // the scrollable extent — otherwise the bottom item is clipped at the end.
                    StackPanel.margin (20.0, 16.0)
                    StackPanel.children [
                        header dispatch
                        appearanceSection dispatch s
                        providerSection dispatch s
                        serverSection dispatch s
                        orchestratorSection dispatch s
                        workspaceSection dispatch s
                        footer dispatch model
                    ]
                ]
            )
        ]
