namespace Nao.Loader

open System
open System.Diagnostics
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open Nao.Agents
open Nao.Core
open Nao.Eval

/// Extension point for custom tool execution strategies (gRPC, MCP, etc.)
type IToolExecutor =
    /// Execute a tool with the given input and config, return (success, output)
    abstract member ExecuteAsync: input: string * config: Map<string, string> -> Task<Result<string, string>>

/// Builds runnable domain objects from parsed definitions
module DefinitionBuilder =

    /// Registry of custom tool executors (populated at startup by consumers)
    let private executors = System.Collections.Concurrent.ConcurrentDictionary<string, IToolExecutor>()

    /// Register a custom tool executor by name
    let registerExecutor (name: string) (executor: IToolExecutor) =
        executors.[name] <- executor

    /// Remove a custom tool executor
    let removeExecutor (name: string) =
        executors.TryRemove(name) |> ignore

    /// Run a process with arguments and return (exitCode, stdout)
    let private runProcess (cmd: string) (args: string list) : Task<int * string> =
        task {
            let psi = ProcessStartInfo(cmd)
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            for arg in args do
                psi.ArgumentList.Add(arg)
            use proc = Process.Start(psi)
            let! output = proc.StandardOutput.ReadToEndAsync()
            do! proc.WaitForExitAsync()
            return (proc.ExitCode, output.TrimEnd())
        }

    /// Compute the actual launcher command and leading arguments for a process tool,
    /// applying the tool's declared runtime and the session runtime policy.
    ///
    /// The resolution order is:
    ///   1. A session `ForceRuntime` overrides the tool's declared runtime.
    ///   2. The (effective) runtime wraps the raw command, e.g.
    ///      deno -> `deno run -A <cmd>`; native -> `<cmd>` unchanged.
    ///   3. A `Containerized` policy wraps the whole thing in
    ///      `docker run --rm -v <pwd>:/work -w /work <image> <pieces...>`.
    ///
    /// Returns (launcherExecutable, leadingArgs) — the tool's own fixed args and the
    /// per-call input are appended to leadingArgs by the caller.
    let resolveProcessLauncher (policy: RuntimePolicy) (toolRuntime: string) (cmd: string) : string * string list =
        // Session ForceRuntime overrides the tool's own declared runtime.
        let runtimeName =
            match policy with
            | ForceRuntime r -> r
            | _ -> toolRuntime
        let runtime = ToolRuntime.resolve runtimeName
        // Host pieces: [launcher; launcherArgs...; cmd]  (or just [cmd] when native).
        let hostPieces =
            if String.IsNullOrWhiteSpace runtime.Command then [ cmd ]
            else runtime.Command :: (runtime.Args @ [ cmd ])
        match policy with
        | Containerized imageOpt ->
            let image =
                imageOpt
                |> Option.orElse runtime.DefaultImage
                |> Option.defaultValue "alpine:latest"
            // Mount the working directory so script paths remain accessible inside the
            // container, and run there.
            let dockerArgs =
                [ "run"; "--rm"; "-v"; "${PWD}:/work"; "-w"; "/work"; image ] @ hostPieces
            ("docker", dockerArgs)
        | _ ->
            match hostPieces with
            | head :: tail -> (head, tail)
            | [] -> (cmd, [])

    /// Execute an HTTP call and return the response body
    let private runHttp (url: string) (httpMethod: string) (headers: Map<string, string>) (input: string) : Task<Result<string, string>> =
        task {
            use client = new HttpClient()
            for kv in headers do
                client.DefaultRequestHeaders.TryAddWithoutValidation(kv.Key, kv.Value) |> ignore
            let! response =
                match httpMethod.ToUpperInvariant() with
                | "GET" -> client.GetAsync(url + "?input=" + Uri.EscapeDataString(input))
                | "PUT" -> client.PutAsync(url, new StringContent(input, Text.Encoding.UTF8, "application/json"))
                | "DELETE" -> client.DeleteAsync(url + "?input=" + Uri.EscapeDataString(input))
                | _ -> client.PostAsync(url, new StringContent(input, Text.Encoding.UTF8, "application/json"))
            let! body = response.Content.ReadAsStringAsync()
            if response.IsSuccessStatusCode then
                return Ok body
            else
                return Error (sprintf "HTTP %d: %s" (int response.StatusCode) body)
        }

    /// Execute a ToolExecutionDef with the given arguments, honouring the runtime
    /// policy and the tool's declared runtime for process executions.
    let private executeDefAsync (policy: RuntimePolicy) (toolRuntime: string) (exec: ToolExecutionDef) (args: string list) : Task<Result<string, string>> =
        task {
            match exec with
            | ToolExecutionDef.Process (cmd, fixedArgs) ->
                let (launcher, leadingArgs) = resolveProcessLauncher policy toolRuntime cmd
                let! (exitCode, output) = runProcess launcher (leadingArgs @ fixedArgs @ args)
                if exitCode = 0 then return Ok output
                else return Error (sprintf "Process exited with code %d: %s" exitCode output)
            | ToolExecutionDef.Http (url, httpMethod, headers) ->
                let input = args |> String.concat " "
                return! runHttp url httpMethod headers input
            | ToolExecutionDef.Custom (executorName, config) ->
                match executors.TryGetValue(executorName) with
                | true, executor ->
                    let input = args |> String.concat " "
                    return! executor.ExecuteAsync(input, config)
                | false, _ ->
                    return Error (sprintf "Custom executor '%s' not registered" executorName)
        }

    /// Build a Tool from a ToolDef, applying the given session runtime policy.
    /// Only executable tools are built here; prompt tools require an LLM provider and must
    /// be built via `buildPromptTool`. Async executable tools are built inline (the async
    /// wrapping is layered on separately by `wrapAsyncTool`).
    let buildToolWith (policy: RuntimePolicy) (def: ToolDef) : Tool =
        match def.Kind with
        | PromptTool _ ->
            failwithf
                "Tool '%s' is prompt-defined and must be built with an LLM provider (DefinitionBuilder.buildPromptTool); it cannot be built as an executable tool."
                def.Name
        | ExecutableTool (execution, runtime, _isAsync) ->
            let verify =
                match def.VerifyExecution with
                | None -> None
                | Some verifyExec ->
                    Some (fun (input: string) (output: string) ->
                        task {
                            let! result = executeDefAsync policy runtime verifyExec [input; output]
                            return result |> Result.map ignore
                        })
            let revert =
                match def.RevertExecution with
                | None -> None
                | Some revertExec ->
                    Some (fun (ctx: RevertContext) ->
                        task {
                            let! result = executeDefAsync policy runtime revertExec [ctx.Input; ctx.Output]
                            return result |> Result.map ignore
                        })
            let contentType =
                if String.IsNullOrEmpty(def.OutputContentType) then ContentMeta.Text
                else ContentMeta.Of def.OutputContentType
            { Name = def.Name
              Description = def.Description
              Version = def.Version
              Execute = fun input -> task {
                let! result = executeDefAsync policy runtime execution [input]
                return
                    match result with
                    | Ok output -> output
                    | Error err -> sprintf "[Error] %s" err
              }
              OutputContentType = contentType
              Verify = verify
              Revert = revert
              Provenance = def.Provenance }

    /// Build a prompt-defined Tool: invoking the tool completes its prompt against the
    /// given LLM provider. The caller is responsible for any conversation/memory context
    /// (the runtime prepends it to the input before invoking the tool).
    let buildPromptTool (provider: ILlmProvider) (def: ToolDef) (prompt: Prompt) : Tool =
        let contentType =
            if String.IsNullOrEmpty(def.OutputContentType) then ContentMeta.Text
            else ContentMeta.Of def.OutputContentType
        let execute (input: string) : Task<string> =
            task {
                let conversation : Conversation =
                    [ { Role = Role.System; Content = Prompt.render prompt }
                      { Role = Role.User; Content = input } ]
                let! result = provider.CompleteAsync conversation CompletionOptions.Default
                return result.Content
            }
        { Name = def.Name
          Description = def.Description
          Version = def.Version
          Execute = execute
          OutputContentType = contentType
          Verify = None
          Revert = None
          Provenance = def.Provenance }

    /// Layer asynchronous execution onto an already-built (inline) executable tool. When a
    /// session task host is available on the ambient `SessionExecution` scope, invoking the
    /// tool spawns a background "tool" task and immediately returns a tracking token instead
    /// of blocking; otherwise it falls back to running the tool inline.
    let wrapAsyncTool (def: ToolDef) (inlineTool: Tool) : Tool =
        let title = sprintf "Async tool: %s" def.Name
        let execute (input: string) : Task<string> =
            task {
                match SessionExecution.current () with
                | Some scope ->
                    let! taskId =
                        scope.SpawnTask
                            { Kind = "tool"
                              Title = title
                              Params = Map [ "tool", def.Name; "input", input ] }
                    if String.IsNullOrEmpty taskId then
                        // No async task host available — run inline.
                        return! inlineTool.Execute input
                    else
                        return
                            sprintf
                                "Started background task **%s** (`%s`). Track its progress or open the result from the task tag."
                                title taskId
                | None ->
                    return! inlineTool.Execute input
            }
        { inlineTool with Execute = execute }

    /// Build a Tool from a ToolDef using the host-default runtime policy
    /// (each tool uses its own declared runtime, executed on the host).
    let buildTool (def: ToolDef) : Tool =
        buildToolWith RuntimePolicy.HostDefault def

    /// Build an OrchestratorConfig from an AgentDef
    let buildOrchestratorConfig
        (provider: ILlmProvider)
        (tools: Tool list)
        (subAgents: IAgent list)
        (def: AgentDef)
        : OrchestratorConfig =
        { Provider = provider
          Tools = tools
          SubAgents = subAgents
          Prompt = def.Prompt
          Options = def.Options
          MaxRounds = def.MaxRounds
          EventSink = AgentEventSink.none
          Memory = OrchestratorMemoryConfig.None
          Instructions = None }

    /// Build an IAgent from an AgentDef using the provided factory (or default Orchestrator).
    let buildAgent
        (provider: ILlmProvider)
        (tools: Tool list)
        (subAgents: IAgent list)
        (def: AgentDef)
        : IAgent =
        let config = buildOrchestratorConfig provider tools subAgents def
        Orchestrator.createWithConfig config

    /// Build an IAgent from an AgentDef using a custom factory.
    let buildAgentWithFactory
        (factory: IOrchestratorFactory)
        (provider: ILlmProvider)
        (tools: Tool list)
        (subAgents: IAgent list)
        (eventSink: IAgentEventSink)
        (def: AgentDef)
        : IAgent =
        let config = { buildOrchestratorConfig provider tools subAgents def with EventSink = eventSink }
        factory.Create config

    /// Build an EvalDataset from an EvalSuiteDef
    let buildEvalDataset (def: EvalSuiteDef) : EvalDataset =
        { Name = def.Name
          Cases = def.Cases }

    /// Parse a category string into a RuleCategory
    let private parseRuleCategory (s: string) : RuleCategory =
        match s with
        | "Safety" -> RuleCategory.Safety
        | "Privacy" -> RuleCategory.Privacy
        | "Behavioral" -> RuleCategory.Behavioral
        | "Format" -> RuleCategory.Format
        | other ->
            if other.StartsWith("Domain:") then
                RuleCategory.Domain (other.Substring(7))
            else
                RuleCategory.Domain other

    /// Build a ConstitutionRule from a ConstitutionRuleDef.
    /// The Pattern field becomes a regex-based Check function.
    let buildConstitutionRule (def: ConstitutionRuleDef) : ConstitutionRule =
        let check =
            if System.String.IsNullOrWhiteSpace(def.Pattern) then
                fun _ -> false
            else
                let regex = Regex(def.Pattern, RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
                fun (content: string) -> regex.IsMatch(content)
        { Id = def.Id
          Description = def.Description
          Category = parseRuleCategory def.Category
          Priority = def.Priority
          IsHardConstraint = def.IsHardConstraint
          Check = check }

    /// Build a Constitution from a ConstitutionDef
    let buildConstitution (def: ConstitutionDef) : Constitution =
        { Name = def.Name
          Version = def.Version
          Rules = def.Rules |> List.map buildConstitutionRule |> List.sortByDescending (fun r -> r.Priority)
          Preamble = None }

    /// Build a merged Constitution from multiple ConstitutionDefs
    let buildMergedConstitution (defs: ConstitutionDef list) : Constitution option =
        match defs with
        | [] -> None
        | [ single ] -> Some (buildConstitution single)
        | multiple ->
            let allRules = multiple |> List.collect (fun d -> d.Rules) |> List.map buildConstitutionRule
            Some
                { Name = multiple |> List.map (fun d -> d.Name) |> String.concat "+"
                  Version = "merged"
                  Rules = allRules |> List.sortByDescending (fun r -> r.Priority)
                  Preamble = None }
