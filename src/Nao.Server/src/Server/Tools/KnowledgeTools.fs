namespace Nao.Assistant

open System
open Nao.Agents

/// The opt-in knowledge-base search tool. The knowledge base is NEVER injected into the
/// conversation automatically — the agent must call this tool to consult it.
module KnowledgeTools =

    /// Hook into the per-workspace knowledge base, set by the embedded server at startup.
    /// Given a query and a result count, returns up to that many (fileName, passage) matches
    /// from files the user explicitly uploaded.
    let mutable knowledgeSearch: (string -> int -> (string * string) list) option = None

    /// Search the user's uploaded knowledge base on demand. The base is not loaded into the
    /// conversation by default; the agent calls this tool (after asking the user) only when it
    /// genuinely needs information from files the user uploaded.
    let searchKnowledge: Tool =
        { Tool.Create("search_knowledge",
            "Search the user's knowledge base — documents the user explicitly uploaded — for passages relevant to a query, returning the top matches as { file, text } snippets. The knowledge base is NOT loaded automatically: only call this when you actually need information from the user's uploaded files, and ASK THE USER FOR PERMISSION before each search. Input: JSON {\"query\":\"...\",\"topK\":4} ('topK' optional, defaults to 4, max 10).",
            fun input -> task {
                match knowledgeSearch with
                | None -> return json {| error = "Knowledge base is not available." |}
                | Some search ->
                    let a = parseArgs input
                    let query = (a.StringOrRaw "query").Trim()
                    if String.IsNullOrWhiteSpace query then
                        return json {| error = "Expected a search query." |}
                    else
                        let topK =
                            match a.TryInt "topK" with
                            | Some n when n > 0 -> min n 10
                            | _ -> 4
                        let hits = search query topK
                        if List.isEmpty hits then
                            return json {| matches = ([||]: obj[]); note = "No relevant passages found in the knowledge base." |}
                        else
                            let matches =
                                hits
                                |> List.map (fun (f, t) -> {| file = f; text = t |})
                            return json {| matches = matches |}
            }) with
            Schema =
                [ reqParam "query" "string" "Text to search the user's uploaded knowledge base for."
                  optParam "topK" "int" (Some "4") "Maximum number of passages to return (default 4, max 10)." ] }
