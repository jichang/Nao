namespace Nao.Agents

open System

/// Immutable input to one governed harness execution.
type ExecutionRequest =
    { Authorization: AuthorizationScope
      TurnId: TurnId
      ConversationId: string
      AgentId: string
      Input: string
      Sandbox: SandboxConfig
      PolicyVersions: Map<string, string>
      DependencyVersions: Map<string, string>
      Correlation: CorrelationContext }

[<RequireQualifiedAccess>]
module ExecutionRequest =
    let private requireText name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name "Value must be non-blank and have no surrounding whitespace."

    let private validateVersions name versions =
        versions
        |> Map.iter (fun identity version ->
            requireText (sprintf "%s identity" name) identity
            requireText (sprintf "%s version" name) version)

    let create authorization turnId conversationId agentId input sandbox policyVersions dependencyVersions correlation =
        requireText (nameof conversationId) conversationId
        requireText (nameof agentId) agentId
        validateVersions (nameof policyVersions) policyVersions
        validateVersions (nameof dependencyVersions) dependencyVersions

        { Authorization = authorization
          TurnId = turnId
          ConversationId = conversationId
          AgentId = agentId
          Input = input
          Sandbox = sandbox
          PolicyVersions = policyVersions
          DependencyVersions = dependencyVersions
          Correlation = correlation }
