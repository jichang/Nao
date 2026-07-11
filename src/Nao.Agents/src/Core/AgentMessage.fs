namespace Nao.Agents

open System

/// A message passed between agents
type AgentMessage =
    { From: AgentId
      To: AgentId option
      Content: string
      Timestamp: DateTimeOffset
      Metadata: Map<string, string> }

module AgentMessage =

    /// Create a directed message from one agent to another
    let create (from: AgentId) (toAgent: AgentId) (content: string) =
        { From = from
          To = Some toAgent
          Content = content
          Timestamp = DateTimeOffset.UtcNow
          Metadata = Map.empty }

    /// Create a broadcast message (no specific recipient)
    let broadcast (from: AgentId) (content: string) =
        { From = from
          To = None
          Content = content
          Timestamp = DateTimeOffset.UtcNow
          Metadata = Map.empty }
