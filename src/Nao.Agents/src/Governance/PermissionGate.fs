namespace Nao.Agents

open System.Threading.Tasks

/// Result of a host permission prompt.
type PermissionOutcome =
    { Decision: PermissionDecision
      RememberForSession: bool }

/// Process-level bridge used by runtimes that cannot directly reference the host's permission UI.
[<RequireQualifiedAccess>]
module PermissionGate =
    /// Optional host callback for resolving permission requests interactively.
    let mutable Prompt: (string -> ResourceAccess -> string -> bool -> Task<PermissionOutcome>) option =
        None
