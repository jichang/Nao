namespace Nao.Assistant

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading.Tasks
open Nao.Agents

/// Routes interactive permission prompts over the per-session WebSocket. When a tool or
/// agent needs the user to approve access to a resource (the `Ask` outcome of the
/// `ResourcePermission` engine, or a dynamic request a tool makes mid-execution), the
/// enforcement layer calls `requestAsync`. The broker:
///   • finds the send channel registered for that session (wired by EmbeddedServer),
///   • pushes a `PermissionRequestDto` and parks a `TaskCompletionSource` keyed by a
///     correlation id,
///   • resumes when `resolve` is fed the matching `PermissionResponseDto` that arrives back
///     over the same socket, and
///   • persists an allow rule when the user chose to remember it for the session or globally.
/// No client connected, or no answer within the timeout, fails closed (Deny).
module PermissionBroker =

    let private json = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    /// How long to wait for the user before giving up and denying.
    let mutable Timeout = TimeSpan.FromMinutes 2.0

    // sessionKey ("userId/sessionId") -> a function that ships a serialized
    // PermissionRequestDto to that session's client over its WebSocket.
    let private senders = ConcurrentDictionary<string, string -> Task>()

    // correlation id -> the call parked waiting for the user's answer.
    let private pending = ConcurrentDictionary<string, TaskCompletionSource<PermissionResponseDto>>()

    /// Register the send channel for a connected session (called on WebSocket connect).
    let registerSession (sessionKey: string) (send: string -> Task) =
        senders.[sessionKey] <- send

    /// Drop a session's channel (called on WebSocket disconnect). Any calls still parked for
    /// that session will fall through to their timeout and deny.
    let unregisterSession (sessionKey: string) =
        senders.TryRemove(sessionKey) |> ignore

    /// Resolve the channel for a session. Falls back to the only connected session when the
    /// exact key is absent, so a single-user desktop session still works even if the tool's
    /// SessionExecution key and the WebSocket route key differ slightly.
    let private senderFor (sessionKey: string) : (string -> Task) option =
        match senders.TryGetValue sessionKey with
        | true, send -> Some send
        | _ ->
            if senders.Count = 1 then
                let mutable e = (senders :> seq<_>).GetEnumerator()
                if e.MoveNext() then Some e.Current.Value else None
            else
                None

    /// Feed a reply that arrived over the WebSocket back to the parked call.
    let resolve (payloadJson: string) =
        try
            let dto = JsonSerializer.Deserialize<PermissionResponseDto>(payloadJson, json)
            if not (isNull (box dto)) && not (String.IsNullOrEmpty dto.RequestId) then
                match pending.TryRemove dto.RequestId with
                | true, tcs -> tcs.TrySetResult dto |> ignore
                | _ -> ()
        with _ -> ()

    /// Build the allow rule to persist when the user remembers a grant.
    let private ruleFor (access: ResourceAccess) (scope: RuleScope) : PermissionRule =
        let kind, pattern =
            match access with
            | ResourceAccess.File(_, path) -> ResourceKind.File, path
            | ResourceAccess.Web(_, url) -> ResourceKind.Web, (ResourcePermission.hostOf url |> Option.defaultValue url)
            | ResourceAccess.ToolCall name -> ResourceKind.Tool, name
        { Id = ""
          Kind = kind
          // Empty operations = any operation on this resource, so remembering "read" also
          // covers a later "write" to the same path the user already trusted.
          Pattern = pattern
          Operations = []
          Decision = PermissionDecision.Allow
          Scope = scope
          CreatedAt = DateTimeOffset.UtcNow }

    /// Ask the user (over the session's WebSocket) to approve an access. Returns the
    /// resulting outcome: the decision plus whether the user chose to remember the grant for
    /// the session (so the session grain records it in its own state). A "global" grant is
    /// persisted here to the cross-session store; "once" persists nothing.
    let requestAsync (sessionKey: string) (access: ResourceAccess) (reason: string) : Task<PermissionOutcome> =
        task {
            match senderFor sessionKey with
            | None -> return { Decision = PermissionDecision.Deny; RememberForSession = false } // no client to ask → fail closed
            | Some send ->
                let requestId = Guid.NewGuid().ToString("N")
                let kind, op, resource =
                    match access with
                    | ResourceAccess.File(o, p) -> "file", o, p
                    | ResourceAccess.Web(o, u) -> "web", o, u
                    | ResourceAccess.ToolCall n -> "tool", "", n
                let dto =
                    { PermissionRequestDto.RequestId = requestId
                      Kind = kind
                      Operation = op
                      Resource = resource
                      Reason = reason }
                let tcs =
                    TaskCompletionSource<PermissionResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously)
                pending.[requestId] <- tcs
                try
                    do! send (JsonSerializer.Serialize(dto, json))
                    let! winner = Task.WhenAny(tcs.Task, Task.Delay Timeout)
                    if winner = (tcs.Task :> Task) then
                        let r = tcs.Task.Result
                        let allow =
                            not (isNull (box r))
                            && String.Equals(r.Decision, "allow", StringComparison.OrdinalIgnoreCase)
                        if allow then
                            match (if isNull (box r.Scope) then "" else r.Scope.ToLowerInvariant()) with
                            | "session" ->
                                // Remembered for the session: the session grain records this
                                // in its own state — nothing persisted here.
                                return { Decision = PermissionDecision.Allow; RememberForSession = true }
                            | "global" ->
                                PermissionStore.grant (ruleFor access RuleScope.Global) |> ignore
                                return { Decision = PermissionDecision.Allow; RememberForSession = false }
                            | _ ->
                                // "once" — allow this time only, persist nothing.
                                return { Decision = PermissionDecision.Allow; RememberForSession = false }
                        else
                            return { Decision = PermissionDecision.Deny; RememberForSession = false }
                    else
                        return { Decision = PermissionDecision.Deny; RememberForSession = false } // timed out → fail closed
                finally
                    pending.TryRemove requestId |> ignore
        }
