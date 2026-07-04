namespace Nao.Assistant.Evaluation

open System
open System.IO
open System.Net.Http
open System.Net.Http.Json
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Assistant
open Nao.Eval

module Config =

    type RunConfig =
        { ProviderType: string
          Endpoint: string
          Model: string
          OutputDir: string
          WorkspaceRoot: string
          DataDir: string }

    let private env name fallback =
        Environment.GetEnvironmentVariable(name)
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue fallback

    let load () =
        let runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
        let outputDir =
            env "NAO_EVAL_OUTPUT_DIR" (Path.Combine(Environment.CurrentDirectory, "artifacts", "assistant-evaluation", runId))
            |> Path.GetFullPath

        { ProviderType = env "NAO_LLM_PROVIDER" "Ollama"
          Endpoint = env "NAO_LLM_ENDPOINT" "http://localhost:11434"
          Model = env "NAO_LLM_MODEL" "qwen2.5:3b"
          OutputDir = outputDir
          WorkspaceRoot = env "NAO_WORKSPACE" (Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".nao", "..")))
          DataDir = Path.Combine(outputDir, "server-data") }

module ProviderProbe =

    let ollamaModelAvailable (endpoint: string) (model: string) =
        task {
            try
                use client = new HttpClient()
                let! response = client.GetAsync(sprintf "%s/api/tags" endpoint)
                if not response.IsSuccessStatusCode then
                    return false, sprintf "Ollama endpoint is not available at %s." endpoint
                else
                    let! body = response.Content.ReadAsStringAsync()
                    if body.Contains(model, StringComparison.OrdinalIgnoreCase) then
                        return true, ""
                    else
                        return false, sprintf "Ollama model '%s' is not available at %s. Run scripts/start-local-llm.sh %s first." model endpoint model
            with ex ->
                return false, sprintf "Ollama endpoint check failed: %s" ex.Message
        }

