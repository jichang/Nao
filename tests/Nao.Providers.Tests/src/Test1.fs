namespace Nao.Providers.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Providers

[<TestClass>]
type ProviderFactoryTests () =

    [<TestMethod>]
    member _.CreatesOpenAIProvider () =
        let provider = ProviderFactory.create (OpenAI OpenAIConfig.Default)
        Assert.IsTrue(provider.Name.StartsWith "OpenAI")

    [<TestMethod>]
    member _.CreatesAnthropicProvider () =
        let provider = ProviderFactory.create (Anthropic AnthropicConfig.Default)
        Assert.AreEqual("Anthropic", provider.Name)

    [<TestMethod>]
    member _.CreatesVllmProvider () =
        let provider = ProviderFactory.create (Vllm VllmConfig.Default)
        Assert.IsTrue(provider.Name.StartsWith "vLLM")

    [<TestMethod>]
    member _.CreatesLlamaCppProvider () =
        let provider = ProviderFactory.create (LlamaCpp LlamaCppConfig.Default)
        Assert.IsTrue(provider.Name.StartsWith "llama.cpp")

    [<TestMethod>]
    member _.OpenAIProviderReturnsResultOnUnreachableServer () =
        // Against an unreachable endpoint the provider must return a graceful error
        // result rather than throwing.
        let provider = ProviderFactory.create (OpenAI { OpenAIConfig.Default with BaseUrl = "http://localhost:1" })
        let conversation = [ { Role = User; Content = "hi" } ]
        let result = (provider.CompleteAsync conversation CompletionOptions.Default).Result
        Assert.AreEqual("error", result.FinishReason)

[<TestClass>]
type OpenAIConfigTests () =

    [<TestMethod>]
    member _.DefaultHasExpectedValues () =
        let config = OpenAIConfig.Default
        Assert.AreEqual("gpt-4", config.Model)
        Assert.AreEqual("https://api.openai.com/v1", config.BaseUrl)

[<TestClass>]
type AnthropicConfigTests () =

    [<TestMethod>]
    member _.DefaultHasExpectedValues () =
        let config = AnthropicConfig.Default
        Assert.AreEqual("claude-sonnet-4-20250514", config.Model)

[<TestClass>]
type VllmConfigTests () =

    [<TestMethod>]
    member _.DefaultUsesLocalhost () =
        let config = VllmConfig.Default
        Assert.AreEqual("http://localhost:8000/v1", config.BaseUrl)
        Assert.AreEqual(None, config.ApiKey)

[<TestClass>]
type LlamaCppConfigTests () =

    [<TestMethod>]
    member _.DefaultUsesLocalhost () =
        let config = LlamaCppConfig.Default
        Assert.AreEqual("http://localhost:8080", config.BaseUrl)
        Assert.AreEqual(None, config.NPredict)
