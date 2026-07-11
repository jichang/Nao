namespace Nao.Agents

/// Unique identity for an agent in a multi-agent system
type AgentId =
    { /// Short unique name (e.g. "weather-agent", "orchestrator")
      Name: string
      /// Human-readable description of the agent's purpose
      Description: string }
