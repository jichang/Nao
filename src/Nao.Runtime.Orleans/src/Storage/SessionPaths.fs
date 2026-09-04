namespace Nao.Runtime.Orleans

open System
open System.IO

/// Single source of truth for mapping a session grain key to its on-disk directory.
///
/// A session key is a '/'-separated path such as "userId/sessionId". The first two
/// segments name the session folder ("userId_sessionId").
module SessionPaths =

    /// Make a single key segment safe to use as a folder name.
    let sanitizeSegment (s: string) =
        (if isNull s then "" else s)
        |> String.map (fun c ->
            if Char.IsLetterOrDigit c || c = '-' || c = '_' then
                c
            else
                '_')

    /// Split a session key into its sanitized, non-empty segments.
    let segments (sessionKey: string) =
        (if isNull sessionKey then "" else sessionKey).Split('/')
        |> Array.filter (fun s -> not (String.IsNullOrWhiteSpace s))
        |> Array.map sanitizeSegment

    /// Resolve a session key to its data directory under `sessionsRoot`.
    let sessionDir (sessionsRoot: string) (sessionKey: string) =
        match segments sessionKey with
        | [||] -> sessionsRoot
        | segs ->
            let baseName =
                if segs.Length = 1 then
                    segs.[0]
                else
                    segs.[0] + "_" + segs.[1]

            Path.Combine(sessionsRoot, baseName)
