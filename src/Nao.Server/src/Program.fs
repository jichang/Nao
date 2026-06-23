namespace Nao.Server

open System
open System.Threading
open Nao.Assistant

/// Standalone HTTP server entry point. Boots the same embedded Orleans-backed
/// HTTP host that the desktop app uses, but as a headless process so the agent
/// system can be hosted on its own (containers, servers, CI).
module Program =

    [<EntryPoint>]
    let main _argv =
        let settings = AppSettingsStore.load ()

        let baseUrl = EmbeddedServer.start settings
        printfn "Nao.Server listening on %s" baseUrl
        printfn "Press Ctrl+C to stop."

        use stopSignal = new ManualResetEventSlim(false)
        Console.CancelKeyPress.Add(fun args ->
            args.Cancel <- true
            stopSignal.Set())
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> stopSignal.Set())

        stopSignal.Wait()
        EmbeddedServer.stop ()
        0
