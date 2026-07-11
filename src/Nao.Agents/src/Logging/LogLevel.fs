namespace Nao.Agents

open System

/// Severity level for agent log entries
[<RequireQualifiedAccess>]
type LogLevel =
    | Trace
    | Debug
    | Info
    | Warning
    | Error
