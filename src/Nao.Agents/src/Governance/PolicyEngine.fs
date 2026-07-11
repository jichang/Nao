namespace Nao.Agents

open System
open System.Threading.Tasks

/// A governance policy that can be enforced at runtime
type Policy =
    { /// Policy identifier
      Id: string
      /// Human-readable description
      Description: string
      /// The enforcement action
      Enforcement: PolicyEnforcement
      /// The check function: returns None if policy passes, Some error message if violated
      Evaluate: PolicyContext -> string option }

/// How a policy violation is handled
and [<RequireQualifiedAccess>] PolicyEnforcement =
    /// Block the action entirely
    | Block
    /// Allow but log a warning
    | Warn
    /// Allow but require confirmation callback
    | Confirm
    /// Modify the action (e.g., redact content)
    | Modify of transform: (string -> string)

/// Context passed to policy evaluation
and PolicyContext =
    { AgentId: AgentId
      Action: string
      Input: string option
      ExecutionId: Guid option
      CurrentUsage: ResourceUsage option }

    /// Create a PolicyContext from an ExecutionContext (canonical factory)
    static member FromExecutionContext (agentId: AgentId) (action: string) (input: string option) (ctx: ExecutionContext) =
        { AgentId = agentId
          Action = action
          Input = input
          ExecutionId = Some ctx.ExecutionId
          CurrentUsage = Some ctx.Usage }

/// Result of policy engine evaluation
type PolicyResult =
    { /// Whether execution should proceed
      Proceed: bool
      /// Policies that were violated
      Violations: PolicyViolation list
      /// Modified input (if any policy transforms applied)
      ModifiedInput: string option
      /// Warnings generated
      Warnings: string list }

and PolicyViolation =
    { PolicyId: string
      Description: string
      Enforcement: PolicyEnforcement
      Message: string }

/// Runtime policy engine that evaluates all registered policies
type PolicyEngine(policies: Policy list) =

    /// Evaluate all policies against a context
    member _.Evaluate(context: PolicyContext) : PolicyResult =
        let violations = ResizeArray<PolicyViolation>()
        let warnings = ResizeArray<string>()
        let mutable blocked = false
        let mutable modifiedInput = context.Input

        for policy in policies |> List.sortByDescending (fun p -> match p.Enforcement with PolicyEnforcement.Block -> 3 | PolicyEnforcement.Confirm -> 2 | PolicyEnforcement.Warn -> 1 | PolicyEnforcement.Modify _ -> 0) do
            match policy.Evaluate context with
            | Some message ->
                let violation =
                    { PolicyId = policy.Id
                      Description = policy.Description
                      Enforcement = policy.Enforcement
                      Message = message }
                violations.Add(violation)

                match policy.Enforcement with
                | PolicyEnforcement.Block ->
                    blocked <- true
                | PolicyEnforcement.Warn ->
                    warnings.Add(sprintf "[Policy %s]: %s" policy.Id message)
                | PolicyEnforcement.Confirm ->
                    blocked <- true // Requires external confirmation
                | PolicyEnforcement.Modify transform ->
                    modifiedInput <- modifiedInput |> Option.map transform
            | None -> ()

        { Proceed = not blocked
          Violations = violations |> Seq.toList
          ModifiedInput = modifiedInput
          Warnings = warnings |> Seq.toList }

module PolicyEngine =

    /// Create a policy engine from a list of policies
    let create (policies: Policy list) : PolicyEngine = PolicyEngine(policies)

    /// Cost budget policy: blocks when estimated cost exceeds budget
    let costBudgetPolicy (maxUsd: decimal) : Policy =
        { Id = "cost-budget"
          Description = sprintf "Enforce maximum cost budget of $%.2f" maxUsd
          Enforcement = PolicyEnforcement.Block
          Evaluate = fun ctx ->
              match ctx.CurrentUsage with
              | Some usage when usage.EstimatedCostUsd > maxUsd ->
                  Some (sprintf "Cost budget exceeded: $%.4f > $%.2f" usage.EstimatedCostUsd maxUsd)
              | _ -> None }

    /// Rate limit policy: blocks when too many actions in a time window
    let rateLimitPolicy (capability: string) (maxPerMinute: int) : Policy =
        let timestamps = System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset>()
        { Id = sprintf "rate-limit-%s" capability
          Description = sprintf "Rate limit %s to %d per minute" capability maxPerMinute
          Enforcement = PolicyEnforcement.Block
          Evaluate = fun ctx ->
              if ctx.Action = capability then
                  let now = DateTimeOffset.UtcNow
                  let cutoff = now.AddMinutes(-1.0)
                  let mutable item = DateTimeOffset.MinValue
                  while timestamps.TryPeek(&item) && item < cutoff do
                      timestamps.TryDequeue(&item) |> ignore
                  if timestamps.Count >= maxPerMinute then
                      Some (sprintf "Rate limit exceeded: %d/%d calls in last minute" timestamps.Count maxPerMinute)
                  else
                      timestamps.Enqueue(now)
                      None
              else None }

    /// Content length policy: blocks excessively long outputs
    let maxOutputLengthPolicy (maxChars: int) : Policy =
        { Id = "max-output-length"
          Description = sprintf "Limit output to %d characters" maxChars
          Enforcement = PolicyEnforcement.Modify (fun s -> if s.Length > maxChars then s.Substring(0, maxChars) + "... [truncated]" else s)
          Evaluate = fun ctx ->
              match ctx.Input with
              | Some input when input.Length > maxChars ->
                  Some (sprintf "Output exceeds maximum length: %d > %d" input.Length maxChars)
              | _ -> None }
