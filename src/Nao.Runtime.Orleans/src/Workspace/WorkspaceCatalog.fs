namespace Nao.Runtime.Orleans

open System
open System.Collections.Concurrent
open Nao.Agents

/// Code-defined workspace contents registered by the host application. Executable values
/// are compiled into the trusted host or addressed through an explicitly configured worker;
/// the runtime never discovers or loads arbitrary assemblies.
type WorkspaceDefinitions =
    { Agents: Agent list
      Tools: Tool list
      Constitutions: Constitution list }

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
        { Agents = workspaces |> List.collect (fun workspace -> workspace.Agents)
          Tools = workspaces |> List.collect (fun workspace -> workspace.Tools)
          Constitutions = workspaces |> List.collect (fun workspace -> workspace.Constitutions) }

    let mergedConstitution (definitions: WorkspaceDefinitions) : Constitution option =
        match definitions.Constitutions with
        | [] -> None
        | [ constitution ] -> Some constitution
        | _ ->
            invalidArg
                (nameof definitions)
                "A workspace must select exactly one explicitly versioned constitution; implicit constitution merging is not supported."

/// A declared external contract required by a compiled workspace.
[<RequireQualifiedAccess>]
type WorkspaceDependencyKind =
    | Provider
    | Policy
    | Schema
    | Migration
    | WorkerExtension

type WorkspaceDependency =
    { Kind: WorkspaceDependencyKind
      Id: string
      Version: string }

/// Host/runtime contracts against which the workspace was compiled and validated.
type WorkspaceCompatibility =
    { RuntimeContractVersion: string
      StateSchemaVersion: string
      Dependencies: WorkspaceDependency list }

/// Immutable compiled workspace registration. ContentDigest identifies the exact host build
/// or signed worker bundle supplying the executable definitions.
type WorkspaceRegistration =
    { Identity: WorkspaceVersionId
      ContentDigest: string
      Compatibility: WorkspaceCompatibility
      Definitions: WorkspaceDefinitions }

module WorkspaceRegistration =
    let currentRuntimeContractVersion = "1"

    let create identity contentDigest stateSchemaVersion dependencies definitions =
        { Identity = identity
          ContentDigest = contentDigest
          Compatibility =
            { RuntimeContractVersion = currentRuntimeContractVersion
              StateSchemaVersion = stateSchemaVersion
              Dependencies = dependencies }
          Definitions = definitions }

