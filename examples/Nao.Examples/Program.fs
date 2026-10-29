namespace Nao.Examples

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Eval
open Nao.Eval.Evaluators
open Nao.Knowledge
open Nao.Persistence
open Nao.Reasoning
open Nao.Reasoning.Datalog
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

module private Host =
    let authorization =
        SecurityPrincipal.createAuthenticated
            (TenantId.parse "example-tenant")
            (UserId.parse "example-user")
            (Some(ServiceId.parse "example-host"))
            [ GroupId.parse "example-readers" ]
            [ "knowledge-reader"; RuntimeAdministrationAuthorization.AdministratorRole ]
            Map.empty
            AuthenticationStrength.MultiFactor
        |> fun principal ->
            AuthorizationScope.tryCreate
                principal
                None
                (WorkspaceId.parse "example-workspace")
                (Some(SessionId.parse "example-session"))
            |> Option.get

    let context () =
        let correlation = CorrelationContext.root ()
        let turnId = TurnId.generate ()
        let turnKey = IdempotencyKey.turn authorization "example-conversation" turnId
        let mutable toolOrdinal = 0
        let mutable artifactOrdinal = 0
        let artifacts = ResizeArray<Artifact>()

        { Correlation = correlation
          SessionKey = "example-session"
          TurnId = TurnId.value turnId
          ExecutionBoundary = ExecutionBoundary.HarnessRequired
          CancellationToken = CancellationToken.None
          NextToolCallKey =
            fun () ->
                toolOrdinal <- toolOrdinal + 1
                IdempotencyKey.operation "tool" toolOrdinal turnKey
          NextArtifactKey =
            fun () ->
                artifactOrdinal <- artifactOrdinal + 1
                IdempotencyKey.operation "artifact" artifactOrdinal turnKey
          GetArtifacts = fun () -> artifacts |> Seq.toList
          GetGrantedResources = fun () -> []
          RequestPermission = fun _ _ _ -> Task.FromResult false
          RequestModelEligibility = fun _ -> Task.FromResult false
          PublishArtifact =
            fun artifact ->
                artifacts.Add artifact
                Task.CompletedTask
          Authorization = Some authorization }

    let harness audit =
        { ToolProtocol = None
          ProcessEnvironment = None
          ContainerEnvironment = None
          ExecutionJournal = None
          ReplayFixtures = None
          Lifecycle = []
          Tracer = None
          Metrics = None
          Resilience = ResilienceConfig.NoResilience
          ReadinessChecks = []
          TraceStore = None
          Judge = None
          Constitution = None
          ConstitutionRemediationBroker = ConstitutionRemediationBroker.unavailable
          AuditLog = audit
          PolicyEngine = None
          PolicyConfirmationBroker = PolicyConfirmationBroker.unavailable
          Bus = EventBus.none }

module private LocalAgent =
    let runAsync () =
        task {
            let agent =
                Agent.create
                    "local-greeter"
                    "Local greeter"
                    "Returns a deterministic greeting."
                    0
                    [ "Return a greeting without external dependencies." ]
                    AgentContract.Text
                    (fun _ name -> Task.FromResult $"Hello, {name}.")

            let context = Host.context ()

            let request =
                ExecutionRequest.create
                    Host.authorization
                    (TurnId.parse context.TurnId)
                    "example-conversation"
                    agent.Metadata.Id
                    "Nao"
                    (SandboxConfig.Restricted(ResourceLimits.Constrained 10 5 1024))
                    Map.empty
                    Map.empty
                    context.Correlation

            let! result = EtclovgHarness.runAsync (Host.harness None) context agent request CancellationToken.None

            match result.Status, result.Outputs.Response with
            | ExecutionTerminalStatus.Succeeded, Some "Hello, Nao." ->
                printfn "local-agent: governed execution succeeded"
            | status, response -> invalidOp $"Unexpected local agent result: {status}, {response}"
        }

