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

/// Check if an LLM provider endpoint is reachable
module ProviderHealth =

    let checkAsync (settings: ProviderSettings) : Task<Result<string, string>> =
        task {
            try
                use client = new System.Net.Http.HttpClient()
                client.Timeout <- TimeSpan.FromSeconds(5.0)
                match settings.ProviderType.ToLowerInvariant() with
                | "ollama" ->
                    let url = if String.IsNullOrWhiteSpace(settings.Endpoint) then "http://localhost:11434" else settings.Endpoint
                    let! resp = client.GetAsync(url + "/api/tags")
                    if resp.IsSuccessStatusCode then
                        return Ok (sprintf "Ollama is running at %s" url)
                    else
                        return Error (sprintf "Ollama returned %d at %s" (int resp.StatusCode) url)
                | "openai" ->
                    let url = if String.IsNullOrWhiteSpace(settings.Endpoint) then "https://api.openai.com/v1" else settings.Endpoint
                    let! resp = client.GetAsync(url + "/models")
                    if int resp.StatusCode < 500 then
                        return Ok (sprintf "OpenAI endpoint reachable at %s" url)
                    else
                        return Error (sprintf "OpenAI endpoint returned %d" (int resp.StatusCode))
                | "anthropic" ->
                    return Ok "Anthropic (API key validated at runtime)"
                | other ->
                    return Ok (sprintf "Provider '%s' — no health check available" other)
            with ex ->
                return Error (sprintf "Cannot reach provider: %s" ex.Message)
        }

