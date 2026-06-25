namespace Nao.Assistant

open System
open System.Text
open System.Text.RegularExpressions
open Nao.Agents

/// Network tools: a raw HTTP request and a readable-text web fetch. Both share one pooled
/// HttpClient and bound their response size before returning it to the model.
module WebTools =

    /// Shared HTTP client for the web tools (connection pooling, sane timeout).
    let private httpClient =
        let c = new System.Net.Http.HttpClient()
        c.Timeout <- TimeSpan.FromSeconds(20.0)
        c.DefaultRequestHeaders.Add("User-Agent", "Nao-Assistant/1.0")
        c

    let private truncate (max: int) (s: string) =
        if s.Length > max then s.Substring(0, max) + "\n...(truncated)" else s

    let httpRequest: Tool =
        { Tool.Create("http_request",
            "Make an HTTP request to any URL. Input: JSON {\"method\":\"GET\",\"url\":\"https://...\",\"body\":\"...\"}. 'method' defaults to GET; 'body' is optional. Returns the status code and response body.",
            fun input -> task {
                try
                    let a = parseArgs input
                    let methodStr = (a.StringOr("method", "GET")).Trim().ToUpperInvariant()
                    let url = (a.StringOrRaw "url").Trim()
                    let body = a.TryString "body"
                    if String.IsNullOrWhiteSpace url then return json {| error = "Expected a 'url'." |}
                    else
                    use req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod(methodStr), url)
                    match body with
                    | Some b -> req.Content <- new System.Net.Http.StringContent(b, Encoding.UTF8, "application/json")
                    | None -> ()
                    let! resp = httpClient.SendAsync(req)
                    let! content = resp.Content.ReadAsStringAsync()
                    return json {| status = int resp.StatusCode; body = truncate 8000 content |}
                with ex ->
                    return json {| error = ex.Message |}
            }) with
            Schema =
                [ optParam "method" "string" (Some "GET") "HTTP method (GET, POST, etc.)."
                  reqParam "url" "string" "Absolute URL to request."
                  optParam "body" "string" None "Optional request body (sent as application/json)." ] }

    let webFetch: Tool =
        { Tool.Create("web_fetch",
            "Fetch a web page and return its readable text content (HTML tags stripped). Input: JSON {\"url\":\"https://...\"}.",
            fun input -> task {
                try
                    let a = parseArgs input
                    let url = (a.StringOrRaw "url").Trim()
                    let! html = httpClient.GetStringAsync(url)
                    let noScript = Regex.Replace(html, "(?is)<(script|style)[^>]*>.*?</\\1>", " ")
                    let noTags = Regex.Replace(noScript, "(?s)<[^>]+>", " ")
                    let decoded = System.Net.WebUtility.HtmlDecode(noTags)
                    let collapsed = Regex.Replace(decoded, "\\s+", " ").Trim()
                    return truncate 8000 collapsed
                with ex ->
                    return json {| error = ex.Message |}
            }) with
            Schema = [ reqParam "url" "string" "Absolute URL of the web page to fetch." ] }
