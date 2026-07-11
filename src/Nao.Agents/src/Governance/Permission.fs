namespace Nao.Agents

open System
open System.Threading.Tasks

/// Permission level for a specific capability
[<RequireQualifiedAccess>]
type PermissionLevel =
    /// Fully allowed
    | Allow
    /// Allowed but logged for audit
    | AllowWithAudit
    /// Requires explicit confirmation before proceeding
    | RequireConfirmation
    /// Denied
    | Deny

/// A specific permission grant for a capability
type Permission =
    { /// What capability this permission covers
      Capability: string
      /// The permission level
      Level: PermissionLevel
      /// Optional conditions under which this permission applies
      Conditions: string list
      /// Who granted this permission
      GrantedBy: string option
      /// When this permission expires (None = never)
      ExpiresAt: DateTimeOffset option }

/// Permission scope categories
[<RequireQualifiedAccess>]
type PermissionScope =
    /// Access to specific tools
    | Tool of toolName: string
    /// Access to delegate to other agents
    | Delegation of agentName: string
    /// Access to external resources (network, filesystem)
    | Resource of resourceType: string
    /// Access to memory operations
    | Memory of operation: string
    /// Maximum cost budget
    | CostBudget of maxUsd: decimal

/// The permission model governing an agent's allowed actions
type PermissionModel =
    { /// Agent this model applies to
      AgentId: AgentId
      /// Explicit permission grants
      Permissions: Permission list
      /// Default permission for unlisted capabilities
      DefaultLevel: PermissionLevel
      /// Scoped permissions
      Scopes: PermissionScope list }

    static member Permissive agentId =
        { AgentId = agentId
          Permissions = []
          DefaultLevel = PermissionLevel.Allow
          Scopes = [] }

    static member Restrictive agentId =
        { AgentId = agentId
          Permissions = []
          DefaultLevel = PermissionLevel.Deny
          Scopes = [] }

module PermissionModel =

    /// Check if an action is permitted under the model
    let check (model: PermissionModel) (capability: string) : PermissionLevel =
        match model.Permissions |> List.tryFind (fun p -> p.Capability = capability) with
        | Some p ->
            // Check expiry
            match p.ExpiresAt with
            | Some expiry when DateTimeOffset.UtcNow > expiry -> PermissionLevel.Deny
            | _ -> p.Level
        | None -> model.DefaultLevel

    /// Add a permission to the model
    let grant (capability: string) (level: PermissionLevel) (model: PermissionModel) : PermissionModel =
        let perm =
            { Capability = capability
              Level = level
              Conditions = []
              GrantedBy = None
              ExpiresAt = None }
        { model with Permissions = perm :: model.Permissions }

    /// Revoke a permission (sets to Deny)
    let revoke (capability: string) (model: PermissionModel) : PermissionModel =
        grant capability PermissionLevel.Deny model

    /// Check tool access
    let canUseTool (model: PermissionModel) (toolName: string) : PermissionLevel =
        check model (sprintf "tool:%s" toolName)

    /// Check delegation access
    let canDelegateTo (model: PermissionModel) (agentName: string) : PermissionLevel =
        check model (sprintf "delegate:%s" agentName)
