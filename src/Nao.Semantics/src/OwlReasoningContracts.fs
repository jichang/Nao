namespace Nao.Semantics

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type OwlProfile =
    | Owl2EL
    | Owl2QL
    | Owl2RL
    | Owl2DL

[<RequireQualifiedAccess>]
type OwlReasoningOperationKind =
    | CheckConsistency
    | Classify
    | Realize
    | CheckEntailment

[<RequireQualifiedAccess>]
type OwlProofCapability =
    | None
    | Opaque
    | DerivationGraph

[<RequireQualifiedAccess>]
type OwlReasonerExecutionMode =
    | IsolatedProcess
    | RemoteService

type OwlReasonerCapabilities =
    private
        { EngineId: string
          EngineVersion: string
          Profiles: Set<OwlProfile>
          Operations: Set<OwlReasoningOperationKind>
          ProofCapability: OwlProofCapability
          ExecutionMode: OwlReasonerExecutionMode }

[<RequireQualifiedAccess>]
module OwlReasonerCapabilities =
    let private canonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"{name} must be canonical and non-blank."

    let create engineId engineVersion profiles operations proofCapability executionMode =
        canonical (nameof engineId) engineId
        canonical (nameof engineVersion) engineVersion

        if Set.isEmpty profiles then
            invalidArg (nameof profiles) "OWL reasoner must declare at least one exactly supported profile."

        if Set.isEmpty operations then
            invalidArg (nameof operations) "OWL reasoner must declare at least one supported operation."

        { EngineId = engineId
          EngineVersion = engineVersion
          Profiles = profiles
          Operations = operations
          ProofCapability = proofCapability
          ExecutionMode = executionMode }

    let engineId value = value.EngineId
    let engineVersion value = value.EngineVersion
    let profiles value = value.Profiles
    let operations value = value.Operations
    let proofCapability value = value.ProofCapability
    let executionMode value = value.ExecutionMode

[<RequireQualifiedAccess>]
type OwlWorldAssumption = | OpenWorld

[<RequireQualifiedAccess>]
type OwlIndividualIdentityAssumption = | NoUniqueName

type OwlSemanticAssumptions =
    { World: OwlWorldAssumption
      IndividualIdentity: OwlIndividualIdentityAssumption }

    static member Standard =
        { World = OwlWorldAssumption.OpenWorld
          IndividualIdentity = OwlIndividualIdentityAssumption.NoUniqueName }

type OwlImportIdentity =
    private
        { VersionIri: RdfIri
          ContentDigest: string }

[<RequireQualifiedAccess>]
module OwlImportIdentity =
    let create versionIri (contentDigest: string) =
        if
            isNull contentDigest
            || contentDigest.Length <> 64
            || contentDigest
               |> Seq.exists (fun character ->
                   not (character >= '0' && character <= '9' || character >= 'a' && character <= 'f'))
        then
            invalidArg (nameof contentDigest) "OWL import digest must be lowercase SHA-256."

        { VersionIri = versionIri
          ContentDigest = contentDigest }

    let versionIri value = value.VersionIri
    let contentDigest value = value.ContentDigest

type OwlOntologyIdentity =
    private
        { OntologyIri: RdfIri
          VersionIri: RdfIri
          DatasetId: string
          DatasetRevision: int64
          ContentDigest: string
          Imports: Map<RdfIri, OwlImportIdentity> }

[<RequireQualifiedAccess>]
module OwlOntologyIdentity =
    let private canonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"{name} must be canonical and non-blank."

    let private digest (value: string) =
        if
            isNull value
            || value.Length <> 64
            || value
               |> Seq.exists (fun character ->
                   not (character >= '0' && character <= '9' || character >= 'a' && character <= 'f'))
        then
            invalidArg (nameof value) "OWL identity digest must be 64 lowercase hexadecimal characters."

    let create descriptor datasetId datasetRevision contentDigest imports =
        canonical (nameof datasetId) datasetId

        if datasetRevision <= 0L then
            invalidArg (nameof datasetRevision) "OWL ontology dataset revision must be positive."

        digest contentDigest

        let declaredImports = OntologyDescriptor.imports descriptor
        let versionedImports = imports |> Map.keys |> Set.ofSeq

        if declaredImports <> versionedImports then
            invalidArg
                (nameof imports)
                "Every declared ontology import must have exactly one immutable version identity."

        { OntologyIri = OntologyDescriptor.ontologyIri descriptor
          VersionIri = OntologyDescriptor.versionIri descriptor
          DatasetId = datasetId
          DatasetRevision = datasetRevision
          ContentDigest = contentDigest
          Imports = imports }

    let ontologyIri value = value.OntologyIri
    let versionIri value = value.VersionIri
    let datasetId value = value.DatasetId
    let datasetRevision value = value.DatasetRevision
    let contentDigest value = value.ContentDigest
    let imports value = value.Imports

