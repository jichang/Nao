namespace Nao.DeveloperTools

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type ExtensionTemplate =
    | Agent
    | Tool
    | Provider
    | Evaluator
    | Adapter

[<RequireQualifiedAccess>]
module ExtensionTemplate =
    let private naoPackageVersion = "1.0.0-alpha1"

    let parse =
        function
        | "agent" -> ExtensionTemplate.Agent
        | "tool" -> ExtensionTemplate.Tool
        | "provider" -> ExtensionTemplate.Provider
        | "evaluator" -> ExtensionTemplate.Evaluator
        | "adapter" -> ExtensionTemplate.Adapter
        | value -> invalidArg (nameof value) $"Unknown extension template '{value}'."

    let package =
        function
        | ExtensionTemplate.Agent
        | ExtensionTemplate.Tool
        | ExtensionTemplate.Provider -> "Nao.Agents"
        | ExtensionTemplate.Evaluator -> "Nao.Eval"
        | ExtensionTemplate.Adapter -> "Nao.Extensions"

    let private source (name: string) =
        function
        | ExtensionTemplate.Agent ->
            $"""namespace {name}

open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
module AgentExtension =
    let create () =
        Agent.create "{name.ToLowerInvariant()}" "{name}" "{name} agent." 0 [] AgentContract.Text (fun _ input ->
            Task.FromResult input)
"""
        | ExtensionTemplate.Tool ->
            $"""namespace {name}

open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
module ToolExtension =
    let create runAsync =
        {{ Name = "{name.ToLowerInvariant()}"
          Description = "{name} tool."
          Priority = 0
          Schema = ToolSchema.create "{{}}" "{{}}"
          Permissions = [ ResourceAccess.tool "{name.ToLowerInvariant()}" ]
          ReplaySafety = ToolReplaySafety.RetrySafe
          RunAsync = runAsync
          RevertAsync = None }}
"""
        | ExtensionTemplate.Provider ->
            $"""namespace {name}

open Nao.Agents

[<RequireQualifiedAccess>]
module ProviderExtension =
    let create completeAsync =
        LlmProvider.create (fun () -> "{name}") completeAsync
"""
        | ExtensionTemplate.Evaluator ->
            $"""namespace {name}

open Nao.Eval

[<RequireQualifiedAccess>]
module EvaluatorExtension =
    let create evaluateAsync =
        Evaluator.deterministic "{name.ToLowerInvariant()}" "1.0.0" "{{}}" false "Report a failed deterministic check." evaluateAsync
"""
        | ExtensionTemplate.Adapter ->
            $"""namespace {name}

open Nao.Extensions

[<RequireQualifiedAccess>]
module AdapterExtension =
    let register (registry: AdapterRegistry) (registration: CompiledAdapterRegistration) =
        registry.Register registration
"""

    let private project (name: string) template contractProject =
        let package = package template

        let reference =
            match contractProject with
            | Some path -> $"    <ProjectReference Include=\"{Path.GetFullPath path}\" />"
            | None -> $"    <PackageReference Include=\"{package}\" Version=\"{naoPackageVersion}\" />"

        $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>{name}</PackageId>
    <Version>1.0.0</Version>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/{name}.fs" />
  </ItemGroup>
  <ItemGroup>
{reference}
  </ItemGroup>
</Project>
"""

    let private testProject (name: string) =
        $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/{name}Tests.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/{name}/{name}.fsproj" />
    <PackageReference Include="MSTest" Version="4.0.1" />
  </ItemGroup>
</Project>
"""

    let private tests (name: string) =
        $"""namespace {name}.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting

[<TestClass>]
type ExtensionTests() =
    [<TestMethod>]
    member _.ScaffoldLoads() =
        Assert.IsTrue(true)
"""

    let create (outputDirectory: string) template (name: string) contractProject =
        if not (Regex.IsMatch(name, "^[A-Z][A-Za-z0-9.]+$", RegexOptions.CultureInvariant)) then
            invalidArg (nameof name) "Extension names must be canonical PascalCase identifiers."

        let root = Path.GetFullPath outputDirectory

        if
            Directory.Exists root
            && Directory.EnumerateFileSystemEntries(root) |> Seq.isEmpty |> not
        then
            invalidArg (nameof outputDirectory) "Scaffold output directory must not exist or must be empty."

        let files =
            [ Path.Combine(root, "src", name, $"{name}.fsproj"), project name template contractProject
              Path.Combine(root, "src", name, "src", $"{name}.fs"), source name template
              Path.Combine(root, "tests", $"{name}.Tests", $"{name}.Tests.fsproj"), testProject name
              Path.Combine(root, "tests", $"{name}.Tests", "src", $"{name}Tests.fs"), tests name ]

        for path, content in files do
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, content)

        root, files |> List.map fst

type ExtensionWorkflowResult =
    { ExitCode: int
      StandardOutput: string
      StandardError: string }

[<RequireQualifiedAccess>]
module ExtensionWorkflow =
    let runAsync verb projectPath outputPath cancellationToken =
        task {
            let projectPath = Path.GetFullPath projectPath

            if not (File.Exists projectPath) then
                invalidArg (nameof projectPath) "Extension project does not exist."

            let arguments =
                match verb with
                | "test" -> $"test \"{projectPath}\" --configuration Release"
                | "package" ->
                    let outputPath = Path.GetFullPath outputPath
                    Directory.CreateDirectory outputPath |> ignore
                    $"pack \"{projectPath}\" --configuration Release --output \"{outputPath}\""
                | _ -> invalidArg (nameof verb) $"Unknown extension workflow '{verb}'."

            let startInfo =
                ProcessStartInfo(
                    "dotnet",
                    arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                )

            use processValue = new Process(StartInfo = startInfo)

            if not (processValue.Start()) then
                invalidOp "Could not start the dotnet extension workflow."

            let output = processValue.StandardOutput.ReadToEndAsync(cancellationToken)
            let error = processValue.StandardError.ReadToEndAsync(cancellationToken)
            do! processValue.WaitForExitAsync cancellationToken
            let! standardOutput = output
            let! standardError = error

            return
                { ExitCode = processValue.ExitCode
                  StandardOutput = standardOutput
                  StandardError = standardError }
        }