module private GovernedTool =
    let runAsync () =
        task {
            let mutable approvalObserved = false

            let broker =
                PermissionApprovalBroker.create (TimeSpan.FromSeconds 1.0) (fun request _ ->
                    approvalObserved <- request.Access = ResourceAccess.tool "calculator"

                    Task.FromResult(
                        PermissionApproval.Approved
                            { Scope = PermissionGrantScope.Operation
                              ExpiresAt = None }
                    ))

            let request =
                { Authorization = Host.authorization
                  Correlation = CorrelationContext.root ()
                  Access = ResourceAccess.tool "calculator"
                  Reason = "Evaluate a deterministic arithmetic expression."
                  ForceConfirmation = true
                  RequestedAt = DateTimeOffset.UtcNow
                  AllowedGrantScopes = Set.singleton PermissionGrantScope.Operation }

            let! approval = PermissionGate.resolve broker request CancellationToken.None

            match approval with
            | PermissionApproval.Approved _ when approvalObserved -> ()
            | _ -> invalidOp "Governed tool approval was not recorded."

            let audit = InMemoryAuditLog.create ()

            let evidence =
                AuditLog.toolInvocation
                    "governed-tool-agent"
                    "calculator"
                    "sha256:input"
                    "sha256:output"
                    true
                    PermissionDecision.Allow
                    None

            do! audit.RecordAsync Host.authorization evidence

            let! entries =
                audit.QueryAsync Host.authorization "governed-tool-agent" (DateTimeOffset.UtcNow.AddMinutes -1.0)

            if entries.Length <> 1 || not entries.Head.Permitted then
                invalidOp "Governed tool audit evidence is incomplete."

            printfn "governed-tool: approval and audit succeeded"
        }

module private GroundedRag =
    let runAsync () =
        task {
            let now = DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero)
            let lifecycle = KnowledgeLifecycle.active None false
            let text = "Nao requires citations for material grounded claims."
            let contentHash = ContentHash.computeText text
            let sourceId = KnowledgeSourceId.parse "example-source"
            let documentId = KnowledgeDocumentId.parse "example-document"
            let sourceUri = Uri "https://example.invalid/nao"

            let trust =
                TrustAssessment.create KnowledgeTrust.OrganizationVerified "repository example" now

            let source =
                KnowledgeSource.create
                    sourceId
                    sourceUri
                    (AuthorizationScope.identity Host.authorization)
                    DataClassification.Internal
                    trust
                    Map.empty
                    now
                    lifecycle

            let document =
                KnowledgeDocument.create documentId sourceId sourceUri Map.empty now lifecycle

            let version =
                KnowledgeDocumentVersion.create
                    documentId
                    sourceId
                    1
                    contentHash
                    (SourceAcquisition.create (ComponentVersion.create "example-source" "1") contentHash now Map.empty)
                    "text/plain"
                    (Some "en")
                    (int64 text.Length)
                    None
                    now
                    DataClassification.Internal
                    trust
                    Map.empty
                    lifecycle

            let location =
                SourceLocation.create 0L (int64 text.Length) 0 text.Length None (Some 1) (Some 1) None

            let contentUnit =
                ContentUnit.create
                    version.Id
                    0
                    ContentUnitKind.Paragraph
                    text
                    location
                    (ComponentVersion.create "example-parser" "1")
                    []
                    Map.empty
                    lifecycle

            let chunk =
                KnowledgeChunk.create
                    version.Id
                    0
                    KnowledgeChunkRole.Leaf
                    None
                    [ contentUnit.Id ]
                    text
                    [ location ]
                    (ComponentVersion.create "example-chunker" "1")
                    (ContentHash.computeText "example-chunker-configuration")
                    []
                    Map.empty
                    lifecycle

            let indexEntry = KnowledgeIndexEntry.create "example-index" chunk None now []

            let catalog = KnowledgeCatalog.inMemory ()

            do!
                catalog.SaveVersionAsync
                    Host.authorization
                    { Source = source
                      Document = document
                      Version = version
                      ContentUnits = [ contentUnit ]
                      Chunks = [ chunk ]
                      IndexEntries = [ indexEntry ] }
                    CancellationToken.None

            let! snapshot = catalog.SnapshotAsync Host.authorization CancellationToken.None

            if snapshot.Chunks.Length <> 1 then
                invalidOp "Knowledge ingestion did not commit the expected chunk."

            let namespaceName = KnowledgeIndexNamespace.parse "example-primary"

            let indexedDocument =
                KnowledgeIndexDocument.create namespaceName KnowledgeIndexAccess.ownerOnly source version chunk now

            let lexicalIndex: KnowledgeLexicalIndex =
                { ApplyAsync =
                    fun _ mutation _ ->
                        Task.FromResult(
                            KnowledgeIndexMutationResult.create
                                (KnowledgeIndexMutation.namespaceName mutation)
                                0
                                0
                                0
                                now
                        )
                  QueryAsync =
                    fun _ query _ ->
                        if
                            not (
                                LexicalIndexQuery.query query
                                |> _.Contains("Nao", StringComparison.OrdinalIgnoreCase)
                            )
                        then
                            invalidOp "Unexpected normalized retrieval query."

                        Task.FromResult [ LexicalIndexHit.create indexedDocument 1.0 ] }

            let retrievalRequest =
                KnowledgeRetrievalRequest.create
                    "What does Nao require?"
                    Host.authorization
                    namespaceName
                    (KnowledgeIndexFilter.create [ sourceId ] [] Map.empty None None None None)
                    KnowledgeTaskIntent.KnowledgeSeeking
                    KnowledgeRetrievalStrategy.Lexical
                    KnowledgeQueryTransformationStrategy.None
                    KnowledgeRetrievalLimits.defaultLimits
                    KnowledgeRetrievalBudget.deterministic
                |> KnowledgeQueryNormalizer.normalize

            let! understanding = KnowledgeQueryUnderstanding.planAsync None retrievalRequest CancellationToken.None

            let! retrieval =
                KnowledgeLexicalRetrievalExecutor.executeAsync lexicalIndex understanding CancellationToken.None

            match retrieval with
            | KnowledgeLexicalRetrievalResult.Retrieved value when KnowledgeLexicalRetrieval.candidateCount value = 1 ->
                ()
            | _ -> invalidOp "Grounded RAG retrieval did not return the ingested chunk."

            let citationId = KnowledgeGroundedCitationReferenceId.parse "example-citation"

            let citation =
                KnowledgeGroundedCitationReference.create
                    citationId
                    $"[nao-citation:{KnowledgeChunkId.value chunk.Id}]"
                    chunk.Id

            let answer = "Nao requires citations."

            let claim =
                KnowledgeGroundedClaim.create
                    answer
                    (KnowledgeGroundedClaimId.parse "example-claim")
                    0
                    answer.Length
                    KnowledgeGroundedClaimPresentation.GeneratedInterpretation
                    [ citationId ]
                    (Confidence.create 1.0)

            let proposal =
                KnowledgeGroundedAnswerProposal.create answer [ claim ] [ citation ] (Confidence.create 1.0)

            if KnowledgeGroundedAnswerProposal.citationReferences proposal |> List.length <> 1 then
                invalidOp "Grounded answer did not preserve its citation."

            printfn "grounded-rag: ingestion, retrieval, and cited answer succeeded"
        }

