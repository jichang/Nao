namespace Nao.Providers

/// Configuration for llama.cpp server
type LlamaCppConfig =
    { BaseUrl: string
      Model: string
      NPredict: int option }

    static member Default =
        { BaseUrl = "http://localhost:8080"
          Model = "default"
          NPredict = None }
