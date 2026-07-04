namespace Nao.Assistant

open System
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Hosts
open Avalonia.FuncUI.Elmish
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Themes.Fluent
open Avalonia.Threading

type MainWindow() as this =
    inherit HostWindow()
    do
        this.Title <- "Nao Desktop"
        this.Width <- 1000.0
        this.Height <- 700.0
        this.MinWidth <- 600.0
        this.MinHeight <- 400.0

        // Expose this window so deeper views can open native dialogs (file pickers).
        UiContext.topLevel <- Some (this :> Avalonia.Controls.TopLevel)

        Elmish.Program.mkProgram Shell.init Shell.update Shell.view
        |> Program.withHost this
        |> Elmish.Program.run

type App() =
    inherit Application()

    override this.Initialize() =
        // Semi.Avalonia is the app-wide design system: it restyles every default
        // control (buttons, combo boxes, text boxes, scrollbars, progress bars) with a
        // modern, cohesive look.
        this.Styles.Add(Semi.Avalonia.SemiTheme())
        // Apply the persisted appearance (theme + language) before the first window renders.
        let settings = AppSettingsStore.load ()
        Theme.apply (Theme.parse settings.Theme)
        Localization.apply (Localization.parse settings.Language)

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            desktop.MainWindow <- MainWindow()
            desktop.ShutdownRequested.Add(fun _ ->
                EmbeddedServer.stop ())
        | _ -> ()
        base.OnFrameworkInitializationCompleted()

module Program =

    [<EntryPoint>]
    let main argv =
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(argv)
