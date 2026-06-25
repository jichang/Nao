namespace Nao.Assistant

open System
open System.IO
open System.Text.RegularExpressions
open Nao.Agents

/// Workspace search tools: regex content search and glob-based file discovery, both rooted
/// in the current session's working directory.
module SearchTools =

    let searchFiles: Tool =
        { Tool.Create("search_files",
            "Search workspace files for a regex pattern. Input: JSON {\"pattern\":\"regex\",\"path\":\"subdir\"} ('path' optional, defaults to the workspace root). Returns up to 200 'relative/path:line: text' matches.",
            fun input -> task {
                try
                    let a = parseArgs input
                    let pattern = (a.StringOrRaw "pattern").Trim()
                    let sub = (a.StringOr("path", "")).Trim()
                    if String.IsNullOrWhiteSpace pattern then return json {| error = "Expected a 'pattern'." |}
                    else
                    let baseDir = currentWorkDir ()
                    let root = if String.IsNullOrWhiteSpace sub then baseDir else resolvePath sub
                    if not (Directory.Exists root) then return json {| error = sprintf "Directory not found: %s" sub |}
                    else
                        let regex = Regex(pattern, RegexOptions.IgnoreCase)
                        let matches = ResizeArray<string>()
                        for file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do
                            if matches.Count < 200 then
                                try
                                    let lines = File.ReadAllLines(file)
                                    lines |> Array.iteri (fun i line ->
                                        if matches.Count < 200 && regex.IsMatch(line) then
                                            let rel = Path.GetRelativePath(baseDir, file).Replace("\\", "/")
                                            matches.Add(sprintf "%s:%d: %s" rel (i + 1) (line.Trim())))
                                with _ -> ()
                        return (if matches.Count = 0 then "(no matches)" else String.Join("\n", matches))
                with ex ->
                    return json {| error = ex.Message |}
            }) with
            Schema =
                [ reqParam "pattern" "string" "Regular expression to search file contents for."
                  optParam "path" "string" None "Subdirectory to search within (defaults to the workspace root)." ] }

    let findFiles: Tool =
        { Tool.Create("find_files",
            "Find files in the workspace by glob pattern (supports *, ?, and ** for any depth). Input: JSON {\"glob\":\"**/*.json\"}.",
            fun input -> task {
                try
                    let a = parseArgs input
                    let glob = (a.StringOrRaw "glob").Trim().Replace("\\", "/").TrimStart('/')
                    let escaped =
                        Regex.Escape(glob)
                            .Replace("\\*\\*/", "(.*/)?")
                            .Replace("\\*\\*", ".*")
                            .Replace("\\*", "[^/]*")
                            .Replace("\\?", "[^/]")
                    let regex = Regex("^" + escaped + "$", RegexOptions.IgnoreCase)
                    let baseDir = currentWorkDir ()
                    if not (Directory.Exists baseDir) then return "(workspace empty)"
                    else
                        let results =
                            Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories)
                            |> Seq.map (fun f -> Path.GetRelativePath(baseDir, f).Replace("\\", "/"))
                            |> Seq.filter regex.IsMatch
                            |> Seq.truncate 500
                            |> List.ofSeq
                        return (if results.IsEmpty then "(no matches)" else String.Join("\n", results))
                with ex ->
                    return json {| error = ex.Message |}
            }) with
            Schema = [ reqParam "glob" "string" "Glob pattern to match file paths (supports *, ?, and ** for any depth)." ] }
