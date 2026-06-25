namespace Nao.Assistant

open System
open System.IO
open System.Text.Json
open Nao.Core
open Nao.Agents

/// Shared infrastructure for the built-in assistant tools: the per-session working
/// directory, traversal-safe path resolution, the conversation-budget clamp, and the
/// compact JSON helper. Marked `[<AutoOpen>]` so each tool file in this namespace can use
/// these by their short names, exactly as when they all lived in one module.
[<AutoOpen>]
module ToolInfra =

    /// Shared fallback workspace used only when no session turn is active (e.g. tests).
    /// Lives UNDER the single app data directory (`.nao-data`, override with NAO_DATA_DIR)
    /// so there is only one base folder — the legacy `~/.nao-workspace` is no longer used.
    let private globalWorkDir =
        let dataDir =
            match Environment.GetEnvironmentVariable("NAO_DATA_DIR") with
            | path when not (String.IsNullOrWhiteSpace path) -> path
            | _ -> Path.Combine(Environment.CurrentDirectory, ".nao-data")
        Path.Combine(dataDir, "workspace")

    /// The directory all file tools operate in. File storage is unified on the current
    /// session's files folder — the same place uploads and generated files live and the UI
    /// lists — so a user's attachments and a tool's output share one location. Falls back to
    /// the shared workspace when there is no active session. The directory is ensured.
    let currentWorkDir () =
        let dir =
            match SessionExecution.current () with
            | Some scope -> (SessionFiles.forKey scope.FilesKey).FilesDir
            | None -> globalWorkDir
        Directory.CreateDirectory dir |> ignore
        dir

    /// Resolve a user-supplied relative path inside the current working directory,
    /// preventing traversal outside it via "..".
    let resolvePath (input: string) =
        let root = currentWorkDir ()
        let cleaned = input.Trim().Replace("\\", "/").TrimStart('/')
        let full = Path.GetFullPath(Path.Combine(root, cleaned))
        let rootFull = Path.GetFullPath(root)
        if full = rootFull || full.StartsWith(rootFull + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
        then full
        else Path.GetFullPath(Path.Combine(root, Path.GetFileName cleaned))

    // ─── Conversation-budget guards ───
    // Large file content must stay on disk, not flood the LLM conversation. read_file
    // returns a bounded window (page through with offset/length); write_file rejects a
    // single oversized blob (build large files with several append calls); and every
    // tool result is clamped before it is handed back to the model.

    /// Max characters any single tool result may contribute to the conversation.
    let maxToolResultChars = 24000

    /// Clamp text to a budget, appending a note that records the original size.
    let clampText (max: int) (s: string) =
        if not (isNull s) && s.Length > max then
            s.Substring(0, max) + sprintf "\n…(truncated to %d of %d chars)" max s.Length
        else s

    /// Serialize a value (typically an anonymous record) to a compact JSON tool result.
    /// Preferred over hand-writing JSON so strings are always correctly escaped.
    let json (value: 'T) = JsonSerializer.Serialize value

    /// Declare a required parameter for a tool's `Schema`.
    let reqParam (name: string) (typ: string) (description: string) : ToolParameter =
        { Name = name; Description = description; Type = typ; Required = true; Default = None; Examples = [] }

    /// Declare an optional parameter (with an optional default) for a tool's `Schema`.
    let optParam (name: string) (typ: string) (defaultValue: string option) (description: string) : ToolParameter =
        { Name = name; Description = description; Type = typ; Required = false; Default = defaultValue; Examples = [] }

    /// Parsed view of a tool's input. Tool inputs are JSON objects (e.g.
    /// `{"path":"a.txt","content":"hi"}`) so they share the one format the planner already
    /// uses for actions — no second ad-hoc delimiter syntax. `Obj` is the parsed object when
    /// the input was a JSON object; `Raw` keeps the original trimmed text so a tool with a
    /// single parameter can still accept a bare string for convenience.
    type ToolArgs =
        { Obj: JsonElement option
          Raw: string }

        /// The named string field, or None when absent/null. Non-string JSON values are
        /// returned as their raw JSON text so callers can still read e.g. numbers as strings.
        member this.TryString(name: string) : string option =
            match this.Obj with
            | Some el ->
                match el.TryGetProperty name with
                | true, v ->
                    match v.ValueKind with
                    | JsonValueKind.String -> Some(v.GetString())
                    | JsonValueKind.Null | JsonValueKind.Undefined -> None
                    | _ -> Some(v.GetRawText())
                | _ -> None
            | None -> None

        /// The named integer field (accepts a JSON number or a numeric string), or None.
        member this.TryInt(name: string) : int option =
            match this.Obj with
            | Some el ->
                match el.TryGetProperty name with
                | true, v ->
                    match v.ValueKind with
                    | JsonValueKind.Number -> (match v.TryGetInt32() with | true, n -> Some n | _ -> None)
                    | JsonValueKind.String -> (match Int32.TryParse(v.GetString()) with | true, n -> Some n | _ -> None)
                    | _ -> None
                | _ -> None
            | None -> None

        /// The named string field, falling back to the given default when absent.
        member this.StringOr(name: string, fallback: string) : string =
            this.TryString name |> Option.defaultValue fallback

        /// The named string field; when the input was a BARE string (not a JSON object), the
        /// raw input itself. Lets single-parameter tools accept either `{"path":"x"}` or "x".
        member this.StringOrRaw(name: string) : string =
            match this.TryString name with
            | Some s -> s
            | None -> match this.Obj with | Some _ -> "" | None -> this.Raw

    /// Parse a tool input string into a `ToolArgs`. A leading '{' is treated as a JSON
    /// object; anything else is kept as a bare value (`Raw`) for single-parameter tools.
    let parseArgs (input: string) : ToolArgs =
        let raw = if isNull input then "" else input.Trim()
        let obj =
            if raw.StartsWith("{") then
                try
                    use doc = JsonDocument.Parse(raw)
                    if doc.RootElement.ValueKind = JsonValueKind.Object then Some(doc.RootElement.Clone()) else None
                with _ -> None
            else None
        { Obj = obj; Raw = raw }


    /// Ensure and return the shared fallback workspace directory.
    let ensureWorkDir () =
        Directory.CreateDirectory(globalWorkDir) |> ignore
        globalWorkDir
