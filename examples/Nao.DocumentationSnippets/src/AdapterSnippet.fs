namespace Nao.DocumentationSnippets

open System
open System.Threading
open System.Threading.Tasks
open Nao.Extensions

[<RequireQualifiedAccess>]
module AdapterSnippet =
    let private compatibility =
        AdapterCompatibility.create (Version(1, 0, 0)) 1 (Version(1, 0, 0)) (Version(1, 0, 0)) "net10.0"

    let registration =
        { Descriptor =
            AdapterDescriptor.create
                "documentation-adapter"
                "Documentation adapter"
                "Compiled adapter registration used by executable documentation."
                (Version(1, 0, 0))
                AdapterKind.Tool
                compatibility
                [ "documentation" ]
                (TimeSpan.FromSeconds 1.0)
          ValidateConfiguration = fun _ -> Ok()
          StartAsync =
            fun _ _ _ ->
                Task.FromResult
                    { HealthAsync = fun _ -> Task.FromResult(Ok AdapterHealth.Ready)
                      StopAsync = fun _ -> Task.FromResult(Ok()) } }

    let verifyAsync () =
        task {
            let registry = AdapterRegistry AdapterHostCompatibility.current

            registry.Register registration
            |> Result.defaultWith (AdapterFailure.message >> invalidOp)

            let context =
                AdapterActivationContext.create "documentation-instance" AdapterHostCompatibility.current []

            let! activated =
                registry.ActivateAsync(
                    AdapterKind.Tool,
                    "documentation-adapter",
                    context,
                    AdapterConfiguration.empty,
                    CancellationToken.None
                )

            let runtime = activated |> Result.defaultWith (AdapterFailure.message >> invalidOp)

            let! health = runtime.HealthAsync CancellationToken.None

            match health with
            | Ok AdapterHealth.Ready -> ()
            | Ok _
            | Error _ -> invalidOp "The documented adapter was not ready."

            let! stopped = runtime.StopAsync CancellationToken.None
            stopped |> Result.defaultWith (AdapterFailure.message >> invalidOp)
        }
