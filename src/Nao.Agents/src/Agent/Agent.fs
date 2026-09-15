namespace Nao.Agents

open System.Threading
open System.Threading.Tasks

/// Declares the transport representation accepted or returned by an agent.
/// Structured schemas are authored text; the runtime does not infer them from CLR types.
[<RequireQualifiedAccess>]
type AgentParameter =
    /// An unstructured text value.
    | Text
    /// A structured value described by the supplied schema.
    | Structured of schema: string

/// Explicit transport contract advertised by an agent.
type AgentContract =
    { Input: AgentParameter
      Output: AgentParameter }

[<RequireQualifiedAccess>]
module AgentContract =
    /// Contract for agents that accept and return plain text.
    let Text =
        { Input = AgentParameter.Text
          Output = AgentParameter.Text }

/// Metadata carried by an immutable functional agent program.
type AgentMetadata =
    { Id: string
      Name: string
      Description: string
      Priority: int
      Responsibilities: string list
      Contract: AgentContract }

/// Stable identity of an agent implementation hosted by an execution worker.
type WorkerAgentReference = private WorkerAgentReference of string

[<RequireQualifiedAccess>]
module WorkerAgentReference =
    let create value =
        if System.String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg (nameof value) "Worker agent identity must be non-blank with no surrounding whitespace."

        WorkerAgentReference value

    let value (WorkerAgentReference value) = value

/// Controls how an agent's input and successful output are transported across a worker boundary.
[<RequireQualifiedAccess>]
type WorkerPayloadProtection =
    | RedactedText
    | OpaqueUntrusted

/// Immutable executable agent capability represented entirely by data and functions.
type Agent =
    { Metadata: AgentMetadata
      WorkerReference: WorkerAgentReference option
      WorkerPayloadProtection: WorkerPayloadProtection
      Execute: AgentContext -> string -> Task<string> }

/// Fail-closed violation raised by a governed execution boundary.
exception GovernedExecutionException of message: string

/// Canonical agent and tool dispatch installed for the current asynchronous execution flow.
type ExecutionDispatcher =
    { RunAgent: AgentContext -> Agent -> string -> Task<Result<string, PlatformFailure>>
      RunTool: AgentContext -> IdempotencyKey -> Tool -> string -> Task<Result<string, PlatformFailure>> }

[<RequireQualifiedAccess>]
module ExecutionRuntime =
    let private current = AsyncLocal<ExecutionDispatcher option>()

    let get () = current.Value
    let set dispatcher = current.Value <- dispatcher

    let private missingHarness context capability =
        PlatformFailure.create
            PlatformErrorCategory.PermissionDenied
            (sprintf "%s execution requires an active harness." capability)
            false
            (context.Correlation.ExecutionId |> ExecutionId.serialize |> Some)
        |> Error
        |> Task.FromResult

    let runAgent context (agent: Agent) input =
        match current.Value, context.ExecutionBoundary with
        | Some dispatcher, _ -> dispatcher.RunAgent context agent input
        | None, ExecutionBoundary.Unrestricted ->
            task {
                let! output = agent.Execute context input
                return Ok output
            }
        | None, ExecutionBoundary.HarnessRequired -> missingHarness context "Agent"

    let runTool context idempotencyKey (tool: Tool) input =
        match current.Value, context.ExecutionBoundary with
        | Some dispatcher, _ -> dispatcher.RunTool context idempotencyKey tool input
        | None, ExecutionBoundary.Unrestricted ->
            task {
                let! result = tool.RunAsync context input

                return
                    result
                    |> Result.mapError (fun failure ->
                        failure.ToPlatformFailure(context.Correlation.ExecutionId |> ExecutionId.serialize |> Some))
            }
        | None, ExecutionBoundary.HarnessRequired -> missingHarness context "Tool"

[<RequireQualifiedAccess>]
module Agent =

    /// Construct an immutable agent capability from metadata and executable functions.
    let create id name description priority responsibilities contract execute =
        let metadata =
            { Id = id
              Name = name
              Description = description
              Priority = priority
              Responsibilities = responsibilities
              Contract = contract }

        { Metadata = metadata
          WorkerReference = None
          WorkerPayloadProtection = WorkerPayloadProtection.RedactedText
          Execute = execute }

    /// Construct an agent capability resolved by stable identity inside a process worker.
    let createWorker id name description priority responsibilities contract workerReference =
        let localExecutionNotSupported _ _ =
            Task.FromException<string>(
                GovernedExecutionException "Worker agents must execute through a configured process environment."
            )

        { create id name description priority responsibilities contract localExecutionNotSupported with
            WorkerReference = Some workerReference }

    /// Construct a worker agent whose opaque untrusted payload must not be text-redacted in transit.
    let createOpaqueWorker id name description priority responsibilities contract workerReference =
        { createWorker id name description priority responsibilities contract workerReference with
            WorkerPayloadProtection = WorkerPayloadProtection.OpaqueUntrusted }

    /// Execute an agent through the active governed runtime.
    let runAsync context input agent =
        ExecutionRuntime.runAgent context agent input
