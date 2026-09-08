namespace Nao.Agents

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Worker-local resource accounting capability constructed from host-supplied limits.
[<NoEquality; NoComparison>]
type WorkerExecutionBudget =
    { BeginLlmCall: unit -> unit
      RecordLlmUsage: int -> decimal -> unit
      BeginToolCall: unit -> unit }

/// Worker-local access to host-authorized secret handles.
[<NoEquality; NoComparison>]
type WorkerSecrets = { Resolve: string -> string }

/// Serializable execution request sent to a process worker.
type WorkerExecutionRequest =
    { AgentId: string
      Input: string
      ExecutionId: string
      CorrelationId: string
      Attempt: int
      EnforceFileSystemPolicy: bool
      AllowedPaths: string list
      Secrets: WorkerSecrets
      Budget: WorkerExecutionBudget }

/// Function resolved by a worker for one stable agent identity.
type WorkerAgentHandler = WorkerExecutionRequest -> Task<Result<string, string>>

[<AllowNullLiteral>]
type internal WorkerMessage() =
    member val ProtocolVersion = 0 with get, set
    member val Kind = "" with get, set
    member val AgentId = "" with get, set
    member val Input = "" with get, set
    member val Output = "" with get, set
    member val Error = "" with get, set
    member val ExecutionId = "" with get, set
    member val CorrelationId = "" with get, set
    member val Attempt = 0 with get, set
    member val ProcessId = 0 with get, set
    member val EnforceFileSystemPolicy = false with get, set
    member val AllowedPaths: string array = Array.empty with get, set
    member val Secrets = Dictionary<string, string>() with get, set
    member val MaxLlmCalls = Int32.MaxValue with get, set
    member val MaxTotalTokens = Int32.MaxValue with get, set
    member val MaxCostUsd = Decimal.MaxValue.ToString(Globalization.CultureInfo.InvariantCulture) with get, set
    member val MaxToolCalls = Int32.MaxValue with get, set
    member val Limit = "" with get, set
    member val LlmCalls = 0 with get, set
    member val TotalTokens = 0 with get, set
    member val EstimatedCostUsd = "0" with get, set
    member val ToolCalls = 0 with get, set

