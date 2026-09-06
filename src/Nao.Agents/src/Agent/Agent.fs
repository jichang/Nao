namespace Nao.Agents

open System.Threading.Tasks

/// Declares the transport representation accepted or returned by an agent.
/// Structured schemas are authored text; the runtime does not infer them from CLR types.
[<RequireQualifiedAccess>]
type AgentParameter =
    /// An unstructured text value.
    | Text
    /// A structured value described by the supplied schema.
    | Structured of schema: string

/// Explicit transport contract advertised by an agent.
type AgentContract =
    { Input: AgentParameter
      Output: AgentParameter }

[<RequireQualifiedAccess>]
module AgentContract =
    /// Contract for agents that accept and return plain text.
    let Text =
        { Input = AgentParameter.Text
          Output = AgentParameter.Text }

/// Metadata carried by an immutable functional agent program.
type AgentMetadata =
    { Id: string
      Name: string
      Description: string
      Priority: int
      Responsibilities: string list
      Contract: AgentContract }

/// Immutable executable agent capability represented entirely by data and functions.
type Agent =
    { Metadata: AgentMetadata
      Execute: AgentContext -> string -> Task<string> }

[<RequireQualifiedAccess>]
module Agent =

    /// Construct an immutable agent capability from metadata and executable functions.
    let create id name description priority responsibilities contract execute =
        let metadata =
            { Id = id
              Name = name
              Description = description
              Priority = priority
              Responsibilities = responsibilities
              Contract = contract }

        { Metadata = metadata
          Execute = execute }

    /// Execute an agent program.
    let runAsync context input (agent: Agent) = agent.Execute context input
