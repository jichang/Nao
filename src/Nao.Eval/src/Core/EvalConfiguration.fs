namespace Nao.Eval

open System
open System.Security.Cryptography
open System.Text

[<RequireQualifiedAccess>]
type EvaluatorAuthority =
    | Deterministic
    | Probabilistic

type EvaluatorReliability =
    { ReleaseBlocking: bool
      FailureSemantics: string }

type EvalComponentIdentity =
    private
        { Name: string
          Version: string
          ConfigurationSha256: string }

[<RequireQualifiedAccess>]
module EvalComponentIdentity =
    let create (name: string) (version: string) (configuration: string) =
        for fieldName, value in [ nameof name, name; nameof version, version ] do
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg fieldName "Component identity values must be canonical and non-blank."

        if isNull configuration then
            nullArg (nameof configuration)

        let configurationSha256 =
            configuration
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexString
            |> _.ToLowerInvariant()

        { Name = name
          Version = version
          ConfigurationSha256 = configurationSha256 }

    let name identity = identity.Name
    let version identity = identity.Version
    let configurationSha256 identity = identity.ConfigurationSha256

type EvalExecutionSpecification =
    private
        { Agents: Map<string, EvalComponentIdentity>
          Prompt: EvalComponentIdentity
          ResponseProtocol: EvalComponentIdentity
          Tools: EvalComponentIdentity list
          Provider: EvalComponentIdentity option
          Model: EvalComponentIdentity option
          Harness: EvalComponentIdentity
          Policies: EvalComponentIdentity list
          KnowledgeIndexes: EvalComponentIdentity list }

type EvalExecutableConfiguration =
    { Agent: EvalComponentIdentity
      Prompt: EvalComponentIdentity
      ResponseProtocol: EvalComponentIdentity
      Tools: EvalComponentIdentity list
      Provider: EvalComponentIdentity option
      Model: EvalComponentIdentity option
      Harness: EvalComponentIdentity
      Policies: EvalComponentIdentity list
      KnowledgeIndexes: EvalComponentIdentity list
      Evaluators: EvalComponentIdentity list }

[<RequireQualifiedAccess>]
module EvalExecutionSpecification =
    let private unique fieldName identities =
        let duplicate =
            identities
            |> List.countBy (fun identity ->
                EvalComponentIdentity.name identity,
                EvalComponentIdentity.version identity,
                EvalComponentIdentity.configurationSha256 identity)
            |> List.tryFind (fun (_, count) -> count > 1)

        if duplicate.IsSome then
            invalidArg fieldName "Component identities must be unique within their configuration role."

    let create agents prompt responseProtocol tools provider model harness policies knowledgeIndexes =
        if Map.isEmpty agents then
            invalidArg (nameof agents) "Evaluation execution specifications require at least one agent identity."

        agents
        |> Map.iter (fun agentId identity ->
            if String.IsNullOrWhiteSpace agentId || agentId <> agentId.Trim() then
                invalidArg (nameof agents) "Agent lookup identities must be canonical and non-blank."

            if agentId <> EvalComponentIdentity.name identity then
                invalidArg (nameof agents) "Agent lookup keys must match component identity names.")

        unique (nameof tools) tools
        unique (nameof policies) policies
        unique (nameof knowledgeIndexes) knowledgeIndexes

        { Agents = agents
          Prompt = prompt
          ResponseProtocol = responseProtocol
          Tools = tools
          Provider = provider
          Model = model
          Harness = harness
          Policies = policies
          KnowledgeIndexes = knowledgeIndexes }

    let resolve agentId evaluatorIdentities specification =
        let agent =
            specification.Agents
            |> Map.tryFind agentId
            |> Option.defaultWith (fun () ->
                invalidArg (nameof agentId) (sprintf "Agent '%s' has no pinned evaluation component identity." agentId))

        { Agent = agent
          Prompt = specification.Prompt
          ResponseProtocol = specification.ResponseProtocol
          Tools = specification.Tools
          Provider = specification.Provider
          Model = specification.Model
          Harness = specification.Harness
          Policies = specification.Policies
          KnowledgeIndexes = specification.KnowledgeIndexes
          Evaluators = evaluatorIdentities }

    let requirePinnedLiveDependencies (specification: EvalExecutionSpecification) =
        if specification.Provider.IsNone || specification.Model.IsNone then
            invalidArg
                (nameof specification)
                "Pinned live evaluation requires explicit provider and model component identities."
