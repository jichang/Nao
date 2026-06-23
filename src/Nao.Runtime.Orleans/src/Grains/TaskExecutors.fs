namespace Nao.Runtime.Orleans.Grains

open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks
open Orleans
open Nao.Agents
open Nao.Loader
open Nao.Runtime.Orleans

/// Built-in executor for "agent" tasks: drives a dedicated sub-session conversation.
///
/// The sub-session is a real `ISessionGrain` (key = the task's "userId/sessionId/taskId"),
/// so it gets the full harness, transcript, and history machinery. It inherits the parent
/// session's workspace, tools, and runtime, and is marked `Kind = "task"` so it runs the
/// (otherwise async) agent inline instead of recursively spawning another task.
type AgentTaskExecutor() =

    interface ITaskExecutor with
        member _.Kind = "agent"

        member _.ExecuteAsync(ctx: TaskExecutionContext) : Task<TaskOutcome> =
            task {
                let p =
                    match JsonSerializer.Deserialize<Dictionary<string, string>>(ctx.ParamsJson) with
                    | null -> Dictionary<string, string>()
                    | d -> d
                let agentName = match p.TryGetValue "agent" with | true, v -> v | _ -> ""
                let input = match p.TryGetValue "input" with | true, v -> v | _ -> ""

                let parent = ctx.GrainFactory.GetGrain<ISessionGrain>(ctx.ParentKey)
                let! parentInfo = parent.GetInfoAsync()

                let sub = ctx.GrainFactory.GetGrain<ISessionGrain>(ctx.SubSessionKey)
                let opts = SessionStartOptions()
                opts.AgentName <- agentName
                opts.WorkspaceKey <- parentInfo.WorkspaceKey
                opts.ToolNames <- ResizeArray(parentInfo.ToolNames)
                opts.RuntimeMode <- parentInfo.RuntimeMode
                opts.Kind <- "task"
                opts.ParentKey <- ctx.ParentKey

                ctx.Report 0.1 (sprintf "Starting %s" agentName)
                let! started = sub.StartAsync(opts)
                if not started then
                    return { Summary = sprintf "[Error] Could not start sub-session for agent '%s'" agentName
                             ResultFileIds = [] }
                else
                    ctx.Report 0.4 "Working"
                    let! answer = sub.ProcessAsync(input)
                    ctx.Report 1.0 "Completed"
                    return { Summary = answer; ResultFileIds = [] }
            }

/// Built-in executor for "tool" tasks: runs an asynchronous executable tool in the
/// background.
///
/// An async executable tool (declared `is_async`) returns a tracking token immediately
/// when invoked inside a harness; the real work runs here on the task's own grain. The
/// tool is resolved from the parent session's workspace and executed inline (the inline
/// builder ignores the async flag) so it does not recursively spawn another task.
type ToolTaskExecutor(registry: IWorkspaceRegistry) =

    interface ITaskExecutor with
        member _.Kind = "tool"

        member _.ExecuteAsync(ctx: TaskExecutionContext) : Task<TaskOutcome> =
            task {
                let p =
                    match JsonSerializer.Deserialize<Dictionary<string, string>>(ctx.ParamsJson) with
                    | null -> Dictionary<string, string>()
                    | d -> d
                let toolName = match p.TryGetValue "tool" with | true, v -> v | _ -> ""
                let input = match p.TryGetValue "input" with | true, v -> v | _ -> ""

                let parent = ctx.GrainFactory.GetGrain<ISessionGrain>(ctx.ParentKey)
                let! parentInfo = parent.GetInfoAsync()
                let policy = RuntimePolicy.parse parentInfo.RuntimeMode

                match registry.TryGet(WorkspaceId.create parentInfo.WorkspaceKey) with
                | None ->
                    return { Summary = sprintf "[Error] Workspace '%s' not available for tool '%s'" parentInfo.WorkspaceKey toolName
                             ResultFileIds = [] }
                | Some workspace ->
                    let (n, ver) = VersionRef.parse toolName
                    match workspace.ToolDefs |> List.tryFind (fun d -> d.Name = n && VersionRef.matches ver d.Version) with
                    | None ->
                        return { Summary = sprintf "[Error] Async tool '%s' not found" toolName; ResultFileIds = [] }
                    | Some def ->
                        let tool = DefinitionBuilder.buildToolWith policy def
                        ctx.Report 0.1 (sprintf "Running %s" toolName)
                        let! output = tool.Execute input
                        ctx.Report 1.0 "Completed"
                        return { Summary = output; ResultFileIds = [] }
            }