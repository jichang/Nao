namespace Nao.Assistant

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

[<CLIMutable>]
type OrchestratorSettings =
    { [<JsonPropertyName("maxRounds")>]
      MaxRounds: int
      [<JsonPropertyName("temperature")>]
      Temperature: float
      [<JsonPropertyName("systemPrompt")>]
      SystemPrompt: string
      [<JsonPropertyName("windowStrategy")>]
      WindowStrategy: string
      [<JsonPropertyName("windowSize")>]
      WindowSize: int
      [<JsonPropertyName("tools")>]
      Tools: string list }

    static member Default =
        { MaxRounds = 10
          Temperature = 0.1
          SystemPrompt = "You are Nao, a helpful assistant."
          WindowStrategy = "LastN"
          WindowSize = 20
          Tools = [] }

[<CLIMutable>]
type ProviderSettings =
    { [<JsonPropertyName("type")>]
      ProviderType: string
      [<JsonPropertyName("endpoint")>]
      Endpoint: string
      [<JsonPropertyName("model")>]
      Model: string }

    static member Default =
        { ProviderType = "Ollama"
          Endpoint = "http://localhost:11434"
          Model = "llama3.2" }

/// How the desktop app reaches the Nao runtime: the bundled local engine, or a remote
/// server cluster. Remote mode unlocks cluster-backed performance and hosted features, and
/// requires the user to sign in via a verification link sent to their email.
[<CLIMutable>]
type ServerSettings =
    { /// "Local" = bundled embedded server; "Remote" = hosted cluster.
      [<JsonPropertyName("mode")>]
      Mode: string
      /// Base URL of the remote server (used only in Remote mode).
      [<JsonPropertyName("remoteUrl")>]
      RemoteUrl: string
      /// Email the verification link is sent to.
      [<JsonPropertyName("authEmail")>]
      AuthEmail: string
      /// Session token obtained after the verification link is confirmed.
      /// Empty = signed out.
      [<JsonPropertyName("authToken")>]
      AuthToken: string }

    static member Default =
        { Mode = "Local"
          RemoteUrl = "https://cloud.nao.dev"
          AuthEmail = ""
          AuthToken = "" }

[<CLIMutable>]
type AppSettings =
    { [<JsonPropertyName("provider")>]
      Provider: ProviderSettings
      [<JsonPropertyName("orchestrator")>]
      Orchestrator: OrchestratorSettings
      [<JsonPropertyName("server")>]
      Server: ServerSettings
      [<JsonPropertyName("workspacePath")>]
      WorkspacePath: string
      [<JsonPropertyName("theme")>]
      Theme: string
      [<JsonPropertyName("language")>]
      Language: string }

    static member Default =
        { Provider = ProviderSettings.Default
          Orchestrator = OrchestratorSettings.Default
          Server = ServerSettings.Default
          WorkspacePath = ""
          Theme = "Dark"
          Language = "en" }

module AppSettingsStore =

    let private settingsDir =
        // Co-locate desktop settings with the rest of the app's data under .nao-data
        // (override with NAO_DATA_DIR), so everything lives in one inspectable, wipeable
        // root. Mirrors Database.dataDir without taking a dependency on the Server layer.
        match Environment.GetEnvironmentVariable("NAO_DATA_DIR") with
        | path when not (String.IsNullOrWhiteSpace path) -> path
        | _ -> Path.Combine(Environment.CurrentDirectory, ".nao-data")

    let private settingsPath = Path.Combine(settingsDir, "settings.json")

    let private jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = true)
        opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        opts

    let load () : AppSettings =
        if File.Exists(settingsPath) then
            try
                let json = File.ReadAllText(settingsPath)
                let loaded = JsonSerializer.Deserialize<AppSettings>(json, jsonOptions)
                // Settings files written before a field existed deserialize that field as
                // null. Backfill any missing nested record with its default so callers can
                // safely read e.g. Server.Mode.
                { loaded with
                    Provider = if obj.ReferenceEquals(loaded.Provider, null) then ProviderSettings.Default else loaded.Provider
                    Orchestrator = if obj.ReferenceEquals(loaded.Orchestrator, null) then OrchestratorSettings.Default else loaded.Orchestrator
                    Server = if obj.ReferenceEquals(loaded.Server, null) then ServerSettings.Default else loaded.Server }
            with _ ->
                AppSettings.Default
        else
            AppSettings.Default

    let save (settings: AppSettings) =
        Directory.CreateDirectory(settingsDir) |> ignore
        let json = JsonSerializer.Serialize(settings, jsonOptions)
        File.WriteAllText(settingsPath, json)

    /// Load orchestrator overrides from a workspace JSON file
    let loadWorkspaceOrchestrator (workspacePath: string) : OrchestratorSettings option =
        let configPath = Path.Combine(workspacePath, ".nao", "orchestrator.json")
        if File.Exists(configPath) then
            try
                let json = File.ReadAllText(configPath)
                Some (JsonSerializer.Deserialize<OrchestratorSettings>(json, jsonOptions))
            with _ -> None
        else
            None
