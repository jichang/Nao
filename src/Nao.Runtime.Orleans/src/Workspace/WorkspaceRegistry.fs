namespace Nao.Runtime.Orleans

open Nao.Agents

/// Code-defined workspace contents registered by the host application.
/// Agents and tools are code-defined values; the runtime does not load JSON definitions
/// or assemblies dynamically.
type WorkspaceDefinitions =
    {
        Agents: Agent list
        Tools: Tool list
        Constitutions: Constitution list
    }

    static member Empty =
        { Agents = []
          Tools = []
          Constitutions = [] }

module WorkspaceDefinitions =

    let create (agents: Agent list) (tools: Tool list) (constitutions: Constitution list) =
        { Agents = agents
          Tools = tools
          Constitutions = constitutions }

    let merge (workspaces: WorkspaceDefinitions list) =
        { Agents = workspaces |> List.collect (fun w -> w.Agents)
          Tools = workspaces |> List.collect (fun w -> w.Tools)
          Constitutions = workspaces |> List.collect (fun w -> w.Constitutions) }

    let mergedConstitution (defs: WorkspaceDefinitions) : Constitution option =
        match defs.Constitutions with
        | [] -> None
        | [ constitution ] -> Some constitution
        | first :: rest ->
            let merged =
                rest
                |> List.fold (fun acc constitution ->
                    { acc with
                        Rules = acc.Rules @ constitution.Rules |> List.sortByDescending (fun rule -> rule.Priority)
                        Preamble =
                            match acc.Preamble, constitution.Preamble with
                            | Some a, Some b when not (System.String.IsNullOrWhiteSpace b) -> Some (a + "\n" + b)
                            | None, Some b -> Some b
                            | existing, _ -> existing }) first
            Some merged

/// Identifies a loaded workspace
type WorkspaceId = { Key: string }

module WorkspaceId =
    let create (key: string) = { Key = key }
    let defaultId = { Key = "default" }

    /// Build a version-qualified workspace id of the form "key@version".
    /// Lets two versions of the same logical workspace coexist as distinct entries.
    let versioned (key: string) (version: string) = { Key = sprintf "%s@%s" key version }

/// Functional registry that manages multiple workspaces within a single silo.
type WorkspaceRegistry =
    /// Get a workspace by key. Returns None if not registered.
    { TryGet: WorkspaceId -> WorkspaceDefinitions option
    /// Get a workspace by key, throwing if not found.
      Get: WorkspaceId -> WorkspaceDefinitions
    /// List all registered workspace keys.
      ListKeys: unit -> WorkspaceId list
    /// Register or update a workspace. Thread-safe.
      Register: WorkspaceId * WorkspaceDefinitions -> unit
    /// Remove a workspace from the registry.
      Remove: WorkspaceId -> bool }

module WorkspaceRegistry =

    /// Create an in-memory registry backed by a concurrent dictionary.
    let create () : WorkspaceRegistry =
        let workspaces = System.Collections.Concurrent.ConcurrentDictionary<string, WorkspaceDefinitions>()
        let tryGet (id: WorkspaceId) =
            match workspaces.TryGetValue(id.Key) with
            | true, defs -> Some defs
            | _ -> None
        { TryGet = tryGet
          Get = fun id ->
              match tryGet id with
              | Some defs -> defs
              | None -> failwithf "Workspace '%s' not registered" id.Key
          ListKeys = fun () -> workspaces.Keys |> Seq.map WorkspaceId.create |> Seq.toList
          Register = fun (id, defs) -> workspaces.[id.Key] <- defs
          Remove = fun id -> workspaces.TryRemove(id.Key) |> fst }

    /// Create a registry and register a default code-defined workspace.
    let fromWorkspace (defs: WorkspaceDefinitions) : WorkspaceRegistry =
        let reg = create ()
        reg.Register(WorkspaceId.defaultId, defs)
        reg

    /// Create a registry from multiple named code-defined workspaces.
    let fromWorkspaces (workspaces: (string * WorkspaceDefinitions) list) : WorkspaceRegistry =
        let reg = create ()
        for (key, defs) in workspaces do
            reg.Register(WorkspaceId.create key, defs)
        reg

    /// Create a registry from multiple versioned code-defined workspaces.
    /// Each entry is (key, version, defs); registered under the id "key@version"
    /// so multiple versions of the same logical workspace coexist side by side.
    let fromVersionedWorkspaces (workspaces: (string * string * WorkspaceDefinitions) list) : WorkspaceRegistry =
        let reg = create ()
        for (key, version, defs) in workspaces do
            reg.Register(WorkspaceId.versioned key version, defs)
        reg
