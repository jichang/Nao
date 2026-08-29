namespace Nao.E2E.Tests

open System.Threading.Tasks
open Nao.Agents

/// Demo tools for E2E testing
module DemoTools =

    type private WeatherTool() =
        inherit TypedTool<string, string>("get_weather", "Get the current weather for a location", [], ToolParameter.text, ToolParameter.text)
        override _.ExecuteAsync(_context, input) =
            Task.FromResult(Ok(sprintf "The weather in %s is 18°C and sunny." input))

    /// A weather lookup tool that returns fake weather data
    let getWeather: ITool = WeatherTool()

    type private CalculatorTool() =
        inherit TypedTool<string, string>("calculator", "Evaluate a math expression", [], ToolParameter.text, ToolParameter.text)
        override _.ExecuteAsync(_context, input) =
            let result =
                match input.Trim() with
                | "2 + 2" -> "4"
                | "3 * 7" -> "21"
                | "10 / 2" -> "5"
                | "100 - 37" -> "63"
                | expression -> sprintf "Cannot evaluate: %s" expression
            Task.FromResult(Ok result)

    /// A calculator tool that evaluates simple math expressions
    let calculator: ITool = CalculatorTool()

    type private GreeterTool() =
        inherit TypedTool<string, string>("greeter", "Generate a greeting for a person", [], ToolParameter.text, ToolParameter.text)
        override _.ExecuteAsync(_context, input) =
            Task.FromResult(Ok(sprintf "Hello, %s! Welcome aboard." input))

    /// A greeting tool
    let greeter: ITool = GreeterTool()
