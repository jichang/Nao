namespace Nao.Providers

/// Configuration for llama.cpp server
type LlamaCppConfig =
    { BaseUrl: string
      Model: string
      NPredict: int option
      /// Request timeout in seconds. None uses the HttpClient default.
      TimeoutSeconds: int option }

    static member Default =
        { BaseUrl = "http://localhost:8080/v1/chat/completions"
          Model = "default"
          NPredict = None
          TimeoutSeconds = None }
