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
      BeginEmbeddingCall: unit -> unit
      RecordEmbeddingUsage: int -> decimal -> unit
      BeginToolCall: unit -> unit }

/// Worker-local access to host-authorized secret handles.
[<NoEquality; NoComparison>]
type WorkerSecrets =
    { Resolve: SecretReference -> SecretValue }

/// Serializable execution request sent to a process worker.
type WorkerExecutionRequest =
    {
        AgentId: string
        AuthorityAgentId: string
        Input: string
        ExecutionId: string
        CorrelationId: string
        CausationExecutionId: string option
        Attempt: int
        /// Host authorization metadata, never appended to model input.
        Principal: SecurityPrincipal option
        /// Ordered agent executions acting under the original principal.
        AgentLineage: AgentAuthority list
        EnforceFileSystemPolicy: bool
        AllowedPaths: string list
        Secrets: WorkerSecrets
        Budget: WorkerExecutionBudget
    }

/// Function resolved by a worker for one stable agent identity.
type WorkerAgentHandler = WorkerExecutionRequest -> Task<Result<string, string>>

[<AllowNullLiteral>]
type internal WorkerMessage() =
    member val ProtocolVersion = 0 with get, set
    member val Kind = "" with get, set
    member val AgentId = "" with get, set
    member val AuthorityAgentId = "" with get, set
    member val Input = "" with get, set
    member val Output = "" with get, set
    member val Error = "" with get, set
    member val ExecutionId = "" with get, set
    member val CorrelationId = "" with get, set
    member val CausationExecutionId = "" with get, set
    member val Attempt = 0 with get, set
    member val Principal: SecurityPrincipal option = None with get, set
    member val AgentLineage: AgentAuthority array = Array.empty with get, set
    member val RequirePrincipal = false with get, set
    member val ProcessId = 0 with get, set
    member val EnforceFileSystemPolicy = false with get, set
    member val AllowedPaths: string array = Array.empty with get, set
    member val Secrets = Dictionary<string, string>() with get, set
    member val MaxLlmCalls = Int32.MaxValue with get, set
    member val MaxEmbeddingCalls = Int32.MaxValue with get, set
    member val MaxTotalTokens = Int32.MaxValue with get, set
    member val MaxCostUsd = Decimal.MaxValue.ToString(Globalization.CultureInfo.InvariantCulture) with get, set
    member val MaxToolCalls = Int32.MaxValue with get, set
    member val Limit = "" with get, set
    member val LlmCalls = 0 with get, set
    member val EmbeddingCalls = 0 with get, set
    member val TotalTokens = 0 with get, set
    member val EstimatedCostUsd = "0" with get, set
    member val ToolCalls = 0 with get, set

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

    [<Literal>]
    let LimitExceeded = "limit-exceeded"

    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let private serializePrincipal principal =
        let fields = Dictionary<string, obj>()
        fields.Add("tenantId", box (SecurityPrincipal.tenantId principal |> TenantId.value))
        fields.Add("subjectId", box (SecurityPrincipal.subjectId principal |> UserId.value))

        fields.Add(
            "impersonatedUserId",
            box (
                SecurityPrincipal.impersonation principal
                |> Option.map (_.EffectiveUserId >> UserId.value)
                |> Option.toObj
            )
        )

        fields.Add(
            "impersonationReason",
            box (SecurityPrincipal.impersonation principal |> Option.map _.Reason |> Option.toObj)
        )

        fields.Add(
            "serviceId",
            box (
                SecurityPrincipal.serviceId principal
                |> Option.map ServiceId.value
                |> Option.toObj
            )
        )

        fields.Add(
            "serviceDelegations",
            box (
                SecurityPrincipal.serviceLineage principal
                |> List.skip (
                    if SecurityPrincipal.serviceId principal |> Option.isSome then
                        1
                    else
                        0
                )
                |> List.map ServiceId.value
                |> List.toArray
            )
        )

        fields.Add("groupIds", box (SecurityPrincipal.groupIds principal |> Seq.map GroupId.value |> Seq.toArray))
        fields.Add("roles", box (SecurityPrincipal.roles principal |> Set.toArray))
        fields.Add("claims", box (SecurityPrincipal.claims principal))
        fields.Add("authenticationStrength", box (string (SecurityPrincipal.authenticationStrength principal)))
        fields

    let private serializeAuthority (authority: AgentAuthority) =
        let fields = Dictionary<string, obj>()
        fields.Add("agentId", box authority.AgentId)
        fields.Add("executionId", box (ExecutionId.serialize authority.ExecutionId))
        fields

    let private rejectDuplicateProperties (element: JsonElement) =
        let names = HashSet<string>(StringComparer.Ordinal)

        for property in element.EnumerateObject() do
            if not (names.Add property.Name) then
                raise (JsonException "Duplicate worker identity property.")

    let private deserializePrincipal (element: JsonElement) =
        rejectDuplicateProperties element
        let tenantId = element.GetProperty("tenantId").GetString() |> TenantId.parse
        let subjectId = element.GetProperty("subjectId").GetString() |> UserId.parse
        let impersonatedUser = element.GetProperty("impersonatedUserId")
        let impersonationReason = element.GetProperty("impersonationReason")
        let service = element.GetProperty("serviceId")

        let serviceId =
            if service.ValueKind = JsonValueKind.Null then
                None
            else
                Some(service.GetString() |> ServiceId.parse)

        let serviceDelegations =
            element.GetProperty("serviceDelegations").EnumerateArray()
            |> Seq.map (fun delegated -> delegated.GetString() |> ServiceId.parse)
            |> Seq.toList

        let groupIds =
            element.GetProperty("groupIds").EnumerateArray()
            |> Seq.map (fun group -> group.GetString() |> GroupId.parse)
            |> Seq.toList

        let roles =
            element.GetProperty("roles").EnumerateArray()
            |> Seq.map _.GetString()
            |> Seq.toList

        let claimFields = element.GetProperty("claims")
        rejectDuplicateProperties claimFields

        let claims =
            claimFields.EnumerateObject()
            |> Seq.map (fun claim -> claim.Name, claim.Value.GetString())
            |> Map.ofSeq

        let strength =
            match element.GetProperty("authenticationStrength").GetString() with
            | "Unspecified" -> AuthenticationStrength.Unspecified
            | "SingleFactor" -> AuthenticationStrength.SingleFactor
            | "MultiFactor" -> AuthenticationStrength.MultiFactor
            | "PhishingResistant" -> AuthenticationStrength.PhishingResistant
            | _ -> raise (JsonException "Invalid worker authentication strength.")

        let principal =
            match impersonatedUser.ValueKind, impersonationReason.ValueKind, serviceId with
            | JsonValueKind.Null, JsonValueKind.Null, _ ->
                SecurityPrincipal.createAuthenticated tenantId subjectId serviceId groupIds roles claims strength
            | JsonValueKind.String, JsonValueKind.String, None ->
                SecurityPrincipal.createImpersonated
                    tenantId
                    subjectId
                    (impersonatedUser.GetString() |> UserId.parse)
                    (impersonationReason.GetString())
                    groupIds
                    roles
                    claims
                    strength
            | _ -> raise (JsonException "Invalid worker impersonation authority.")

        serviceDelegations
        |> List.fold
            (fun delegated delegatedService ->
                let current =
                    SecurityPrincipal.currentService delegated
                    |> Option.defaultWith (fun () ->
                        raise (JsonException "Service delegation requires an authenticated service."))

                SecurityPrincipal.delegateService current delegatedService delegated)
            principal

    let internal serialize (message: WorkerMessage) =
        let fields = Dictionary<string, obj>()
        fields.Add("protocolVersion", box message.ProtocolVersion)
        fields.Add("kind", box message.Kind)
        fields.Add("agentId", box message.AgentId)
        fields.Add("authorityAgentId", box message.AuthorityAgentId)
        fields.Add("input", box message.Input)
        fields.Add("output", box message.Output)
        fields.Add("error", box message.Error)
        fields.Add("executionId", box message.ExecutionId)
        fields.Add("correlationId", box message.CorrelationId)
        fields.Add("causationExecutionId", box message.CausationExecutionId)
        fields.Add("attempt", box message.Attempt)
        fields.Add("principal", message.Principal |> Option.map (serializePrincipal >> box) |> Option.toObj)
        fields.Add("agentLineage", box (message.AgentLineage |> Array.map serializeAuthority))
        fields.Add("requirePrincipal", box message.RequirePrincipal)
        fields.Add("processId", box message.ProcessId)
        fields.Add("enforceFileSystemPolicy", box message.EnforceFileSystemPolicy)
        fields.Add("allowedPaths", box message.AllowedPaths)
        fields.Add("secrets", box message.Secrets)
        fields.Add("maxLlmCalls", box message.MaxLlmCalls)
        fields.Add("maxEmbeddingCalls", box message.MaxEmbeddingCalls)
        fields.Add("maxTotalTokens", box message.MaxTotalTokens)
        fields.Add("maxCostUsd", box message.MaxCostUsd)
        fields.Add("maxToolCalls", box message.MaxToolCalls)
        fields.Add("limit", box message.Limit)
        fields.Add("llmCalls", box message.LlmCalls)
        fields.Add("embeddingCalls", box message.EmbeddingCalls)
        fields.Add("totalTokens", box message.TotalTokens)
        fields.Add("estimatedCostUsd", box message.EstimatedCostUsd)
        fields.Add("toolCalls", box message.ToolCalls)
        JsonSerializer.Serialize(fields, options)

    let internal deserialize (value: string) =
        try
            use document = JsonDocument.Parse value
            let root = document.RootElement
            rejectDuplicateProperties root
            let message = WorkerMessage()
            message.ProtocolVersion <- root.GetProperty("protocolVersion").GetInt32()
            message.Kind <- root.GetProperty("kind").GetString()
            message.AgentId <- root.GetProperty("agentId").GetString()
            message.AuthorityAgentId <- root.GetProperty("authorityAgentId").GetString()
            message.Input <- root.GetProperty("input").GetString()
            message.Output <- root.GetProperty("output").GetString()
            message.Error <- root.GetProperty("error").GetString()
            message.ExecutionId <- root.GetProperty("executionId").GetString()
            message.CorrelationId <- root.GetProperty("correlationId").GetString()
            message.CausationExecutionId <- root.GetProperty("causationExecutionId").GetString()
            message.Attempt <- root.GetProperty("attempt").GetInt32()

            if message.Kind = Execute then
                message.RequirePrincipal <- root.GetProperty("requirePrincipal").GetBoolean()
                let principal = root.GetProperty("principal")

                message.Principal <-
                    if principal.ValueKind = JsonValueKind.Null then
                        None
                    else
                        Some(deserializePrincipal principal)

                if message.RequirePrincipal && message.Principal.IsNone then
                    raise (JsonException "Protected worker execution requires a principal.")

                message.AgentLineage <-
                    root.GetProperty("agentLineage").EnumerateArray()
                    |> Seq.map (fun authority ->
                        rejectDuplicateProperties authority

                        let agentId = authority.GetProperty("agentId").GetString()

                        if String.IsNullOrWhiteSpace agentId || agentId <> agentId.Trim() then
                            raise (JsonException "Worker agent authority must have a canonical agent identity.")

                        { AgentId = agentId
                          ExecutionId = authority.GetProperty("executionId").GetString() |> ExecutionId.parse })
                    |> Seq.toArray

                if
                    message.AgentLineage |> Seq.map _.ExecutionId |> Seq.distinct |> Seq.length
                    <> message.AgentLineage.Length
                then
                    raise (JsonException "Worker agent authority execution identities must be unique.")

                if message.RequirePrincipal then
                    match Array.tryLast message.AgentLineage with
                    | Some authority when
                        authority.AgentId = message.AuthorityAgentId
                        && ExecutionId.serialize authority.ExecutionId = message.ExecutionId
                        ->
                        ()
                    | _ ->
                        raise (JsonException "Protected worker execution requires matching delegated agent authority.")

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
            message.MaxEmbeddingCalls <- root.GetProperty("maxEmbeddingCalls").GetInt32()
            message.MaxTotalTokens <- root.GetProperty("maxTotalTokens").GetInt32()
            message.MaxCostUsd <- root.GetProperty("maxCostUsd").GetString()
            message.MaxToolCalls <- root.GetProperty("maxToolCalls").GetInt32()
            message.Limit <- root.GetProperty("limit").GetString()
            message.LlmCalls <- root.GetProperty("llmCalls").GetInt32()
            message.EmbeddingCalls <- root.GetProperty("embeddingCalls").GetInt32()
            message.TotalTokens <- root.GetProperty("totalTokens").GetInt32()
            message.EstimatedCostUsd <- root.GetProperty("estimatedCostUsd").GetString()
            message.ToolCalls <- root.GetProperty("toolCalls").GetInt32()

            Ok message
        with
        | :? JsonException
        | :? KeyNotFoundException
        | :? InvalidOperationException
        | :? ArgumentException
        | :? FormatException
        | :? OverflowException
        | :? UnauthorizedAccessException -> Error "Worker protocol message was invalid."

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
        (agentContext: AgentContext)
        enforceFileSystemPolicy
        allowedPaths
        secrets
        input
        =
        let value = message Execute version
        value.AgentId <- agentId

        value.AuthorityAgentId <-
            agentContext.Authorization
            |> Option.bind AuthorizationScope.currentAgent
            |> Option.map _.AgentId
            |> Option.defaultValue ""

        value.Input <- input
        value.ExecutionId <- ExecutionId.serialize context.ExecutionId
        value.CorrelationId <- context.Correlation.CorrelationId |> CorrelationId.value |> string

        value.CausationExecutionId <-
            context.Correlation.CausationId
            |> Option.map ExecutionId.serialize
            |> Option.defaultValue ""

        value.Attempt <- context.Correlation.Attempt
        value.Principal <- agentContext.Authorization |> Option.map AuthorizationScope.principal

        value.AgentLineage <-
            agentContext.Authorization
            |> Option.map (AuthorizationScope.agentLineage >> List.toArray)
            |> Option.defaultValue Array.empty

        value.RequirePrincipal <- agentContext.ExecutionBoundary = ExecutionBoundary.HarnessRequired
        value.EnforceFileSystemPolicy <- enforceFileSystemPolicy
        value.AllowedPaths <- allowedPaths |> List.toArray

        for KeyValue(handle, secret) in secrets do
            value.Secrets.Add(handle, secret)

        let usage = context.Usage
        value.MaxLlmCalls <- max 0 (context.Sandbox.Limits.MaxLlmCalls - usage.LlmCalls)
        value.MaxEmbeddingCalls <- max 0 (context.Sandbox.Limits.MaxEmbeddingCalls - usage.EmbeddingCalls)
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
        message.EmbeddingCalls <- usage.EmbeddingCalls
        message.TotalTokens <- usage.TotalTokens
        message.EstimatedCostUsd <- usage.EstimatedCostUsd.ToString(Globalization.CultureInfo.InvariantCulture)
        message.ToolCalls <- usage.ToolCalls
        message

    let internal usage (message: WorkerMessage) =
        { ResourceUsage.Zero with
            LlmCalls = message.LlmCalls
            EmbeddingCalls = message.EmbeddingCalls
            TotalTokens = message.TotalTokens
            EstimatedCostUsd = Decimal.Parse(message.EstimatedCostUsd, Globalization.CultureInfo.InvariantCulture)
            ToolCalls = message.ToolCalls }

    let internal tryParseLimit value =
        match value with
        | "LlmCalls" -> Some Nao.Agents.LimitExceeded.LlmCalls
        | "EmbeddingCalls" -> Some Nao.Agents.LimitExceeded.EmbeddingCalls
        | "TotalTokens" -> Some Nao.Agents.LimitExceeded.TotalTokens
        | "Cost" -> Some Nao.Agents.LimitExceeded.Cost
        | "ToolCalls" -> Some Nao.Agents.LimitExceeded.ToolCalls
        | _ -> None

    let internal executionRequest (message: WorkerMessage) : WorkerExecutionRequest * Nao.Agents.ExecutionContext =
        let limits =
            { ResourceLimits.Unlimited with
                MaxLlmCalls = message.MaxLlmCalls
                MaxEmbeddingCalls = message.MaxEmbeddingCalls
                MaxTotalTokens = message.MaxTotalTokens
                MaxCostUsd = Decimal.Parse(message.MaxCostUsd, Globalization.CultureInfo.InvariantCulture)
                MaxToolCalls = message.MaxToolCalls }

        let correlation =
            { ExecutionId = ExecutionId.parse message.ExecutionId
              CorrelationId = CorrelationId.parse message.CorrelationId
              CausationId =
                if String.IsNullOrWhiteSpace message.CausationExecutionId then
                    None
                else
                    Some(ExecutionId.parse message.CausationExecutionId)
              Attempt = message.Attempt }

        let context =
            ExecutionContext.CreateWithCorrelation
                { SandboxConfig.Default with
                    Limits = limits }
                correlation

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
              BeginEmbeddingCall = fun () -> requireAvailable context.BeginEmbeddingCall
              RecordEmbeddingUsage =
                fun tokens costUsd ->
                    if tokens < 0 then
                        invalidArg (nameof tokens) "Worker embedding token usage cannot be negative."

                    if costUsd < 0m then
                        invalidArg (nameof costUsd) "Worker embedding cost usage cannot be negative."

                    requireAvailable (fun () -> context.RecordEmbeddingUsage(tokens, costUsd))
              BeginToolCall = fun () -> requireAvailable context.BeginToolCall }

        let secrets =
            { Resolve =
                fun reference ->
                    match message.Secrets.TryGetValue(SecretReference.value reference) with
                    | true, value -> SecretValue.create reference value
                    | false, _ ->
                        raise (UnauthorizedAccessException "Secret reference was not authorized for this worker.") }

        let request =
            { AgentId = message.AgentId
              AuthorityAgentId = message.AuthorityAgentId
              Input = message.Input
              ExecutionId = message.ExecutionId
              CorrelationId = message.CorrelationId
              CausationExecutionId = correlation.CausationId |> Option.map ExecutionId.serialize
              Attempt = message.Attempt
              Principal = message.Principal
              AgentLineage = message.AgentLineage |> List.ofArray
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