module ServerClient =

    let private jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    type ServerSession(baseUrl: string) =
        let http = new HttpClient(BaseAddress = Uri(baseUrl))

        let wsUrl (sessionId: string) =
            let uri = Uri(baseUrl)
            let scheme = if uri.Scheme = "https" then "wss" else "ws"
            sprintf "%s://%s:%d/ws/sessions/%s" scheme uri.Host uri.Port sessionId

        let ensureSuccess (resp: HttpResponseMessage) = task {
            if not resp.IsSuccessStatusCode then
                let! body = resp.Content.ReadAsStringAsync()
                failwithf "Server API error (%d): %s" (int resp.StatusCode) body
        }

        let sendWs (socket: ClientWebSocket) (msg: WsRequest) = task {
            let json = JsonSerializer.Serialize(msg, jsonOptions)
            let bytes = Encoding.UTF8.GetBytes(json)
            do! socket.SendAsync(ArraySegment(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
        }

        let receiveWs (socket: ClientWebSocket) = task {
            let buffer = Array.zeroCreate<byte> 8192
            let segments = ResizeArray<byte>()
            let mutable endOfMessage = false
            while not endOfMessage do
                let! result = socket.ReceiveAsync(ArraySegment(buffer), CancellationToken.None)
                if result.MessageType = WebSocketMessageType.Close then
                    endOfMessage <- true
                    failwith "Server closed the WebSocket before completing the turn."
                else
                    segments.AddRange(buffer.[0..result.Count - 1])
                    endOfMessage <- result.EndOfMessage

            let json = Encoding.UTF8.GetString(segments.ToArray())
            return JsonSerializer.Deserialize<WsResponse>(json, jsonOptions)
        }

        member _.CreateDocumentSessionAsync() = task {
            let request =
                { SessionStartRequest.Default with
                    AgentName = "document"
                    ToolNames = [ "convert_document" ] }

            let! resp = http.PostAsJsonAsync("/api/sessions", request, jsonOptions)
            do! ensureSuccess resp
            let! body = resp.Content.ReadFromJsonAsync<JsonElement>(jsonOptions)
            return body.GetProperty("sessionId").GetString()
        }

        member _.ChatAsync(sessionId: string, text: string, attachments: (string * string) list) = task {
            use socket = new ClientWebSocket()
            do! socket.ConnectAsync(Uri(wsUrl sessionId), CancellationToken.None)

            let request =
                { Text = text
                  Attachments =
                    attachments
                    |> List.map (fun (name, content) -> { AttachmentDto.Name = name; Content = content })
                    |> List.toArray }

            let payload = JsonSerializer.Serialize(request, jsonOptions)
            do! sendWs socket { Type = WsRequestType.Chat; Payload = payload }

            let mutable finished = false
            let mutable response = ""
            while not finished do
                let! frame = receiveWs socket
                match frame.Type with
                | WsResponseType.Done ->
                    response <- frame.Payload
                    finished <- true
                | WsResponseType.Error -> failwith frame.Payload
                | WsResponseType.PermissionRequest ->
                    let permission = JsonSerializer.Deserialize<PermissionRequestDto>(frame.Payload, jsonOptions)
                    let reply = { PermissionResponseDto.RequestId = permission.RequestId; Decision = "allow"; Scope = "session" }
                    let replyPayload = JsonSerializer.Serialize(reply, jsonOptions)
                    do! sendWs socket { Type = WsRequestType.PermissionResponse; Payload = replyPayload }
                | _ -> ()

            return response
        }

        member _.ListFilesAsync(sessionId: string) = task {
            let! resp = http.GetAsync(sprintf "/api/sessions/files/%s" sessionId)
            do! ensureSuccess resp
            let! files = resp.Content.ReadFromJsonAsync<SessionFileDto[]>(jsonOptions)
            return if isNull (box files) then [] else files |> Array.toList
        }

        interface IDisposable with
            member _.Dispose() = http.Dispose()

type ServerEvaluationAgent(baseUrl: string, attachments: (string * string) list) =
    let id = { Name = "nao-server-document-session"; Description = "Nao.Server document session over HTTP/WebSocket" }

    interface IAgent with
        member _.Id = id

        member _.RunAsync(input: string) =
            task {
                use session = new ServerClient.ServerSession(baseUrl)
                let! sessionId = session.CreateDocumentSessionAsync()
                let! response = session.ChatAsync(sessionId, input, attachments)
                let! files = session.ListFilesAsync(sessionId)

                let fileLines =
                    files
                    |> List.map (fun file -> sprintf "file|%s|%s|%s|%s|%d" file.Source file.Name file.DisplayName file.MediaType file.Size)
                    |> String.concat "\n"

                return sprintf "server-session:\n%s\n\nserver-response:\n%s\n\nsession-files:\n%s" sessionId response fileLines
            }

        member _.HandleMessageAsync(_msg: AgentMessage) = Task.FromResult(None)

type MarkdownConversionEvaluator() =
    interface IEvaluator with
        member _.Name = "nao-server-markdown-conversion-files"

        member _.EvaluateAsync(_case: EvalCase) (actualOutput: string) =
            let lines =
                actualOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun line -> line.Trim())

            let hasGeneratedFileWithExtension (extension: string) =
                lines
                |> Array.exists (fun line ->
                    line.StartsWith("file|generated|", StringComparison.OrdinalIgnoreCase)
                    && line.Contains(extension, StringComparison.OrdinalIgnoreCase))

            let hasHtml = hasGeneratedFileWithExtension ".html"
            let hasPdf = hasGeneratedFileWithExtension ".pdf"
            let responseMentionsBoth =
                actualOutput.Contains("html", StringComparison.OrdinalIgnoreCase)
                && actualOutput.Contains("pdf", StringComparison.OrdinalIgnoreCase)

            let verdict, reason =
                match hasHtml, hasPdf, responseMentionsBoth with
                | true, true, true -> EvalVerdict.Pass, "Nao.Server generated both HTML and PDF files and reported both targets."
                | true, true, false -> EvalVerdict.Partial 0.8, "Nao.Server generated both files, but the assistant response did not clearly mention both targets."
                | _ -> EvalVerdict.Fail, sprintf "Expected generated HTML and PDF files. hasHtml=%b hasPdf=%b" hasHtml hasPdf

            Task.FromResult(verdict, reason)

module ReportWriter =

    type ReportFile =
        { Source: string
          Name: string
          DisplayName: string
          MediaType: string
          Size: int64 }

    let private resultStatus (result: EvalResult) =
        match result.Verdict with
        | EvalVerdict.Pass -> "pass"
        | EvalVerdict.Fail -> "fail"
        | EvalVerdict.Partial score -> sprintf "partial %.2f" score

    let private filesFromOutput (actualOutput: string) =
        actualOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            let parts = line.Trim().Split('|')
            if parts.Length = 6 && String.Equals(parts.[0], "file", StringComparison.OrdinalIgnoreCase) then
                let mutable size = 0L
                Int64.TryParse(parts.[5], &size) |> ignore
                Some
                    { Source = parts.[1]
                      Name = parts.[2]
                      DisplayName = parts.[3]
                      MediaType = parts.[4]
                      Size = size }
            else None)
        |> Array.toList

    let private generatedFiles result =
        filesFromOutput result.ActualOutput
        |> List.filter (fun file -> String.Equals(file.Source, "generated", StringComparison.OrdinalIgnoreCase))

    let private sessionIdFromOutput (actualOutput: string) =
        let marker = "server-session:\n"
        let responseMarker = "\n\nserver-response:"
        if actualOutput.StartsWith(marker, StringComparison.Ordinal) then
            let start = marker.Length
            let stop = actualOutput.IndexOf(responseMarker, StringComparison.Ordinal)
            if stop >= start then actualOutput.Substring(start, stop - start).Trim()
            else actualOutput.Substring(start).Trim()
        else ""

    let private sessionDir (dataDir: string) (sessionId: string) =
        let sanitize (s: string) =
            (if isNull s then "" else s)
            |> String.map (fun c -> if Char.IsLetterOrDigit c || c = '-' || c = '_' then c else '_')

        let segments =
            (if isNull sessionId then "" else sessionId).Split('/')
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.map sanitize

        let sessionsRoot = Path.Combine(dataDir, "sessions")
        match segments with
        | [||] -> sessionsRoot
        | segs ->
            let baseName = if segs.Length = 1 then segs.[0] else segs.[0] + "_" + segs.[1]
            let mutable dir = Path.Combine(sessionsRoot, baseName)
            for i in 2 .. segs.Length - 1 do
                dir <- Path.Combine(dir, "tasks", segs.[i], "sessions", segs.[i])
            dir

    let private filePathFor (config: Config.RunConfig) (result: EvalResult) (file: ReportFile) =
        let sessionId = sessionIdFromOutput result.ActualOutput
        if String.IsNullOrWhiteSpace sessionId then ""
        else Path.Combine(sessionDir config.DataDir sessionId, "files", file.Name).Replace('\\', '/')

    let private serverResponseFromOutput (actualOutput: string) =
        let marker = "server-response:\n"
        let filesMarker = "\n\nsession-files:"
        let markerIndex = actualOutput.IndexOf(marker, StringComparison.Ordinal)
        if markerIndex >= 0 then
            let start = markerIndex + marker.Length
            let stop = actualOutput.IndexOf(filesMarker, StringComparison.Ordinal)
            if stop >= start then actualOutput.Substring(start, stop - start).Trim()
            else actualOutput.Substring(start).Trim()
        else actualOutput.Trim()

    let private generatedFileRows (config: Config.RunConfig) (report: EvalReport) =
        report.Results
        |> List.collect (fun result ->
            generatedFiles result
            |> List.map (fun file ->
                sprintf "| `%s` | `%s` | `%s` | `%s` | %d | `%s` |" result.CaseId file.Name file.DisplayName file.MediaType file.Size (filePathFor config result file)))
        |> function
            | [] -> "| _none_ |  |  |  |  |  |"
            | rows -> String.concat "\n" rows

    let private serverResponseSections (report: EvalReport) =
        report.Results
        |> List.map (fun result ->
            [ sprintf "### `%s`" result.CaseId
              ""
              "````text"
              serverResponseFromOutput result.ActualOutput
              "````" ]
            |> String.concat "\n")
        |> String.concat "\n\n"

    let write (outputDir: string) (config: Config.RunConfig) (report: EvalReport) =
        Directory.CreateDirectory outputDir |> ignore

        let textPath = Path.Combine(outputDir, "report.md")
        let jsonPath = Path.Combine(outputDir, "report.json")

        let details =
            report.Results
            |> List.map (fun result ->
                sprintf "| `%s` | %s | %.2f | %d | %s |" result.CaseId (resultStatus result) (EvalResult.score result) result.LatencyMs (result.Reason.Replace("|", "\\|")))
            |> String.concat "\n"

        let markdown =
            [ sprintf "# %s" report.Name
              ""
              sprintf "Run at: `%s`" (report.RunAt.ToString("o"))
              sprintf "Provider: `%s` `%s` at `%s`" config.ProviderType config.Model config.Endpoint
              "Target: `Nao.Server` embedded HTTP/WebSocket session API"
              sprintf "Server data: `%s`" (config.DataDir.Replace('\\', '/'))
              ""
              sprintf "Total: `%d`, Passed: `%d`, Failed: `%d`, Partial: `%d`, Average score: `%.2f`, Average latency: `%.0fms`" report.TotalCases report.Passed report.Failed report.Partial report.AverageScore report.AverageLatencyMs
              ""
              "| Case | Verdict | Score | Latency ms | Reason |"
              "| --- | --- | ---: | ---: | --- |"
              details
              ""
              "## Generated Files"
              ""
              "| Case | Name | Display name | Media type | Size bytes | Path |"
              "| --- | --- | --- | --- | ---: | --- |"
              generatedFileRows config report
              ""
              "## Server Agent Output"
              ""
              serverResponseSections report
              ""
              "## Raw Eval Output"
              ""
              "```text"
              EvalReport.format report
              "```" ]
            |> String.concat "\n"

        File.WriteAllText(textPath, markdown)

        let jsonRows =
            report.Results
            |> List.map (fun result ->
                let generatedFilesJson =
                    generatedFiles result
                    |> List.map (fun file ->
                        {| source = file.Source
                           name = file.Name
                           displayName = file.DisplayName
                           mediaType = file.MediaType
                           size = file.Size
                           path = filePathFor config result file |})

                {| caseId = result.CaseId
                   serverSession = sessionIdFromOutput result.ActualOutput
                   verdict = resultStatus result
                   score = EvalResult.score result
                   latencyMs = result.LatencyMs
                   reason = result.Reason
                   serverResponse = serverResponseFromOutput result.ActualOutput
                   generatedFiles = generatedFilesJson
                   actualOutput = result.ActualOutput |})

        let json =
            JsonSerializer.Serialize(
                {| name = report.Name
                   runAt = report.RunAt
                   target = "Nao.Server"
                   provider = {| providerType = config.ProviderType; endpoint = config.Endpoint; model = config.Model |}
                   serverDataDir = config.DataDir
                   totalCases = report.TotalCases
                   passed = report.Passed
                   failed = report.Failed
                   partial = report.Partial
                   averageScore = report.AverageScore
                   averageLatencyMs = report.AverageLatencyMs
                   results = jsonRows |},
                JsonSerializerOptions(WriteIndented = true))

        File.WriteAllText(jsonPath, json)
        textPath, jsonPath