type private ExampleAdministrationTransport() =
    interface IRuntimeAdministrationTransport with
        member _.PreviewAsync(_, _, request, _) =
            AdministrativeImpactPreview(
                Summary = "validated by example host transport",
                CurrentRevision = request.ExpectedRevision,
                TargetRevision = request.ExpectedRevision + 1L
            )
            |> Task.FromResult

        member _.MutateAsync(_, _, request, _) =
            AdministrativeMutationResult(OperationId = request.OperationId, Revision = request.ExpectedRevision + 1L)
            |> Task.FromResult

        member _.GetResourceAsync(_, _, resourceKind, resourceId, _) =
            AdministrativeResourceSnapshot(ResourceKind = resourceKind, ResourceId = resourceId)
            |> Task.FromResult

module private DistributedOrleans =
    let runAsync () =
        task {
            let principal = AuthorizationScope.principal Host.authorization

            let client =
                RuntimeAdministrationClient(
                    principal,
                    AuthorizationScope.tenantId Host.authorization,
                    ExampleAdministrationTransport()
                )

            let change =
                AdministrativeChange.create
                    (Guid.NewGuid())
                    AdministrativeResourceKind.Provider
                    "example-provider"
                    "reload"
                    "activate the reviewed example configuration"
                    0L
                    (Map.ofList [ "configuration_revision", "providers-2" ])

            let! preview = client.PreviewAsync(change, CancellationToken.None)

            if preview.TargetRevision <> 1L then
                invalidOp "Orleans administration preview returned an unexpected revision."

            let! applied = client.ApplyAsync(change, CancellationToken.None)

            if applied.Revision <> preview.TargetRevision then
                invalidOp "Orleans administration mutation did not match its preview."

            printfn "orleans: authenticated preview and mutation succeeded"
        }

module private ReplayEvaluation =
    let componentIdentity name =
        EvalComponentIdentity.create name "1" (name + "-configuration")

    let runAsync () =
        task {
            let agent =
                Agent.create
                    "replay-agent"
                    "Replay agent"
                    "Returns deterministic recorded-safe output."
                    0
                    []
                    AgentContract.Text
                    (fun _ input -> Task.FromResult("answer:" + input))

            let specification =
                EvalExecutionSpecification.create
                    (Map [ agent.Metadata.Id, componentIdentity agent.Metadata.Id ])
                    (componentIdentity "prompt")
                    (componentIdentity "response-protocol")
                    []
                    (Some(componentIdentity "provider"))
                    (Some(componentIdentity "model"))
                    (componentIdentity "harness")
                    []
                    []

            let fixtures = EvalRecordedFixtureSet.create "nao-replay-v1" ReplayFixtures.empty

            let timestamp = DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero)

            let execution =
                EvalExecutionConfig.create
                    Host.authorization
                    Host.context
                    (Host.harness None)
                    (SandboxConfig.Restricted(ResourceLimits.Constrained 10 5 1024))
                    specification
                    (EvalExecutionMode.RecordedReplay fixtures)
                    (EvalRuntime.deterministic 42 timestamp)

            let dataset =
                EvalDataset.createAt
                    (Guid.Parse "6ff259f6-f7f0-45f8-b076-24b17b511c51")
                    (AuthorizationScope.identity Host.authorization)
                    timestamp
                    "example-replay"
                    1
                    [ EvalCase.repository "case-1" "input" "answer:input" ]
                    []

            let! report =
                EvalRunner.runDatasetAsync
                    (EvalRunnerConfig.create execution)
                    ExactMatch.evaluator
                    agent
                    dataset
                    CancellationToken.None

            if report.Results |> List.exists (fun result -> result.Verdict <> EvalVerdict.Pass) then
                invalidOp "Recorded replay evaluation gate failed."

            printfn "replay-evaluation: deterministic gate passed"
        }

