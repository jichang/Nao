namespace Nao.Agents

/// Built-in LLM specialist that interprets memory requests and curates results through
/// deterministic, host-scoped memory tools.
[<RequireQualifiedAccess>]
module MemoryAgent =
    let id = "nao.memory"
    let name = "memory"

    let private prompt =
        { Prompt.Empty with
            Role =
                "You are a memory specialist. You help another agent deliberately recall and maintain durable user and project knowledge."
            Objective =
                "Interpret the caller's memory need, use the available memory tools, and return only the context or management outcome needed by the caller."
            Constraints =
                [ "Use memory_search one or more times when recalling information. Refine the query when an initial search is insufficient."
                  "Distinguish durable preferences, facts, entities, and decisions from transient task details."
                  "Use memory_remember to create or replace a stable key only when the caller requests a durable update or the user explicitly asked to remember it."
                  "Use memory_forget only when it is available and the user explicitly requested deletion of an exact recalled key; otherwise never claim a memory was deleted."
                  "When new information conflicts with an existing memory, search first, then replace the same stable key or preserve both facts when the conflict cannot be resolved."
                  "Never invent a remembered fact. Treat tool results as the only evidence of stored memory."
                  "Return concise relevant context with memory keys when recalling. For writes, report exactly what changed."
                  "Use the available tool descriptions and schemas as the source of truth."
                  "Finish with respond after the memory task is complete." ] }

    let create (factory: OrchestratorFactory) (provider: LlmProvider) (tools: Tool list) : Agent =
        factory.Create
            { Id = id
              Name = name
              Description = "Recalls and maintains durable session knowledge using deliberate multi-step reasoning."
              Priority = 1000
              Responsibilities =
                [ "Recall prior preferences, facts, decisions, people, projects, and earlier work"
                  "Create or update durable memories"
                  "Resolve memory conflicts through explicit retrieval before writing" ]
              Contract = AgentContract.Text
              Provider = provider
              Tools = tools
              SubAgents = []
              Prompt = prompt
              Options =
                { CompletionOptions.Default with
                    Temperature = 0.0
                    MaxTokens = Some 800 }
              MaxRounds = 5
              Bus = EventBus.none
              Scope = EventScope.CreateEmpty() }

    let asTool (agent: Agent) : Tool =
        AgentTool.create
            "memory"
            "Ask the memory specialist to recall, reconcile, create, or update durable session knowledge. Use it when the current task depends on prior context or the user asks to remember something."
            1000
            "object\n  - request (required): string - What should be recalled or changed.\n  - purpose (required): string - Why the caller needs this memory operation now."
            "string - Curated relevant context or a precise memory-management outcome."
            agent
