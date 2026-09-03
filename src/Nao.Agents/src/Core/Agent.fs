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
type AgentContract = { Input: AgentParameter; Output: AgentParameter }

[<RequireQualifiedAccess>]
module AgentContract =
    /// Contract for agents that accept and return plain text.
    let Text = { Input = AgentParameter.Text; Output = AgentParameter.Text }

/// Metadata carried by an immutable functional agent program.
type AgentMetadata = { Id: string; Name: string; Description: string; Priority: int; Responsibilities: string list; Contract: AgentContract }

/// Immutable executable agent capability represented entirely by data and functions.
type Agent = { Metadata: AgentMetadata; Execute: AgentContext -> string -> Task<string>; HandleMessage: AgentContext -> AgentMessage -> Task<AgentMessage option> }

[<RequireQualifiedAccess>]
module Agent =

    /// Construct an immutable agent capability from metadata and executable functions.
    let create id name description priority responsibilities contract execute handleMessage =
        let metadata = { Id = id; Name = name; Description = description; Priority = priority; Responsibilities = responsibilities; Contract = contract }
        { Metadata = metadata; Execute = execute; HandleMessage = handleMessage }

    /// Construct an agent whose message handling delegates to normal execution.
    let createContextual id name description priority responsibilities contract execute =
        let handleMessage context (message: AgentMessage) =
            task {
                let! output = execute context message.Content
                return Some(AgentMessage.create id message.From output)
            }

        create id name description priority responsibilities contract execute handleMessage

    /// Execute an agent program.
    let runAsync context input (agent: Agent) = agent.Execute context input

    /// Deliver an inter-agent message.
    let handleMessageAsync context message (agent: Agent) = agent.HandleMessage context message

