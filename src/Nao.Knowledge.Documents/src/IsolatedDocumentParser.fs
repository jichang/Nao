namespace Nao.Knowledge.Documents

open System
open System.Threading.Tasks
open Nao.Agents
open Nao.Knowledge
open Nao.Documents

type IsolatedDocumentParserConfig =
    { Environment: ExecutionEnvironment
      CreateAgentContext: ExecutionContext -> AuthorizationScope -> AgentContext
      PackageReadLimits: PackageReadLimits
      MaximumMemoryBytes: int64
      MaximumOutputBytes: int64
      MaximumCpuTime: TimeSpan option
      MaximumDuration: TimeSpan }

[<RequireQualifiedAccess>]
module IsolatedDocumentParser =
    let private validate config =
        if not config.Environment.Isolation.SeparateProcess then
            invalidArg (nameof config) "Document parsing requires a separate execution process."

        if not config.Environment.Isolation.SupportsHardMemoryLimit then
            invalidArg (nameof config) "Document parsing requires hard memory-limit enforcement."

        if isNull (box config.CreateAgentContext) then
            nullArg (nameof config.CreateAgentContext)

        if config.MaximumMemoryBytes <= 0L || config.MaximumOutputBytes <= 0L then
            invalidArg (nameof config) "Document parser memory and output limits must be positive."

        if config.MaximumDuration <= TimeSpan.Zero then
            invalidArg (nameof config) "Document parser duration must be positive."

        config.MaximumCpuTime
        |> Option.iter (fun limit ->
            if limit <= TimeSpan.Zero then
                invalidArg (nameof config) "Document parser CPU time must be positive.")

    let create config (parser: KnowledgeParser) =
        validate config

        let agent =
            Agent.createOpaqueWorker
                DocumentParserWorkerProtocol.WorkerId
                "Knowledge document parser"
                "Parses untrusted documents inside a hard-memory-limited worker."
                0
                []
                AgentContract.Text
                (WorkerAgentReference.create DocumentParserWorkerProtocol.WorkerId)

        { parser with
            ParseAsync =
                fun authorization content fileName cancellationToken ->
                    task {
                        let request =
                            { Schema = DocumentParserWorkerProtocol.CurrentSchema
                              ParserId = parser.Descriptor.Parser.ComponentId
                              FileName = fileName
                              Content = content
                              GroupId = authorization |> AuthorizationScope.groupId |> Option.map GroupId.value
                              WorkspaceId = authorization |> AuthorizationScope.workspaceId |> WorkspaceId.value
                              SessionId = authorization |> AuthorizationScope.sessionId |> Option.map SessionId.value
                              PackageReadLimits = config.PackageReadLimits }

                        let resourceLimits =
                            { ResourceLimits.Unlimited with
                                MaxDuration = config.MaximumDuration
                                MaxMemoryBytes = Some config.MaximumMemoryBytes
                                MaxCpuTime = config.MaximumCpuTime
                                MaxOutputBytes = Some config.MaximumOutputBytes }

                        let execution =
                            ExecutionContext.CreateWithCancellation
                                { SandboxConfig.Restricted resourceLimits with
                                    Isolation = SandboxIsolation.Container }
                                cancellationToken

                        let agentContext = config.CreateAgentContext execution authorization

                        if
                            agentContext.ExecutionBoundary <> ExecutionBoundary.HarnessRequired
                            || agentContext.Authorization.IsNone
                            || (agentContext.Authorization
                                |> Option.map (AuthorizationScope.identity >> AuthorizationIdentity.storageKey))
                               <> Some(authorization |> AuthorizationScope.identity |> AuthorizationIdentity.storageKey)
                        then
                            invalidOp "Document parser context factory returned invalid worker authorization."

                        match!
                            config.Environment.ExecuteAsync
                                execution
                                agentContext
                                agent
                                (DocumentParserWorkerProtocol.serializeRequest request)
                        with
                        | Ok response -> return DocumentParserWorkerProtocol.deserializeResponse response
                        | Error LimitExceeded.Memory ->
                            return
                                Error
                                    { Kind = ParserRejectionKind.Suspicious
                                      Message = "Document parser exceeded its hard memory limit." }
                        | Error LimitExceeded.Duration ->
                            return
                                Error
                                    { Kind = ParserRejectionKind.Suspicious
                                      Message = "Document parser exceeded its execution deadline." }
                        | Error LimitExceeded.Cpu ->
                            return
                                Error
                                    { Kind = ParserRejectionKind.Suspicious
                                      Message = "Document parser exceeded its CPU-time limit." }
                        | Error LimitExceeded.Output ->
                            return
                                Error
                                    { Kind = ParserRejectionKind.Suspicious
                                      Message = "Document parser response exceeded its output limit." }
                        | Error limit ->
                            return
                                Error
                                    { Kind = ParserRejectionKind.Suspicious
                                      Message = $"Document parser worker exceeded the {limit} limit." }
                    } }
