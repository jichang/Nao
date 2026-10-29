namespace Nao.DeveloperTools.Cli

open System
open System.Threading
open Nao.DeveloperTools

module Program =
    [<EntryPoint>]
    let main arguments =
        try
            let result =
                DeveloperCommandLine.runAsync DeveloperCommandServices.localOnly arguments CancellationToken.None
                |> _.GetAwaiter().GetResult()

            Console.Out.WriteLine(DeveloperCommandResult.serialize result)
            if result.Succeeded then 0 else 1
        with error ->
            let result = DeveloperCommandResult.failure "host" "unhandled_failure" error.Message
            Console.Out.WriteLine(DeveloperCommandResult.serialize result)
            1
