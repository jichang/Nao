namespace Nao.Agents

[<RequireQualifiedAccess>]
module internal RuntimeExecutionBudget =
    let private current = System.Threading.AsyncLocal<ExecutionContext option>()

    let get () = current.Value
    let set context = current.Value <- context

    let cancellationToken () =
        current.Value
        |> Option.map _.CancellationToken
        |> Option.defaultValue System.Threading.CancellationToken.None

    let beginLlmCall () =
        current.Value |> Option.bind _.BeginLlmCall()

    let recordLlmUsage usage =
        let tokens =
            usage
            |> Option.map (fun value -> value.InputTokens + value.OutputTokens)
            |> Option.defaultValue 0

        current.Value |> Option.bind (fun context -> context.RecordLlmUsage(tokens, 0m))
