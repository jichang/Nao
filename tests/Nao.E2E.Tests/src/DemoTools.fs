namespace Nao.E2E.Tests

open System.Threading.Tasks
open Nao.Agents

/// Demo tools for E2E testing
module DemoTools =

    let private createText name description execute =
        Tool.create name description 0 [] ToolCodec.text ToolCodec.text (ToolOperation.create execute)

    /// A weather lookup tool that returns fake weather data
    let getWeather =
        createText "get_weather" "Get the current weather for a location" (fun _ input ->
            Task.FromResult(Ok(sprintf "The weather in %s is 18°C and sunny." input)))

    /// A calculator tool that evaluates simple math expressions
    let calculator =
        createText "calculator" "Evaluate a math expression" (fun _ input ->
            let result =
                match input.Trim() with
                | "2 + 2" -> "4"
                | "3 * 7" -> "21"
                | "10 / 2" -> "5"
                | "100 - 37" -> "63"
                | expression -> sprintf "Cannot evaluate: %s" expression
            Task.FromResult(Ok result))

    /// A greeting tool
    let greeter =
        createText "greeter" "Generate a greeting for a person" (fun _ input ->
            Task.FromResult(Ok(sprintf "Hello, %s! Welcome aboard." input)))
