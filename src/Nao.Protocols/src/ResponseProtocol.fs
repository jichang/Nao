namespace Nao.Protocols

open System

/// Describes an LLM response protocol for prompt construction and discovery.
type ResponseProtocolDescriptor =
    { /// Stable protocol name, such as "compact tool lines".
      Name: string
      /// Short explanation of when and why the protocol is used.
      Description: string
      /// Exact rules to include in the model's system prompt.
      Instructions: string list
      /// Complete valid responses suitable for few-shot prompting.
      Examples: string list
      /// Media type of the response representation when one applies.
      MediaType: string option
      /// Extensible protocol metadata for hosts and tooling.
      Metadata: Map<string, string> }

/// Structured parse failure used to produce targeted LLM repair instructions.
type ResponseParseError =
    { /// Concise statement of what failed.
      Summary: string
      /// Location of the failure, for example "line 3, column 8".
      Location: string option
      /// Expected syntax or semantic shape at the failure point.
      Expected: string option
      /// Concrete correction the model should make.
      SuggestedFix: string option
      /// Machine-readable context for logging and custom repair strategies.
      Details: Map<string, string> }

[<RequireQualifiedAccess>]
module ResponseParseError =
    let create summary =
        { Summary = summary
          Location = None
          Expected = None
          SuggestedFix = None
          Details = Map.empty }

    /// Render a diagnostic without discarding its structured context.
    let format error =
        [ yield error.Summary
          match error.Location with
          | Some location -> yield sprintf "Location: %s." location
          | None -> ()
          match error.Expected with
          | Some expected -> yield sprintf "Expected: %s." expected
          | None -> ()
          match error.SuggestedFix with
          | Some fix -> yield sprintf "Suggested fix: %s." fix
          | None -> () ]
        |> String.concat " "

/// Owns one swappable LLM response protocol from prompting through resilient parsing.
type ResponseProtocol<'Action> =
    { Descriptor: ResponseProtocolDescriptor
      Parse: string -> Result<'Action list, ResponseParseError>
      BuildRepairMessage: ResponseParseError -> string }

[<RequireQualifiedAccess>]
module ResponseProtocol =
    /// Create a protocol from pure functions, making agent-specific formats easy to compose.
    let create descriptor parse buildRepairMessage : ResponseProtocol<'Action> =
        { Descriptor = descriptor
          Parse = parse
          BuildRepairMessage = buildRepairMessage }

    /// Render the protocol contract for inclusion in a system prompt.
    let promptInstructions (protocol: ResponseProtocol<'Action>) =
        let descriptor = protocol.Descriptor
        [ yield sprintf "# Response Protocol: %s" descriptor.Name
          yield descriptor.Description
          yield! descriptor.Instructions
          if not descriptor.Examples.IsEmpty then
              yield "Examples:"
              yield! descriptor.Examples ]
        |> String.concat "\n"
