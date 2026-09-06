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

    let resolveWith prompt sessionKey access reason forceConfirm =
        match prompt with
        | Some resolve -> resolve sessionKey access reason forceConfirm
        | None ->
            Task.FromResult
                { Decision = PermissionDecision.Deny
                  RememberForSession = false }

    /// Resolve through the configured host bridge, denying when no bridge is installed.
    let resolve sessionKey access reason forceConfirm =
        resolveWith Prompt sessionKey access reason forceConfirm
