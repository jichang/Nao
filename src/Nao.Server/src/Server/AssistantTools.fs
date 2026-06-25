namespace Nao.Assistant

open Nao.Agents

/// Aggregates the built-in assistant tools — defined per concern under `Tools/` — into the
/// single `allTools` list the runtime consumes, and re-exports the few members other modules
/// reference by their historical `AssistantTools.*` names. Each tool is permission-guarded and
/// its result clamped to the conversation budget before reaching the model.
module AssistantTools =

    /// Ensure and return the shared fallback workspace directory.
    let ensureWorkDir = ToolInfra.ensureWorkDir

    /// The document-conversion tool (re-exported for callers/tests using the legacy name).
    let convertDocument = DocumentTools.convertDocument

    /// Dynamic, mid-execution permission check a custom tool can call at runtime.
    let requestPermissionAsync = ToolPermissions.requestPermissionAsync

    let allTools =
        [ FileTools.createFolder; FileTools.writeFile; FileTools.readFile; FileTools.listFolder; FileTools.delete
          UtilityTools.dateTime; UtilityTools.calculator
          WebTools.httpRequest; WebTools.webFetch
          SearchTools.searchFiles; SearchTools.findFiles
          KnowledgeTools.searchKnowledge; DocumentTools.convertDocument ]
        // Enforce resource permissions first, then clamp every tool result so no tool
        // (current or future) can flood the conversation regardless of its output size.
        |> List.map (fun tool ->
            let guarded = ToolPermissions.guard tool
            { guarded with Execute = fun ctx input -> task { let! r = guarded.Execute ctx input in return clampText maxToolResultChars r } })
