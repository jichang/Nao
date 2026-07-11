namespace Nao.Agents

open System
open System.Threading.Tasks

/// A rule in a declarative constitution that governs agent behavior
type ConstitutionRule =
    { /// Unique rule identifier
      Id: string
      /// Human-readable description of the rule
      Description: string
      /// The rule category
      Category: RuleCategory
      /// Priority (higher = more important, used for conflict resolution)
      Priority: int
      /// Whether violating this rule should halt execution
      IsHardConstraint: bool
      /// The rule predicate — returns true if the content violates the rule
      Check: string -> bool }

/// Categories of constitutional rules
and [<RequireQualifiedAccess>] RuleCategory =
    /// Safety rules (prevent harm)
    | Safety
    /// Privacy rules (protect information)
    | Privacy
    /// Behavioral rules (maintain consistency)
    | Behavioral
    /// Output format rules
    | Format
    /// Domain-specific rules
    | Domain of name: string

/// Result of constitution evaluation
type ConstitutionCheckResult =
    { /// Whether the content passes all rules
      Passed: bool
      /// Rules that were violated
      Violations: ConstitutionViolation list
      /// Rules that passed
      PassedRules: string list }

/// A single rule violation
and ConstitutionViolation =
    { /// The violated rule
      RuleId: string
      /// Description of the rule
      RuleDescription: string
      /// Whether this is a hard constraint violation
      IsHardViolation: bool
      /// The content that violated the rule
      ViolatingContent: string option }

/// A declarative constitution governing agent behavior
type Constitution =
    { /// Name of this constitution
      Name: string
      /// Version
      Version: string
      /// Rules in priority order
      Rules: ConstitutionRule list
      /// System prompt preamble derived from the constitution
      Preamble: string option }

module Constitution =

    /// Create an empty constitution
    let empty (name: string) : Constitution =
        { Name = name
          Version = "1.0"
          Rules = []
          Preamble = None }

    /// Add a rule to the constitution
    let addRule (rule: ConstitutionRule) (constitution: Constitution) : Constitution =
        { constitution with Rules = constitution.Rules @ [ rule ] |> List.sortByDescending (fun r -> r.Priority) }

    /// Check content against all rules
    let check (constitution: Constitution) (content: string) : ConstitutionCheckResult =
        let violations = ResizeArray<ConstitutionViolation>()
        let passed = ResizeArray<string>()

        for rule in constitution.Rules do
            if rule.Check content then
                violations.Add
                    { RuleId = rule.Id
                      RuleDescription = rule.Description
                      IsHardViolation = rule.IsHardConstraint
                      ViolatingContent = Some (content.Substring(0, min 200 content.Length)) }
            else
                passed.Add rule.Id

        { Passed = violations.Count = 0
          Violations = violations |> Seq.toList
          PassedRules = passed |> Seq.toList }

    /// Check if any hard constraints are violated
    let hasHardViolations (result: ConstitutionCheckResult) : bool =
        result.Violations |> List.exists (fun v -> v.IsHardViolation)

    /// Render the constitution as a system prompt section
    let renderForPrompt (constitution: Constitution) : string =
        let rulesStr =
            constitution.Rules
            |> List.map (fun r ->
                let enforcement = if r.IsHardConstraint then "[MUST]" else "[SHOULD]"
                sprintf "%s %s" enforcement r.Description)
            |> String.concat "\n"

        let preamble = constitution.Preamble |> Option.defaultValue ""

        sprintf "# Constitution: %s\n%s\n\n## Rules\n%s" constitution.Name preamble rulesStr

    /// Common safety rules
    let noHarmRule : ConstitutionRule =
        { Id = "safety-no-harm"
          Description = "Do not generate content that could cause physical, emotional, or financial harm"
          Category = RuleCategory.Safety
          Priority = 100
          IsHardConstraint = true
          Check = fun _ -> false } // Placeholder — real implementation would use content filtering

    let noPrivateDataRule : ConstitutionRule =
        { Id = "privacy-no-pii"
          Description = "Do not output personally identifiable information (PII) including emails, phone numbers, or addresses"
          Category = RuleCategory.Privacy
          Priority = 90
          IsHardConstraint = true
          Check = fun content ->
              // Simple heuristic check for common PII patterns
              let emailPattern = System.Text.RegularExpressions.Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b")
              let phonePattern = System.Text.RegularExpressions.Regex(@"\b\d{3}[-.]?\d{3}[-.]?\d{4}\b")
              emailPattern.IsMatch(content) || phonePattern.IsMatch(content) }