[<RequireQualifiedAccess>]
module WorkerProtocol =
    [<Literal>]
    let CurrentVersion = 4

    [<Literal>]
    let Hello = "hello"

    [<Literal>]
    let Execute = "execute"

    [<Literal>]
    let Succeeded = "succeeded"

    [<Literal>]
    let Failed = "failed"

    [<Literal>]
    let LimitExceeded = "limit-exceeded"

    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let internal serialize (message: WorkerMessage) =
        let fields = Dictionary<string, obj>()
        fields.Add("protocolVersion", box message.ProtocolVersion)
        fields.Add("kind", box message.Kind)
        fields.Add("agentId", box message.AgentId)
        fields.Add("input", box message.Input)
        fields.Add("output", box message.Output)
        fields.Add("error", box message.Error)
        fields.Add("executionId", box message.ExecutionId)
        fields.Add("correlationId", box message.CorrelationId)
        fields.Add("attempt", box message.Attempt)
        fields.Add("processId", box message.ProcessId)
        fields.Add("enforceFileSystemPolicy", box message.EnforceFileSystemPolicy)
        fields.Add("allowedPaths", box message.AllowedPaths)
        fields.Add("secrets", box message.Secrets)
        fields.Add("maxLlmCalls", box message.MaxLlmCalls)
        fields.Add("maxTotalTokens", box message.MaxTotalTokens)
        fields.Add("maxCostUsd", box message.MaxCostUsd)
        fields.Add("maxToolCalls", box message.MaxToolCalls)
        fields.Add("limit", box message.Limit)
        fields.Add("llmCalls", box message.LlmCalls)
        fields.Add("totalTokens", box message.TotalTokens)
        fields.Add("estimatedCostUsd", box message.EstimatedCostUsd)
        fields.Add("toolCalls", box message.ToolCalls)
        JsonSerializer.Serialize(fields, options)

    let internal deserialize (value: string) =
        try
            use document = JsonDocument.Parse value
            let root = document.RootElement
            let message = WorkerMessage()
            message.ProtocolVersion <- root.GetProperty("protocolVersion").GetInt32()
            message.Kind <- root.GetProperty("kind").GetString()
            message.AgentId <- root.GetProperty("agentId").GetString()
            message.Input <- root.GetProperty("input").GetString()
            message.Output <- root.GetProperty("output").GetString()
            message.Error <- root.GetProperty("error").GetString()
            message.ExecutionId <- root.GetProperty("executionId").GetString()
            message.CorrelationId <- root.GetProperty("correlationId").GetString()
            message.Attempt <- root.GetProperty("attempt").GetInt32()
            message.ProcessId <- root.GetProperty("processId").GetInt32()
            message.EnforceFileSystemPolicy <- root.GetProperty("enforceFileSystemPolicy").GetBoolean()

            message.AllowedPaths <-
                root.GetProperty("allowedPaths").EnumerateArray()
                |> Seq.map _.GetString()
                |> Seq.toArray

            let secrets = Dictionary<string, string>()

            for property in root.GetProperty("secrets").EnumerateObject() do
                secrets.Add(property.Name, property.Value.GetString())

            message.Secrets <- secrets

            message.MaxLlmCalls <- root.GetProperty("maxLlmCalls").GetInt32()
            message.MaxTotalTokens <- root.GetProperty("maxTotalTokens").GetInt32()
            message.MaxCostUsd <- root.GetProperty("maxCostUsd").GetString()
            message.MaxToolCalls <- root.GetProperty("maxToolCalls").GetInt32()
            message.Limit <- root.GetProperty("limit").GetString()
            message.LlmCalls <- root.GetProperty("llmCalls").GetInt32()
            message.TotalTokens <- root.GetProperty("totalTokens").GetInt32()
            message.EstimatedCostUsd <- root.GetProperty("estimatedCostUsd").GetString()
            message.ToolCalls <- root.GetProperty("toolCalls").GetInt32()

            Ok message
        with error ->
            Error(sprintf "Worker protocol message was invalid JSON: %s" error.Message)

    let internal message kind version =
        let value = WorkerMessage()
        value.ProtocolVersion <- version
        value.Kind <- kind
        value.ProcessId <- Environment.ProcessId
        value

    let internal request
        version
        agentId
        (context: Nao.Agents.ExecutionContext)
        enforceFileSystemPolicy
        allowedPaths
        secrets
        input
        =
        let value = message Execute version
        value.AgentId <- agentId
        value.Input <- input
        value.ExecutionId <- ExecutionId.serialize context.ExecutionId
        value.CorrelationId <- context.Correlation.CorrelationId |> CorrelationId.value |> string
        value.Attempt <- context.Correlation.Attempt
        value.EnforceFileSystemPolicy <- enforceFileSystemPolicy
        value.AllowedPaths <- allowedPaths |> List.toArray

        for KeyValue(handle, secret) in secrets do
            value.Secrets.Add(handle, secret)

        let usage = context.Usage
        value.MaxLlmCalls <- max 0 (context.Sandbox.Limits.MaxLlmCalls - usage.LlmCalls)
        value.MaxTotalTokens <- max 0 (context.Sandbox.Limits.MaxTotalTokens - usage.TotalTokens)

        value.MaxCostUsd <-
            max 0m (context.Sandbox.Limits.MaxCostUsd - usage.EstimatedCostUsd)
            |> _.ToString(Globalization.CultureInfo.InvariantCulture)

        value.MaxToolCalls <- max 0 (context.Sandbox.Limits.MaxToolCalls - usage.ToolCalls)
        value

    let private failed version error =
        let value = message Failed version
        value.Error <- error
        value

    let private succeeded version output =
        let value = message Succeeded version
        value.Output <- output
        value

    let private limitExceeded version limit =
        let value = message LimitExceeded version
        value.Limit <- string limit
        value

    let private withUsage (context: Nao.Agents.ExecutionContext) (message: WorkerMessage) =
        let usage = context.Usage
        message.LlmCalls <- usage.LlmCalls
        message.TotalTokens <- usage.TotalTokens
        message.EstimatedCostUsd <- usage.EstimatedCostUsd.ToString(Globalization.CultureInfo.InvariantCulture)
        message.ToolCalls <- usage.ToolCalls
        message

    let internal usage (message: WorkerMessage) =
        { ResourceUsage.Zero with
            LlmCalls = message.LlmCalls
            TotalTokens = message.TotalTokens
            EstimatedCostUsd = Decimal.Parse(message.EstimatedCostUsd, Globalization.CultureInfo.InvariantCulture)
            ToolCalls = message.ToolCalls }

    let internal tryParseLimit value =
        match value with
        | "LlmCalls" -> Some Nao.Agents.LimitExceeded.LlmCalls
        | "TotalTokens" -> Some Nao.Agents.LimitExceeded.TotalTokens
        | "Cost" -> Some Nao.Agents.LimitExceeded.Cost
        | "ToolCalls" -> Some Nao.Agents.LimitExceeded.ToolCalls
        | _ -> None

    let internal executionRequest (message: WorkerMessage) : WorkerExecutionRequest * Nao.Agents.ExecutionContext =
        let limits =
            { ResourceLimits.Unlimited with
                MaxLlmCalls = message.MaxLlmCalls
                MaxTotalTokens = message.MaxTotalTokens
                MaxCostUsd = Decimal.Parse(message.MaxCostUsd, Globalization.CultureInfo.InvariantCulture)
                MaxToolCalls = message.MaxToolCalls }

        let context =
            ExecutionContext.Create
                { SandboxConfig.Default with
                    Limits = limits }

        let requireAvailable operation =
            match operation () with
            | None -> ()
            | Some limit -> raise (ExecutionLimitExceededException limit)

        let budget =
            { BeginLlmCall = fun () -> requireAvailable context.BeginLlmCall
              RecordLlmUsage =
                fun tokens costUsd ->
                    if tokens < 0 then
                        invalidArg (nameof tokens) "Worker token usage cannot be negative."

                    if costUsd < 0m then
                        invalidArg (nameof costUsd) "Worker cost usage cannot be negative."

                    requireAvailable (fun () -> context.RecordLlmUsage(tokens, costUsd))
              BeginToolCall = fun () -> requireAvailable context.BeginToolCall }

        let secrets =
            { Resolve =
                fun handle ->
                    match message.Secrets.TryGetValue handle with
                    | true, value -> value
                    | false, _ ->
                        raise (UnauthorizedAccessException "Secret handle was not authorized for this worker.") }

        let request =
            { AgentId = message.AgentId
              Input = message.Input
              ExecutionId = message.ExecutionId
              CorrelationId = message.CorrelationId
              Attempt = message.Attempt
              EnforceFileSystemPolicy = message.EnforceFileSystemPolicy
              AllowedPaths = message.AllowedPaths |> List.ofArray
              Secrets = secrets
              Budget = budget }

        request, context

    let private readMessage (reader: TextReader) cancellationToken =
        task {
            let! line = reader.ReadLineAsync(cancellationToken)

            if isNull line then
                return Error "Worker closed stdout before sending a protocol message."
            else
                return deserialize line
        }

    let private writeMessage (writer: TextWriter) message cancellationToken =
        task {
            do! writer.WriteLineAsync((serialize message).AsMemory(), cancellationToken)
            do! writer.FlushAsync(cancellationToken)
        }

    /// Serve one versioned worker execution over newline-delimited standard IO.
    let runSingleAsync
        (protocolVersion: int)
        (handlers: Map<string, WorkerAgentHandler>)
        (reader: TextReader)
        (writer: TextWriter)
        (cancellationToken: CancellationToken)
        =
        task {
            match! readMessage reader cancellationToken with
            | Error error -> return Error error
            | Ok hello when hello.Kind <> Hello -> return Error "Worker expected a hello message."
            | Ok hello when hello.ProtocolVersion <> protocolVersion ->
                do! writeMessage writer (message Hello protocolVersion) cancellationToken
                return Error(sprintf "Unsupported worker protocol version %d." hello.ProtocolVersion)
            | Ok _ ->
                do! writeMessage writer (message Hello protocolVersion) cancellationToken

                match! readMessage reader cancellationToken with
                | Error error -> return Error error
                | Ok request when request.Kind <> Execute -> return Error "Worker expected an execute message."
                | Ok request when request.ProtocolVersion <> protocolVersion ->
                    return Error(sprintf "Unsupported worker protocol version %d." request.ProtocolVersion)
                | Ok request ->
                    let! response =
                        match handlers |> Map.tryFind request.AgentId with
                        | None ->
                            failed protocolVersion (sprintf "Worker agent '%s' is not registered." request.AgentId)
                            |> Task.FromResult
                        | Some handler ->
                            task {
                                let executionRequest, executionContext = executionRequest request

                                try
                                    match! handler executionRequest with
                                    | Ok output -> return succeeded protocolVersion output |> withUsage executionContext
                                    | Error error -> return failed protocolVersion error |> withUsage executionContext
                                with
                                | ExecutionLimitExceededException limit ->
                                    return limitExceeded protocolVersion limit |> withUsage executionContext
                                | error -> return failed protocolVersion error.Message |> withUsage executionContext
                            }

                    do! writeMessage writer response cancellationToken
                    return Ok()
        }