module Evaluation =

    let markdownCase =
        EvalCase.openEnded
            "server-md-to-html-and-pdf"
            "Nao.Server should understand a request with an attached markdown file and create both HTML and PDF outputs."
            "Convert this markdown file to HTML file and PDF file"
        |> EvalCase.withTags [ "server"; "documents"; "markdown"; "conversion" ]
        |> EvalCase.withMetadata (Map.ofList [ "attachment", "sample.md"; "expectedOutputs", "html,pdf" ])

    let run (baseUrl: string) =
        let attachments =
            [ "sample.md", "# Quarterly Notes\n\n- Revenue increased\n- Margin improved\n\nPlease preserve this list." ]
        let dataset = EvalDataset.create "Nao.Server document conversion" [ markdownCase ]
        let agent = ServerEvaluationAgent(baseUrl, attachments) :> IAgent
        let evaluator = MarkdownConversionEvaluator() :> IEvaluator
        EvalRunner.runDatasetAsync EvalRunnerConfig.Default evaluator agent dataset

module Program =

    [<EntryPoint>]
    let main _argv =
        let config = Config.load ()
        Directory.CreateDirectory config.OutputDir |> ignore
        Directory.CreateDirectory config.DataDir |> ignore
        Environment.SetEnvironmentVariable("NAO_DATA_DIR", config.DataDir)
        Environment.SetEnvironmentVariable("NAO_WORKSPACE", config.WorkspaceRoot)

        let providerAvailable, providerReason =
            if String.Equals(config.ProviderType, "Ollama", StringComparison.OrdinalIgnoreCase) then
                (ProviderProbe.ollamaModelAvailable config.Endpoint config.Model).GetAwaiter().GetResult()
            else true, ""

        if not providerAvailable then
            let report = EvalReport.fromResults "Nao.Server document conversion" []
            let textPath, jsonPath = ReportWriter.write config.OutputDir config report
            File.AppendAllText(textPath, sprintf "\n\n## Skipped\n\n%s\n" providerReason)
            printfn "Evaluation skipped: %s" providerReason
            printfn "Report: %s" textPath
            printfn "JSON: %s" jsonPath
            printfn "Server data: %s" config.DataDir
            2
        else
            let settings =
                { AppSettings.Default with
                    Provider =
                        { AppSettings.Default.Provider with
                            ProviderType = config.ProviderType
                            Endpoint = config.Endpoint
                            Model = config.Model } }

            let baseUrl = EmbeddedServer.start settings
            let report = (Evaluation.run baseUrl).GetAwaiter().GetResult()
            let textPath, jsonPath = ReportWriter.write config.OutputDir config report

            printfn "%s" (EvalReport.format report)
            printfn ""
            printfn "Report: %s" textPath
            printfn "JSON: %s" jsonPath
            printfn "Server data: %s" config.DataDir

            if report.Failed = 0 then 0 else 1