namespace Nao.Knowledge.Tests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge
open Nao.Knowledge.Documents
open Nao.Knowledge.Documents.Worker
open Nao.Documents

[<TestClass; TestCategory("Security"); TestCategory("FailClosed"); TestCategory("Isolation")>]
type IsolatedParsingTests() =
    let packageLimits = PackageReadLimits.create 128 1048576L 4194304L 100.0

    let authorization () =
        SecurityPrincipal.create (TenantId.parse "parser-tenant") (UserId.parse "parser-user") []
        |> fun principal -> AuthorizationScope.tryCreate principal None (WorkspaceId.parse "parser-workspace") None
        |> Option.get

    let createAgentContext (execution: ExecutionContext) authorization =
        let context = AgentContext.unrestrictedForTests ()

        let workerAuthorization =
            authorization
            |> AuthorizationScope.startAgent DocumentParserWorkerProtocol.WorkerId execution.ExecutionId

        { context with
            Correlation = execution.Correlation
            ExecutionBoundary = ExecutionBoundary.HarnessRequired
            CancellationToken = execution.CancellationToken
            Authorization = Some workerAuthorization }

    let config environment =
        { Environment = environment
          CreateAgentContext = createAgentContext
          PackageReadLimits = packageLimits
          MaximumMemoryBytes = 268435456L
          MaximumOutputBytes = 4194304L
          MaximumCpuTime = Some(TimeSpan.FromSeconds 10.0)
          MaximumDuration = TimeSpan.FromSeconds 15.0 }

    [<TestMethod>]
    member _.``Isolated parser rejects boundaries without hard memory enforcement``() =
        let error =
            Assert.ThrowsExactly<ArgumentException>(fun () ->
                IsolatedDocumentParser.create
                    (config (
                        ExecutionEnvironment.processWorker
                            { FileName = "/bin/true"
                              Arguments = []
                              ProtocolVersion = WorkerProtocol.CurrentVersion
                              IdentityIsolation = ProcessIdentityIsolation.Disabled
                              FileSystemIsolation = ProcessFileSystemIsolation.Disabled
                              SecretResolver = None
                              OutputCapture = None }
                    ))
                    (DocumentParserRuntime.markdown packageLimits)
                |> ignore)

        StringAssert.Contains(error.Message, "hard memory-limit")

    [<TestMethod>]
    member _.``Hard memory violation becomes suspicious parser rejection``() =
        let environment =
            { Isolation =
                { SeparateProcess = true
                  SupportsHardMemoryLimit = true }
              ExecuteAsync = fun _ _ _ _ -> Task.FromResult(Error LimitExceeded.Memory) }

        let parser =
            DocumentParserRuntime.markdown packageLimits
            |> IsolatedDocumentParser.create (config environment)

        let result =
            parser.ParseAsync
                (authorization ())
                (Encoding.UTF8.GetBytes "# guide")
                (Some "guide.md")
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match result with
        | Error rejection ->
            Assert.AreEqual(ParserRejectionKind.Suspicious, rejection.Kind)
            StringAssert.Contains(rejection.Message, "hard memory")
        | Ok _ -> Assert.Fail "Expected hard-memory rejection."

    [<TestMethod>]
    member _.``Document worker protocol round-trips parsed units``() =
        let parserRequest =
            { Schema = DocumentParserWorkerProtocol.CurrentSchema
              ParserId = "signedbyte.documents.markdown"
              FileName = Some "guide.md"
              Content = Encoding.UTF8.GetBytes "# Guide"
              GroupId = None
              WorkspaceId = "parser-workspace"
              SessionId = None
              PackageReadLimits = packageLimits }

        let request =
            { AgentId = DocumentParserWorkerProtocol.WorkerId
              AuthorityAgentId = DocumentParserWorkerProtocol.WorkerId
              Input = DocumentParserWorkerProtocol.serializeRequest parserRequest
              ExecutionId = "execution"
              CorrelationId = "correlation"
              Attempt = 1
              Principal =
                Some(SecurityPrincipal.create (TenantId.parse "parser-tenant") (UserId.parse "parser-user") [])
              AgentLineage = []
              EnforceFileSystemPolicy = false
              AllowedPaths = []
              Secrets = { Resolve = fun _ -> failwith "No secrets are available." }
              Budget =
                { BeginLlmCall = ignore
                  RecordLlmUsage = fun _ _ -> ()
                  BeginToolCall = ignore } }

        let response =
            DocumentParserWorkerHandler.handle request |> _.GetAwaiter().GetResult()

        match response with
        | Error error -> Assert.Fail error
        | Ok payload ->
            match DocumentParserWorkerProtocol.deserializeResponse payload with
            | Error rejection -> Assert.Fail rejection.Message
            | Ok document ->
                let unit = document.Units |> List.exactlyOne
                Assert.AreEqual(ContentUnitKind.Heading, unit.Kind)
                Assert.AreEqual("Guide", unit.Text)

    [<TestMethod>]
    member _.``Document worker protocol rejects unsupported schemas``() =
        Assert.AreEqual("nao-document-parser/v1", DocumentParserWorkerProtocol.CurrentSchema)

        let request =
            { Schema = "nao-document-parser/v2"
              ParserId = "signedbyte.documents.markdown"
              FileName = None
              Content = Array.empty
              GroupId = None
              WorkspaceId = "parser-workspace"
              SessionId = None
              PackageReadLimits = packageLimits }

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            request
            |> DocumentParserWorkerProtocol.serializeRequest
            |> DocumentParserWorkerProtocol.deserializeRequest
            |> ignore)
        |> ignore