module private WorkspaceRegistrationValidation =
    let private required fieldName value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg fieldName "Value must be non-blank with no surrounding whitespace."

        value

    let private validateDigest (digest: string) =
        let prefix = "sha256:"

        if
            digest.Length <> prefix.Length + 64
            || not (digest.StartsWith(prefix, StringComparison.Ordinal))
            || digest.Substring(prefix.Length)
               |> Seq.exists (fun character -> not (Char.IsAsciiHexDigitLower character))
        then
            invalidArg
                (nameof digest)
                "Workspace content digest must be canonical lowercase 'sha256:<64 hex characters>'."

    let private rejectDuplicates<'Value, 'Identity when 'Identity: equality>
        fieldName
        (projection: 'Value -> 'Identity)
        (values: 'Value seq)
        =
        values
        |> Seq.groupBy projection
        |> Seq.tryFind (fun (_, matches) -> Seq.length matches > 1)
        |> Option.iter (fun (identity, _) -> invalidArg fieldName $"Duplicate workspace identity '{identity}'.")

    let validate (registration: WorkspaceRegistration) =
        validateDigest registration.ContentDigest

        if
            registration.Compatibility.RuntimeContractVersion
            <> WorkspaceRegistration.currentRuntimeContractVersion
        then
            invalidArg
                (nameof registration)
                $"Workspace runtime contract '{registration.Compatibility.RuntimeContractVersion}' is unsupported."

        required (nameof registration.Compatibility.StateSchemaVersion) registration.Compatibility.StateSchemaVersion
        |> ignore

        registration.Compatibility.Dependencies
        |> List.iter (fun dependency ->
            required (nameof dependency.Id) dependency.Id |> ignore
            required (nameof dependency.Version) dependency.Version |> ignore)

        rejectDuplicates
            (nameof registration.Compatibility.Dependencies)
            (fun (dependency: WorkspaceDependency) -> dependency.Kind, dependency.Id)
            registration.Compatibility.Dependencies

        registration.Definitions.Agents
        |> List.iter (fun agent ->
            required (nameof agent.Metadata.Id) agent.Metadata.Id |> ignore
            required (nameof agent.Metadata.Name) agent.Metadata.Name |> ignore

            match agent.Metadata.Contract.Input, agent.Metadata.Contract.Output with
            | AgentParameter.Structured input, _ -> required "agent input schema" input |> ignore
            | _ -> ()

            match agent.Metadata.Contract.Output with
            | AgentParameter.Structured output -> required "agent output schema" output |> ignore
            | _ -> ())

        rejectDuplicates
            (nameof registration.Definitions.Agents)
            (fun (agent: Agent) -> agent.Metadata.Id)
            registration.Definitions.Agents

        rejectDuplicates
            (nameof registration.Definitions.Agents)
            (fun (agent: Agent) -> agent.Metadata.Name)
            registration.Definitions.Agents

        registration.Definitions.Tools
        |> List.iter (fun tool ->
            required (nameof tool.Name) tool.Name |> ignore
            required "tool input schema" tool.Schema.Input |> ignore
            required "tool output schema" tool.Schema.Output |> ignore)

        rejectDuplicates
            (nameof registration.Definitions.Tools)
            (fun (tool: Tool) -> tool.Name)
            registration.Definitions.Tools

        WorkspaceDefinitions.mergedConstitution registration.Definitions |> ignore

/// Process-local catalog of trusted executable workspace versions. Registrations are immutable:
/// lifecycle retirement affects routing, not catalog availability, so pinned sessions can keep
/// resolving the exact code version they started with.
type WorkspaceCatalog =
    { TryGet: TenantId * WorkspaceVersionId -> WorkspaceRegistration option
      Get: TenantId * WorkspaceVersionId -> WorkspaceRegistration
      ListVersions: TenantId * string -> WorkspaceVersionId list
      Register: TenantId * WorkspaceRegistration -> unit }

module WorkspaceCatalog =
    let create (availableDependencies: Set<WorkspaceDependencyKind * string * string>) : WorkspaceCatalog =
        let registrations = ConcurrentDictionary<string * string, WorkspaceRegistration>()

        let tryGet (tenantId: TenantId, identity: WorkspaceVersionId) =
            let key = TenantId.value tenantId, WorkspaceVersionId.value identity

            match registrations.TryGetValue key with
            | true, registration -> Some registration
            | _ -> None

        { TryGet = tryGet
          Get =
            (fun (tenantId, identity) ->
                tryGet (tenantId, identity)
                |> Option.defaultWith (fun () ->
                    failwithf
                        "Workspace version '%s/%s' is not registered."
                        (TenantId.value tenantId)
                        (WorkspaceVersionId.value identity)))
          ListVersions =
            (fun (tenantId, name) ->
                registrations
                |> Seq.choose (fun pair ->
                    let tenant, _ = pair.Key
                    let identity = pair.Value.Identity

                    if tenant = TenantId.value tenantId && WorkspaceVersionId.name identity = name then
                        Some identity
                    else
                        None)
                |> Seq.sortBy WorkspaceVersionId.value
                |> Seq.toList)
          Register =
            (fun (tenantId, registration) ->
                WorkspaceRegistrationValidation.validate registration

                registration.Compatibility.Dependencies
                |> List.iter (fun dependency ->
                    if not (availableDependencies.Contains(dependency.Kind, dependency.Id, dependency.Version)) then
                        invalidOp
                            $"Workspace dependency '{dependency.Kind}/{dependency.Id}@{dependency.Version}' is not available in the validated host manifest.")

                let key = TenantId.value tenantId, WorkspaceVersionId.value registration.Identity

                if not (registrations.TryAdd(key, registration)) then
                    invalidOp $"Workspace version '{fst key}/{snd key}' is already registered and cannot be replaced.") }
