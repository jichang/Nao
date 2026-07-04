namespace Nao.Loader

open System
open System.IO
open System.Reflection
open Nao.Agents
open Nao.Agents
open Nao.Eval

/// Loads agents, tools, and evaluators from a .NET assembly via reflection.
/// Discovers:
///   - Types implementing IAgent (instantiated via parameterless constructor)
///   - Static properties/fields of type Tool
///   - Types implementing IEvaluator (instantiated via parameterless constructor)
type AssemblySource(assemblyPath: string) =

    let tryCreateInstance (targetType: Type) (t: Type) : obj option =
        try
            let instance = Activator.CreateInstance(t)
            if targetType.IsInstanceOfType(instance) then Some instance
            else None
        with _ -> None

    let hasParameterlessCtor (t: Type) =
        t.GetConstructor(Type.EmptyTypes) |> isNull |> not

    let loadAssembly () =
        if not (File.Exists assemblyPath) then
            Result.Error (FileNotFound assemblyPath)
        else
            try
                let asm = Assembly.LoadFrom(assemblyPath)
                Result.Ok asm
            with ex ->
                Result.Error (AssemblyLoadError (assemblyPath, ex.Message))

    let discoverAgents (asm: Assembly) : IAgent list =
        asm.GetExportedTypes()
        |> Array.filter (fun t ->
            not t.IsAbstract
            && typeof<IAgent>.IsAssignableFrom(t)
            && hasParameterlessCtor t)
        |> Array.choose (fun t -> tryCreateInstance typeof<IAgent> t |> Option.map (fun o -> o :?> IAgent))
        |> Array.toList

    let discoverTools (asm: Assembly) : Tool list =
        // Look for static properties that return Tool
        asm.GetExportedTypes()
        |> Array.collect (fun t ->
            t.GetProperties(BindingFlags.Public ||| BindingFlags.Static)
            |> Array.filter (fun p -> p.PropertyType = typeof<Tool>)
            |> Array.choose (fun p ->
                try
                    let tool = p.GetValue(null) :?> Tool
                    let memberName = sprintf "%s.%s" t.FullName p.Name
                    Some { tool with Provenance = Some (ToolProvenance.assembly assemblyPath memberName) }
                with _ -> None))
        |> Array.toList

    let discoverEvaluators (asm: Assembly) : IEvaluator list =
        asm.GetExportedTypes()
        |> Array.filter (fun t ->
            not t.IsAbstract
            && typeof<IEvaluator>.IsAssignableFrom(t)
            && hasParameterlessCtor t)
        |> Array.choose (fun t -> tryCreateInstance typeof<IEvaluator> t |> Option.map (fun o -> o :?> IEvaluator))
        |> Array.toList

    interface IDefinitionSource with
        member _.Name = sprintf "assembly:%s" (Path.GetFileName(assemblyPath))

        member _.Load() =
            match loadAssembly () with
            | Result.Error _ ->
                LoadedDefinitions.Empty
            | Result.Ok asm ->
                { Agents = []
                  Tools = []
                  EvalSuites = []
                  Constitutions = []
                  BuiltAgents = discoverAgents asm
                  BuiltTools = discoverTools asm
                  BuiltEvaluators = discoverEvaluators asm }

module AssemblySource =

    /// Create an assembly source from a DLL path
    let fromPath (dllPath: string) =
        AssemblySource(dllPath) :> IDefinitionSource

    /// Discover all plugin DLLs in a directory
    let fromDirectory (dir: string) : IDefinitionSource list =
        if Directory.Exists dir then
            Directory.GetFiles(dir, "*.dll")
            |> Array.map (fun f -> AssemblySource(f) :> IDefinitionSource)
            |> Array.toList
        else []
