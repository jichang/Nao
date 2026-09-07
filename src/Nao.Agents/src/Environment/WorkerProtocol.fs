namespace Nao.Agents

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Serializable execution request sent to a process worker.
[<CLIMutable>]
type WorkerExecutionRequest =
    { AgentId: string
      Input: string
      ExecutionId: string
      CorrelationId: string
      Attempt: int }

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

[<RequireQualifiedAccess>]
module WorkerProtocol =
    [<Literal>]
    let CurrentVersion = 1

    [<Literal>]
    let Hello = "hello"

    [<Literal>]
    let Execute = "execute"

    [<Literal>]
    let Succeeded = "succeeded"

    [<Literal>]
    let Failed = "failed"

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
            Ok message
        with error ->
            Error(sprintf "Worker protocol message was invalid JSON: %s" error.Message)

    let internal message kind version =
        let value = WorkerMessage()
        value.ProtocolVersion <- version
        value.Kind <- kind
        value.ProcessId <- Environment.ProcessId
        value

    let internal request version agentId (context: Nao.Agents.ExecutionContext) input =
        let value = message Execute version
        value.AgentId <- agentId
        value.Input <- input
        value.ExecutionId <- ExecutionId.serialize context.ExecutionId
        value.CorrelationId <- context.Correlation.CorrelationId |> CorrelationId.value |> string
        value.Attempt <- context.Correlation.Attempt
        value

    let private failed version error =
        let value = message Failed version
        value.Error <- error
        value

    let private succeeded version output =
        let value = message Succeeded version
        value.Output <- output
        value

    let internal executionRequest (message: WorkerMessage) : WorkerExecutionRequest =
        { AgentId = message.AgentId
          Input = message.Input
          ExecutionId = message.ExecutionId
          CorrelationId = message.CorrelationId
          Attempt = message.Attempt }

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
                                try
                                    match! handler (executionRequest request) with
                                    | Ok output -> return succeeded protocolVersion output
                                    | Error error -> return failed protocolVersion error
                                with error ->
                                    return failed protocolVersion error.Message
                            }

                    do! writeMessage writer response cancellationToken
                    return Ok()
        }