module private FormalReasoning =
    let runAsync () =
        task {
            let symbol name =
                { Name = name
                  Type = DatalogType.Symbol }

            let variable name = DatalogTerm.Variable name

            let atom relation names =
                { Relation = relation
                  Terms = names |> List.map variable }

            let citation id =
                ReasoningCitation.create $"example:{id}" "rules-v1" None

            let relations =
                [ { Name = "edge"
                    Columns = [ symbol "source"; symbol "target" ]
                    Kind = DatalogRelationKind.Extensional }
                  { Name = "path"
                    Columns = [ symbol "source"; symbol "target" ]
                    Kind = DatalogRelationKind.Derived } ]

            let facts =
                [ { Id = "edge-a-b"
                    Relation = "edge"
                    Values = [ DatalogValue.Symbol "a"; DatalogValue.Symbol "b" ]
                    Citations = [ citation "edge-a-b" ] } ]

            let rules =
                [ { Id = "path-base"
                    Head = atom "path" [ "x"; "y" ]
                    Body = [ DatalogLiteral.Positive(atom "edge" [ "x"; "y" ]) ]
                    Citations = [ citation "path-base" ] } ]

            let program =
                DatalogValidation.create
                    Host.authorization
                    { Id = "example-reachability"
                      Version = "1"
                      Digest = String('a', 64) }
                    relations
                    facts
                    rules
                    { Atom = atom "path" [ "source"; "target" ]
                      OutputVariables = [ "source"; "target" ] }

            let runner =
                SouffleRunner.create (fun _ _ ->
                    Task.FromResult
                        { EngineVersion = "example-2.5.0"
                          Rows =
                            [ { Binding =
                                  Map.ofList [ "source", DatalogValue.Symbol "a"; "target", DatalogValue.Symbol "b" ]
                                Derivations =
                                  [ { RuleId = "path-base"
                                      PremiseFactIds = [ "edge-a-b" ] } ] } ]
                          ResponseBytes = 64
                          PeakMemoryBytes = 1024L
                          Steps = 1L
                          TimedOut = false
                          Failure = None })

            let execute =
                SouffleEndpoint.create "example-2.5.0" 10 TimeProvider.System [] runner

            let limits =
                ReasoningLimits.create (TimeSpan.FromSeconds 2.0) 10_000_000L 10_000L 100 10 10 10_000 0

            let! result = execute Host.authorization program limits CancellationToken.None

            match result.Outcome with
            | DatalogExecutionOutcome.Completed [ row ] when row.Binding.["target"] = DatalogValue.Symbol "b" ->
                printfn "formal-reasoning: cited deterministic conclusion succeeded"
            | outcome -> invalidOp $"Unexpected reasoning outcome: {outcome}"
        }

module Program =
    let private examples =
        Map.ofList
            [ "local-agent", LocalAgent.runAsync
              "governed-tool", GovernedTool.runAsync
              "grounded-rag", GroundedRag.runAsync
              "orleans", DistributedOrleans.runAsync
              "replay-evaluation", ReplayEvaluation.runAsync
              "formal-reasoning", FormalReasoning.runAsync ]

    let private runAllAsync () =
        task {
            for name, run in examples |> Map.toList do
                printfn "running %s" name
                do! run ()
        }

    [<EntryPoint>]
    let main arguments =
        let execution =
            match arguments |> Array.toList with
            | [ "all" ] -> runAllAsync ()
            | [ name ] ->
                examples
                |> Map.tryFind name
                |> Option.defaultWith (fun () -> invalidArg (nameof arguments) $"Unknown example '{name}'.")
                |> fun run -> run ()
            | _ ->
                let names = examples |> Map.toList |> List.map fst |> String.concat ", "

                invalidArg (nameof arguments) $"Expected one of: all, {names}."

        execution.GetAwaiter().GetResult()
        0
