namespace Nao.Reasoning

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Nao.Agents

module private Contract =
    let canonical name (value: string) =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"{name} must be canonical and non-blank."

    let digest (value: string) =
        if
            isNull value
            || value.Length <> 64
            || value
               |> Seq.exists (fun character ->
                   not (character >= '0' && character <= '9' || character >= 'a' && character <= 'f'))
        then
            invalidArg (nameof value) "Digest must be 64 lowercase hexadecimal characters."

    let sha256 (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

type ReasoningArtifact =
    private
        { MediaType: string
          Content: string
          Digest: string }

[<RequireQualifiedAccess>]
module ReasoningArtifact =
    let create mediaType content =
        Contract.canonical (nameof mediaType) mediaType

        if isNull content then
            nullArg (nameof content)

        { MediaType = mediaType
          Content = content
          Digest = Contract.sha256 content }

    let mediaType value = value.MediaType
    let content value = value.Content
    let digest value = value.Digest

type ReasoningCitation =
    private
        { Address: string
          SourceVersionId: string
          Fragment: string option }

[<RequireQualifiedAccess>]
module ReasoningCitation =
    let create address sourceVersionId fragment =
        Contract.canonical (nameof address) address
        Contract.canonical (nameof sourceVersionId) sourceVersionId
        fragment |> Option.iter (Contract.canonical (nameof fragment))

        { Address = address
          SourceVersionId = sourceVersionId
          Fragment = fragment }

    let address value = value.Address
    let sourceVersionId value = value.SourceVersionId
    let fragment value = value.Fragment

type ReasoningAssertion =
    private
        { Id: string
          Artifact: ReasoningArtifact
          Citations: ReasoningCitation list }

[<RequireQualifiedAccess>]
module ReasoningAssertion =
    let create id artifact citations =
        Contract.canonical (nameof id) id

        if List.isEmpty citations then
            invalidArg (nameof citations) "Reasoning assertions require source citations."

        { Id = id
          Artifact = artifact
          Citations = citations }

    let id value = value.Id
    let artifact value = value.Artifact
    let citations value = value.Citations

[<RequireQualifiedAccess>]
type ReasoningWorldAssumption =
    | OpenWorld
    | ClosedWorld

[<RequireQualifiedAccess>]
type ReasoningNegationSemantics =
    | NoNegation
    | Classical
    | Explicit
    | NegationAsFailure

[<RequireQualifiedAccess>]
type ReasoningMonotonicity =
    | Monotonic
    | NonMonotonic

type ReasoningSemanticMode =
    private
        { World: ReasoningWorldAssumption
          Negation: ReasoningNegationSemantics
          Monotonicity: ReasoningMonotonicity
          IdentityAssumptions: Set<string>
          EngineAssumptions: Map<string, string> }

[<RequireQualifiedAccess>]
module ReasoningSemanticMode =
    let create world negation monotonicity identityAssumptions engineAssumptions =
        identityAssumptions
        |> Set.iter (Contract.canonical (nameof identityAssumptions))

        engineAssumptions
        |> Map.iter (fun key value ->
            Contract.canonical (nameof engineAssumptions) key
            Contract.canonical (nameof engineAssumptions) value)

        { World = world
          Negation = negation
          Monotonicity = monotonicity
          IdentityAssumptions = identityAssumptions
          EngineAssumptions = engineAssumptions }

    let world value = value.World
    let negation value = value.Negation
    let monotonicity value = value.Monotonicity
    let identityAssumptions value = value.IdentityAssumptions
    let engineAssumptions value = value.EngineAssumptions

type ReasoningInputIdentity =
    private
        { Kind: string
          Id: string
          Version: string
          Digest: string }

[<RequireQualifiedAccess>]
module ReasoningInputIdentity =
    let create kind id version digest =
        Contract.canonical (nameof kind) kind
        Contract.canonical (nameof id) id
        Contract.canonical (nameof version) version
        Contract.digest digest

        { Kind = kind
          Id = id
          Version = version
          Digest = digest }

    let kind value = value.Kind
    let id value = value.Id
    let version value = value.Version
    let digest value = value.Digest

[<RequireQualifiedAccess>]
type ReasoningExecutionMode =
    | IsolatedProcess
    | RemoteService

type ReasoningEngineRequirements =
    private
        { EngineId: string option
          RequiredCapabilities: Set<string>
          AllowedExecutionModes: Set<ReasoningExecutionMode>
          RequireProof: bool
          RequireUnsatCore: bool }

[<RequireQualifiedAccess>]
module ReasoningEngineRequirements =
    let create engineId requiredCapabilities allowedExecutionModes requireProof requireUnsatCore =
        engineId |> Option.iter (Contract.canonical (nameof engineId))

        requiredCapabilities
        |> Set.iter (Contract.canonical (nameof requiredCapabilities))

        if Set.isEmpty allowedExecutionModes then
            invalidArg (nameof allowedExecutionModes) "At least one isolated reasoning execution mode is required."

        { EngineId = engineId
          RequiredCapabilities = requiredCapabilities
          AllowedExecutionModes = allowedExecutionModes
          RequireProof = requireProof
          RequireUnsatCore = requireUnsatCore }

    let engineId value = value.EngineId
    let requiredCapabilities value = value.RequiredCapabilities
    let allowedExecutionModes value = value.AllowedExecutionModes
    let requireProof value = value.RequireProof
    let requireUnsatCore value = value.RequireUnsatCore

type ReasoningLimits =
    private
        { Timeout: TimeSpan
          MaxMemoryBytes: int64
          MaxSteps: int64
          MaxAssertions: int
          MaxConclusions: int
          MaxProofSteps: int
          MaxResponseBytes: int
          MaxExternalCalls: int }

[<RequireQualifiedAccess>]
module ReasoningLimits =
    let create
        timeout
        maxMemoryBytes
        maxSteps
        maxAssertions
        maxConclusions
        maxProofSteps
        maxResponseBytes
        maxExternalCalls
        =
        if timeout < TimeSpan.FromMilliseconds 1.0 then
            invalidArg (nameof timeout) "Reasoning timeout must be at least one millisecond."

        if maxMemoryBytes <= 0L then
            invalidArg (nameof maxMemoryBytes) "Reasoning memory limit must be positive."

        if maxSteps <= 0L then
            invalidArg (nameof maxSteps) "Reasoning step limit must be positive."

        [ nameof maxAssertions, maxAssertions
          nameof maxConclusions, maxConclusions
          nameof maxProofSteps, maxProofSteps
          nameof maxResponseBytes, maxResponseBytes ]
        |> List.iter (fun (name, value) ->
            if value <= 0 then
                invalidArg name $"{name} must be positive.")

        if maxExternalCalls < 0 then
            invalidArg (nameof maxExternalCalls) "Reasoning external-call limit cannot be negative."

        { Timeout = timeout
          MaxMemoryBytes = maxMemoryBytes
          MaxSteps = maxSteps
          MaxAssertions = maxAssertions
          MaxConclusions = maxConclusions
          MaxProofSteps = maxProofSteps
          MaxResponseBytes = maxResponseBytes
          MaxExternalCalls = maxExternalCalls }

    let timeout value = value.Timeout
    let maxMemoryBytes value = value.MaxMemoryBytes
    let maxSteps value = value.MaxSteps
    let maxAssertions value = value.MaxAssertions
    let maxConclusions value = value.MaxConclusions
    let maxProofSteps value = value.MaxProofSteps
    let maxResponseBytes value = value.MaxResponseBytes
    let maxExternalCalls value = value.MaxExternalCalls

type ReasoningRequest =
    private
        { Owner: AuthorizationIdentity
          Id: Guid
          Assertions: ReasoningAssertion list
          SemanticMode: ReasoningSemanticMode
          Query: ReasoningArtifact
          EngineRequirements: ReasoningEngineRequirements
          Limits: ReasoningLimits
          PinnedInputs: ReasoningInputIdentity list }

[<RequireQualifiedAccess>]
module ReasoningRequest =
    let create authorization id assertions semanticMode query engineRequirements limits pinnedInputs =
        if id = Guid.Empty then
            invalidArg (nameof id) "Reasoning request identity cannot be empty."

        let assertionIds = assertions |> List.map ReasoningAssertion.id

        if assertionIds.Length <> (assertionIds |> List.distinct |> List.length) then
            invalidArg (nameof assertions) "Reasoning assertion identities must be unique."

        if assertions.Length > ReasoningLimits.maxAssertions limits then
            invalidArg (nameof assertions) "Reasoning assertions exceed the request limit."

        if List.isEmpty pinnedInputs then
            invalidArg (nameof pinnedInputs) "Reasoning requests require immutable pinned input identities."

        let inputKeys =
            pinnedInputs
            |> List.map (fun value ->
                ReasoningInputIdentity.kind value,
                ReasoningInputIdentity.id value,
                ReasoningInputIdentity.version value)

        if inputKeys.Length <> (inputKeys |> List.distinct |> List.length) then
            invalidArg (nameof pinnedInputs) "Reasoning pinned input identities must be unique."

        { Owner = AuthorizationScope.identity authorization
          Id = id
          Assertions = assertions
          SemanticMode = semanticMode
          Query = query
          EngineRequirements = engineRequirements
          Limits = limits
          PinnedInputs = pinnedInputs }

    let requireOwner authorization request =
        if request.Owner <> AuthorizationScope.identity authorization then
            raise (UnauthorizedAccessException "Reasoning request cannot cross authorization boundaries.")

        request

    let owner value = value.Owner
    let id value = value.Id
    let assertions value = value.Assertions
    let semanticMode value = value.SemanticMode
    let query value = value.Query
    let engineRequirements value = value.EngineRequirements
    let limits value = value.Limits
    let pinnedInputs value = value.PinnedInputs

type ReasoningEngineIdentity =
    private
        { Id: string
          Version: string
          ConfigurationDigest: string
          ExecutionMode: ReasoningExecutionMode
          Capabilities: Set<string> }

[<RequireQualifiedAccess>]
module ReasoningEngineIdentity =
    let create id version configurationDigest executionMode capabilities =
        Contract.canonical (nameof id) id
        Contract.canonical (nameof version) version
        Contract.digest configurationDigest
        capabilities |> Set.iter (Contract.canonical (nameof capabilities))

        { Id = id
          Version = version
          ConfigurationDigest = configurationDigest
          ExecutionMode = executionMode
          Capabilities = capabilities }

    let id value = value.Id
    let version value = value.Version
    let configurationDigest value = value.ConfigurationDigest
    let executionMode value = value.ExecutionMode
    let capabilities value = value.Capabilities

type ReasoningConclusion =
    { Artifact: ReasoningArtifact
      Citations: ReasoningCitation list }

type ReasoningProofStep =
    { Id: string
      RuleOrAxiomId: string
      PremiseIds: string list
      Conclusion: ReasoningArtifact }

type ReasoningProof =
    { Steps: ReasoningProofStep list
      RootStepIds: string list }

type ReasoningEvidence =
    { Proof: ReasoningProof option
      Explanation: ReasoningArtifact option
      UnsatCore: ReasoningAssertion list
      Citations: ReasoningCitation list }

    static member Empty =
        { Proof = None
          Explanation = None
          UnsatCore = []
          Citations = [] }

[<RequireQualifiedAccess>]
type ReasoningUnknownReason =
    | NotProven
    | Incomplete
    | EngineReturnedUnknown of detail: string

[<RequireQualifiedAccess>]
type ReasoningResourceLimit =
    | Inputs
    | Memory
    | Steps
    | Conclusions
    | ProofSteps
    | ResponseBytes
    | ExternalCalls

[<RequireQualifiedAccess>]
type ReasoningOutcome =
    | Proven of conclusions: ReasoningConclusion list * evidence: ReasoningEvidence
    | Disproven of evidence: ReasoningEvidence
    | Unknown of reason: ReasoningUnknownReason * evidence: ReasoningEvidence
    | Inconsistent of evidence: ReasoningEvidence
    | TimedOut
    | Unsupported of capability: string * detail: string
    | ResourceLimitExceeded of resource: ReasoningResourceLimit * limit: int64 * observedAtLeast: int64
    | Failed of code: string * detail: string

type ReasoningUsage =
    { WallTime: TimeSpan
      CpuTime: TimeSpan option
      PeakMemoryBytes: int64 option
      Steps: int64 option
      Conclusions: int
      ProofSteps: int
      ResponseBytes: int option
      ExternalCalls: int option }

type ReasoningAudit =
    { TraceId: Guid
      StartedAt: DateTimeOffset
      CompletedAt: DateTimeOffset
      CacheHit: bool }

type ReasoningResult =
    private
        { RequestId: Guid
          Engine: ReasoningEngineIdentity
          SemanticMode: ReasoningSemanticMode
          PinnedInputs: ReasoningInputIdentity list
          SourceAssertions: ReasoningAssertion list
          Outcome: ReasoningOutcome
          Usage: ReasoningUsage
          Audit: ReasoningAudit
          Artifacts: ReasoningArtifact list }

[<RequireQualifiedAccess>]
module ReasoningResult =
    let create request engine outcome usage audit artifacts =
        if audit.TraceId = Guid.Empty then
            invalidArg (nameof audit) "Reasoning audit trace identity cannot be empty."

        if audit.CompletedAt < audit.StartedAt then
            invalidArg (nameof audit) "Reasoning audit completion cannot precede its start."

        if
            usage.WallTime < TimeSpan.Zero
            || usage.CpuTime |> Option.exists (fun value -> value < TimeSpan.Zero)
            || usage.PeakMemoryBytes |> Option.exists (fun value -> value < 0L)
            || usage.Steps |> Option.exists (fun value -> value < 0L)
            || usage.Conclusions < 0
            || usage.ProofSteps < 0
            || usage.ResponseBytes |> Option.exists (fun value -> value < 0)
            || usage.ExternalCalls |> Option.exists (fun value -> value < 0)
        then
            invalidArg (nameof usage) "Reasoning usage values cannot be negative."

        { RequestId = ReasoningRequest.id request
          Engine = engine
          SemanticMode = ReasoningRequest.semanticMode request
          PinnedInputs = ReasoningRequest.pinnedInputs request
          SourceAssertions = ReasoningRequest.assertions request
          Outcome = outcome
          Usage = usage
          Audit = audit
          Artifacts = artifacts }

    let requestId value = value.RequestId
    let engine value = value.Engine
    let semanticMode value = value.SemanticMode
    let pinnedInputs value = value.PinnedInputs
    let sourceAssertions value = value.SourceAssertions
    let outcome value = value.Outcome
    let usage value = value.Usage
    let audit value = value.Audit
    let artifacts value = value.Artifacts

type ReasoningAuditRecord =
    { RequestId: Guid
      TraceId: Guid
      Owner: AuthorizationIdentity
      EngineId: string
      EngineVersion: string
      StartedAt: DateTimeOffset
      CompletedAt: DateTimeOffset
      Outcome: string
      InputDigests: string list }

type ReasoningAuditSink =
    { RecordAsync: AuthorizationScope -> ReasoningAuditRecord -> CancellationToken -> Task }

type ReasoningEndpoint =
    { Engine: ReasoningEngineIdentity
      ExecuteAsync: AuthorizationScope -> ReasoningRequest -> CancellationToken -> Task<ReasoningResult> }

[<RequireQualifiedAccess>]
module ReasoningAuditRecord =
    let private outcomeName =
        function
        | ReasoningOutcome.Proven _ -> "proven"
        | ReasoningOutcome.Disproven _ -> "disproven"
        | ReasoningOutcome.Unknown _ -> "unknown"
        | ReasoningOutcome.Inconsistent _ -> "inconsistent"
        | ReasoningOutcome.TimedOut -> "timeout"
        | ReasoningOutcome.Unsupported _ -> "unsupported"
        | ReasoningOutcome.ResourceLimitExceeded _ -> "resource-limit"
        | ReasoningOutcome.Failed _ -> "failed"

    let create request result =
        let audit = ReasoningResult.audit result
        let engine = ReasoningResult.engine result

        { RequestId = ReasoningResult.requestId result
          TraceId = audit.TraceId
          Owner = ReasoningRequest.owner request
          EngineId = ReasoningEngineIdentity.id engine
          EngineVersion = ReasoningEngineIdentity.version engine
          StartedAt = audit.StartedAt
          CompletedAt = audit.CompletedAt
          Outcome = ReasoningResult.outcome result |> outcomeName
          InputDigests = ReasoningResult.pinnedInputs result |> List.map ReasoningInputIdentity.digest }

[<RequireQualifiedAccess>]
module ReasoningEndpoint =
    let withAudit (sink: ReasoningAuditSink) (endpoint: ReasoningEndpoint) =
        { endpoint with
            ExecuteAsync =
                fun authorization request cancellationToken ->
                    task {
                        ReasoningRequest.requireOwner authorization request |> ignore
                        let! result = endpoint.ExecuteAsync authorization request cancellationToken
                        let record = ReasoningAuditRecord.create request result
                        do! sink.RecordAsync authorization record cancellationToken
                        return result
                    } }
