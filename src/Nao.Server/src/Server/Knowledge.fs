namespace Nao.Assistant

open System
open System.IO
open System.Net.WebSockets
open System.Net.Sockets
open System.Data.Common
open System.Text
open System.Text.RegularExpressions
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Nao.Core
open Nao.Agents
open Nao.Loader
open Nao.Providers
open Nao.Persistence
open Nao.Feedback
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

/// Per-workspace knowledge base: uploaded text files chunked and embedded with the
/// dependency-free bag-of-words provider, used for similarity (RAG) retrieval.
module Knowledge =

    let dirFor (workspaceRoot: string) = Path.Combine(workspaceRoot, ".nao", "knowledge")

    /// Split text into ~1000-character chunks on blank-line boundaries.
    let chunk (text: string) : string list =
        let paras = text.Replace("\r\n", "\n").Split([| "\n\n" |], StringSplitOptions.RemoveEmptyEntries)
        let chunks = ResizeArray<string>()
        let sb = StringBuilder()
        for p in paras do
            if sb.Length > 0 && sb.Length + p.Length > 1000 then
                chunks.Add(sb.ToString().Trim())
                sb.Clear() |> ignore
            sb.Append(p).Append("\n\n") |> ignore
        if sb.Length > 0 then chunks.Add(sb.ToString().Trim())
        chunks |> List.ofSeq |> List.filter (fun c -> c.Length > 0)

    type private IndexedChunk = { File: string; Text: string; Embedding: float array }

    type KnowledgeStore(workspaceRoot: string) =
        let dir = dirFor workspaceRoot
        let provider = SimpleEmbeddingProvider() :> IEmbeddingProvider
        let gate = obj ()
        let mutable chunks : IndexedChunk list = []
        let mutable built = false

        let listFiles () =
            if Directory.Exists dir then Directory.GetFiles(dir) |> Array.toList else []

        let rebuild () =
            let cs = ResizeArray<IndexedChunk>()
            for f in listFiles () do
                try
                    let text = File.ReadAllText f
                    for ck in chunk text do
                        let emb = (provider.EmbedAsync ck).Result
                        cs.Add { File = Path.GetFileName f; Text = ck; Embedding = emb }
                with _ -> ()
            chunks <- List.ofSeq cs
            built <- true

        let ensureBuilt () = lock gate (fun () -> if not built then rebuild ())

        member _.Files () : KnowledgeFileDto list =
            ensureBuilt ()
            listFiles ()
            |> List.map (fun f ->
                let name = Path.GetFileName f
                let n = chunks |> List.filter (fun c -> c.File = name) |> List.length
                { Name = name; SizeBytes = FileInfo(f).Length; Chunks = n })

        member _.Save (name: string) (content: string) =
            Directory.CreateDirectory dir |> ignore
            File.WriteAllText(Path.Combine(dir, Path.GetFileName name), content)
            lock gate (fun () -> built <- false)

        member _.Delete (name: string) : bool =
            let path = Path.Combine(dir, Path.GetFileName name)
            let existed = File.Exists path
            if existed then File.Delete path
            lock gate (fun () -> built <- false)
            existed

        /// Retrieve up to topK relevant chunks for the query as (file, text) pairs.
        member _.Retrieve (query: string) (topK: int) : (string * string) list =
            ensureBuilt ()
            if List.isEmpty chunks then []
            else
                let q = (provider.EmbedAsync query).Result
                chunks
                |> List.map (fun c -> c, SemanticSimilarity.cosineSimilarity q c.Embedding)
                |> List.filter (fun (_, s) -> s > 0.0)
                |> List.sortByDescending snd
                |> List.truncate topK
                |> List.map (fun (c, _) -> c.File, c.Text)