type OwlReasonerConfiguration =
    private
        { Id: string
          Version: string
          Digest: string
          Settings: Map<string, string> }

[<RequireQualifiedAccess>]
module OwlReasonerConfiguration =
    let create id version (digest: string) settings =
        let canonical name (value: string) =
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg name $"{name} must be canonical and non-blank."

        canonical (nameof id) id
        canonical (nameof version) version

        if
            isNull digest
            || digest.Length <> 64
            || digest
               |> Seq.exists (fun character ->
                   not (character >= '0' && character <= '9' || character >= 'a' && character <= 'f'))
        then
            invalidArg (nameof digest) "OWL reasoner configuration digest must be lowercase SHA-256."

        settings
        |> Map.iter (fun key value ->
            canonical (nameof settings) key
            canonical (nameof settings) value)

        { Id = id
          Version = version
          Digest = digest
          Settings = settings }

    let id value = value.Id
    let version value = value.Version
    let digest value = value.Digest
    let settings value = value.Settings

type OwlReasoningBudget =
    private
        { Timeout: TimeSpan
          MaxInputStatements: int
          MaxOutputStatements: int
          MaxProofSteps: int
          MaxResponseBytes: int }

[<RequireQualifiedAccess>]
module OwlReasoningBudget =
    let create timeout maxInputStatements maxOutputStatements maxProofSteps maxResponseBytes =
        if timeout < TimeSpan.FromMilliseconds 1.0 then
            invalidArg (nameof timeout) "OWL reasoning timeout must be at least one millisecond."

        [ nameof maxInputStatements, maxInputStatements
          nameof maxOutputStatements, maxOutputStatements
          nameof maxProofSteps, maxProofSteps
          nameof maxResponseBytes, maxResponseBytes ]
        |> List.iter (fun (name, value) ->
            if value <= 0 then
                invalidArg name $"{name} must be positive.")

        { Timeout = timeout
          MaxInputStatements = maxInputStatements
          MaxOutputStatements = maxOutputStatements
          MaxProofSteps = maxProofSteps
          MaxResponseBytes = maxResponseBytes }

    let timeout value = value.Timeout
    let maxInputStatements value = value.MaxInputStatements
    let maxOutputStatements value = value.MaxOutputStatements
    let maxProofSteps value = value.MaxProofSteps
    let maxResponseBytes value = value.MaxResponseBytes

[<RequireQualifiedAccess>]
type OwlReasoningOperation =
    | CheckConsistency
    | Classify
    | Realize
    | CheckEntailment of RdfResource * RdfIri * RdfTerm

[<RequireQualifiedAccess>]
module OwlReasoningOperation =
    let kind =
        function
        | OwlReasoningOperation.CheckConsistency -> OwlReasoningOperationKind.CheckConsistency
        | OwlReasoningOperation.Classify -> OwlReasoningOperationKind.Classify
        | OwlReasoningOperation.Realize -> OwlReasoningOperationKind.Realize
        | OwlReasoningOperation.CheckEntailment _ -> OwlReasoningOperationKind.CheckEntailment

type OwlReasoningRequest =
    private
        { Profile: OwlProfile
          Assumptions: OwlSemanticAssumptions
          Ontology: OwlOntologyIdentity
          Configuration: OwlReasonerConfiguration
          Operation: OwlReasoningOperation
          Budget: OwlReasoningBudget }

[<RequireQualifiedAccess>]
module OwlReasoningRequest =
    let create profile ontology configuration operation budget =
        { Profile = profile
          Assumptions = OwlSemanticAssumptions.Standard
          Ontology = ontology
          Configuration = configuration
          Operation = operation
          Budget = budget }

    let profile value = value.Profile
    let assumptions value = value.Assumptions
    let ontology value = value.Ontology
    let configuration value = value.Configuration
    let operation value = value.Operation
    let budget value = value.Budget

