namespace Nao.Agents

open System
open System.IO
open System.Threading
open System.Threading.Tasks

[<AutoOpen>]
module NaoTestFixtures =

    let repositoryRoot () =
        let rec find (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "Nao.slnx")) then
                directory.FullName
            elif isNull directory.Parent then
                invalidOp $"Cannot locate Nao.slnx above test output directory '{AppContext.BaseDirectory}'."
            else
                find directory.Parent

        find (DirectoryInfo AppContext.BaseDirectory)

    type AgentContext with
        static member unrestrictedForTests() =
            { Correlation = CorrelationContext.root ()
              SessionKey = ""
              TurnId = ""
              ExecutionBoundary = ExecutionBoundary.Unrestricted
              CancellationToken = CancellationToken.None
              NextToolCallKey = (fun () -> invalidOp "Tool identity requires a governed execution context.")
              NextArtifactKey = (fun () -> invalidOp "Artifact identity requires a governed execution context.")
              GetArtifacts = (fun () -> [])
              GetGrantedResources = (fun () -> [])
              RequestPermission = (fun _ _ _ -> Task.FromResult true)
              RequestModelEligibility = (fun _ -> Task.FromResult true)
              PublishArtifact = (fun _ -> Task.CompletedTask)
              Authorization = None }

    type HarnessServices with
        static member none =
            { Tracer = None
              Metrics = None
              ExecutionJournal = None
              TraceStore = None
              AuditLog = None }

    type EtclovgConfig with
        static member Default =
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
              AuditLog = None
              PolicyEngine = None
              PolicyConfirmationBroker = PolicyConfirmationBroker.unavailable
              Bus = EventBus.none }
