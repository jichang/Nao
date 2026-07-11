namespace Nao.E2E.Tests

open System.Threading.Tasks
open Nao.Agents

/// Demo tools for E2E testing
module DemoTools =

    /// A weather lookup tool that returns fake weather data
    let getWeather: Tool =
        Tool.Create("get_weather", "Get the current weather for a location",
            fun args -> Task.FromResult(sprintf "The weather in %s is 18°C and sunny." args))

    /// A calculator tool that evaluates simple math expressions
    let calculator: Tool =
        Tool.Create("calculator", "Evaluate a math expression",
            fun args ->
                let result =
                    match args.Trim() with
                    | "2 + 2" -> "4"
                    | "3 * 7" -> "21"
                    | "10 / 2" -> "5"
                    | "100 - 37" -> "63"
                    | expr -> sprintf "Cannot evaluate: %s" expr
                Task.FromResult(result))

    /// A greeting tool
    let greeter: Tool =
        Tool.Create("greeter", "Generate a greeting for a person",
            fun args -> Task.FromResult(sprintf "Hello, %s! Welcome aboard." args))