type OwlReasoningCacheKey =
    private
        { EngineId: string
          EngineVersion: string
          Profile: OwlProfile
          OntologyVersionIri: RdfIri
          OntologyContentDigest: string
          Imports: Map<RdfIri, OwlImportIdentity>
          ConfigurationDigest: string
          Operation: OwlReasoningOperation }

[<RequireQualifiedAccess>]
module OwlReasoningCacheKey =
    let create capabilities request =
        let ontology = OwlReasoningRequest.ontology request
        let configuration = OwlReasoningRequest.configuration request

        { EngineId = OwlReasonerCapabilities.engineId capabilities
          EngineVersion = OwlReasonerCapabilities.engineVersion capabilities
          Profile = OwlReasoningRequest.profile request
          OntologyVersionIri = OwlOntologyIdentity.versionIri ontology
          OntologyContentDigest = OwlOntologyIdentity.contentDigest ontology
          Imports = OwlOntologyIdentity.imports ontology
          ConfigurationDigest = OwlReasonerConfiguration.digest configuration
          Operation = OwlReasoningRequest.operation request }

    let engineId value = value.EngineId
    let engineVersion value = value.EngineVersion
    let profile value = value.Profile
    let ontologyVersionIri value = value.OntologyVersionIri
    let ontologyContentDigest value = value.OntologyContentDigest
    let imports value = value.Imports
    let configurationDigest value = value.ConfigurationDigest
    let operation value = value.Operation

type OwlProofStep =
    { Id: string
      RuleOrAxiomId: string
      PremiseIds: string list
      Conclusion: RdfStatement }

type OwlProof =
    { Steps: OwlProofStep list
      RootStepIds: string list }

[<RequireQualifiedAccess>]
type OwlConsistencyResult =
    | Consistent
    | Inconsistent of proof: OwlProof option

[<RequireQualifiedAccess>]
type OwlEntailmentResult =
    | Entailed of proof: OwlProof option
    | Disentailed of proof: OwlProof option
    | NotEntailed
    | Unknown of reason: string

[<RequireQualifiedAccess>]
type OwlReasoningResult =
    | Consistency of OwlConsistencyResult
    | Classification of RdfStatement list
    | Realization of RdfStatement list
    | Entailment of OwlEntailmentResult

[<RequireQualifiedAccess>]
type OwlReasoningOutcome =
    | Completed of OwlReasoningResult
    | Unsupported of operation: OwlReasoningOperationKind * detail: string
    | TimedOut
    | InputLimitExceeded of limit: int * observed: int
    | OutputLimitExceeded of limit: int * observedAtLeast: int
    | ProofLimitExceeded of limit: int * observedAtLeast: int
    | ResponseSizeLimitExceeded of limitBytes: int * observedBytes: int
    | Failed of code: string * detail: string

type OwlReasoningProvenance =
    { ExecutionId: Guid
      EngineId: string
      EngineVersion: string
      Profile: OwlProfile
      Assumptions: OwlSemanticAssumptions
      Operation: OwlReasoningOperationKind
      ConfigurationId: string
      ConfigurationVersion: string
      ConfigurationDigest: string
      OntologyDatasetId: string
      OntologyDatasetRevision: int64
      OntologyVersionIri: RdfIri
      OntologyContentDigest: string
      Imports: Map<RdfIri, OwlImportIdentity>
      StartedAt: DateTimeOffset
      Duration: TimeSpan
      CacheHit: bool }

type OwlReasoningExecution =
    { Provenance: OwlReasoningProvenance
      Outcome: OwlReasoningOutcome }

type OwlReasoningCache =
    { TryGetAsync: OwlReasoningCacheKey -> CancellationToken -> Task<OwlReasoningResult option>
      StoreAsync: OwlReasoningCacheKey -> OwlReasoningResult -> CancellationToken -> Task }

[<RequireQualifiedAccess>]
module OwlReasoningCache =
    let disabled =
        { TryGetAsync = fun _ _ -> Task.FromResult None
          StoreAsync = fun _ _ _ -> Task.CompletedTask }

type OwlReasoningEndpoint =
    { Capabilities: OwlReasonerCapabilities
      ExecuteAsync:
          AuthorizationScope -> RdfDataset -> OwlReasoningRequest -> CancellationToken -> Task<OwlReasoningExecution> }
