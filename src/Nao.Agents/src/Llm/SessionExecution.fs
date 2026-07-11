namespace Nao.Agents

/// Types describing background work a tool or orchestrator can launch. There is no ambient
/// state here: the session/turn identity a unit of work belongs to is carried explicitly on
/// the `ToolContext` the runtime threads into every tool and orchestrator.
module SessionExecution =

    /// Opaque handle returned by a runtime after it accepts background work.
    /// Presentation layers can use the metadata to render a localized response.
    type BackgroundTaskHandle =
        { TaskId: string
          Kind: string
          Title: string }

    /// A request to launch a background task. `Params` is an arbitrary, serializable
    /// key/value bag the task executor (keyed by `Kind`) knows how to interpret — e.g.
    /// for "document-conversion": source/target/media types; for "agent": agent/input.
    /// It is serialized to JSON so the owning task grain can persist and replay it.
    type TaskSpec =
        { /// Executor kind (e.g. "document-conversion", "agent").
          Kind: string
          /// Human-readable task title shown in the UI.
          Title: string
          /// Serializable parameters interpreted by the executor for this kind.
          Params: Map<string, string> }
