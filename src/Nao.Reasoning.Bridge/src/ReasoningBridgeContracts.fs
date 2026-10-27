namespace Nao.Reasoning.Bridge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Reasoning

module private BridgeContract =
    let canonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name "Value must be canonical and non-blank."

        value

    let confidence name value =
        if not (Double.IsFinite value) || value < 0.0 || value > 1.0 then
            invalidArg name "Confidence must be finite and between zero and one."

        value

type ReasoningSchema =
    private
        { Id: string
          Version: string
          Digest: string
          Vocabulary: ReasoningArtifact
          QuerySchema: ReasoningArtifact
          Owner: AuthorizationIdentity }

[<RequireQualifiedAccess>]
module ReasoningSchema =
    let create authorization id version (digest: string) vocabulary querySchema =
        BridgeContract.canonical (nameof id) id |> ignore
        BridgeContract.canonical (nameof version) version |> ignore

        if
            isNull digest
            || digest.Length <> 64
            || digest
               |> Seq.exists (fun character -> not (Char.IsDigit character || character >= 'a' && character <= 'f'))
        then
            invalidArg (nameof digest) "Schema digest must be canonical lowercase SHA-256."

        { Id = id
          Version = version
          Digest = digest
          Vocabulary = vocabulary
          QuerySchema = querySchema
          Owner = AuthorizationScope.identity authorization }

    let requireOwner authorization value =
        if value.Owner <> AuthorizationScope.identity authorization then
            raise (UnauthorizedAccessException "Reasoning schema cannot cross authorization boundaries.")

        value

    let id value = value.Id
    let version value = value.Version
    let digest value = value.Digest
    let vocabulary value = value.Vocabulary
    let querySchema value = value.QuerySchema

type ReasoningSchemaDiscovery =
    { DiscoverAsync: AuthorizationScope -> CancellationToken -> Task<ReasoningSchema> }

type ReasoningEntityAlternative =
    private
        { EntityId: string
          Label: string
          Confidence: float
          Citations: ReasoningCitation list }

[<RequireQualifiedAccess>]
module ReasoningEntityAlternative =
    let create entityId label confidence citations =
        BridgeContract.canonical (nameof entityId) entityId |> ignore
        BridgeContract.canonical (nameof label) label |> ignore
        BridgeContract.confidence (nameof confidence) confidence |> ignore

        if List.isEmpty citations then
            invalidArg (nameof citations) "Entity alternatives require source citations."

        { EntityId = entityId
          Label = label
          Confidence = confidence
          Citations = citations }

    let entityId value = value.EntityId
    let label value = value.Label
    let confidence value = value.Confidence
    let citations value = value.Citations

type ReasoningEntityResolution =
    private
        { Mention: string
          Alternatives: ReasoningEntityAlternative list }

[<RequireQualifiedAccess>]
module ReasoningEntityResolution =
    let create mention alternatives =
        BridgeContract.canonical (nameof mention) mention |> ignore

        if List.isEmpty alternatives then
            invalidArg (nameof alternatives) "Entity resolution requires at least one alternative."

        let alternatives =
            alternatives |> List.sortByDescending ReasoningEntityAlternative.confidence

        let ids = alternatives |> List.map ReasoningEntityAlternative.entityId

        if ids.Length <> (ids |> List.distinct |> List.length) then
            invalidArg (nameof alternatives) "Entity alternatives must be unique."

        { Mention = mention
          Alternatives = alternatives }

    let mention value = value.Mention
    let alternatives value = value.Alternatives

type ReasoningInterpretation =
    private
        { Id: Guid
          SchemaId: string
          SchemaVersion: string
          SchemaDigest: string
          FormalRequest: ReasoningArtifact
          Confidence: float
          EntityResolutions: ReasoningEntityResolution list
          Citations: ReasoningCitation list }

[<RequireQualifiedAccess>]
module ReasoningInterpretation =
    let create id schema formalRequest confidence entityResolutions citations =
        if id = Guid.Empty then
            invalidArg (nameof id) "Interpretation identity cannot be empty."

        BridgeContract.confidence (nameof confidence) confidence |> ignore

        if List.isEmpty citations then
            invalidArg (nameof citations) "Interpretations require source citations."

        { Id = id
          SchemaId = ReasoningSchema.id schema
          SchemaVersion = ReasoningSchema.version schema
          SchemaDigest = ReasoningSchema.digest schema
          FormalRequest = formalRequest
          Confidence = confidence
          EntityResolutions = entityResolutions
          Citations = citations }

    let id value = value.Id
    let schemaId value = value.SchemaId
    let schemaVersion value = value.SchemaVersion
    let schemaDigest value = value.SchemaDigest
    let formalRequest value = value.FormalRequest
    let confidence value = value.Confidence
    let entityResolutions value = value.EntityResolutions
    let citations value = value.Citations

type ReasoningTranslator =
    { TranslateAsync:
        AuthorizationScope -> ReasoningSchema -> string -> CancellationToken -> Task<ReasoningInterpretation list> }

[<RequireQualifiedAccess>]
type ReasoningImpact =
    | Ordinary
    | HighImpact of category: string

type ValidatedReasoningRequest =
    private
        { Interpretation: ReasoningInterpretation
          Request: ReasoningRequest
          Impact: ReasoningImpact }

[<RequireQualifiedAccess>]
module ValidatedReasoningRequest =
    let create interpretation request impact =
        { Interpretation = interpretation
          Request = request
          Impact = impact }

    let interpretation value = value.Interpretation
    let request value = value.Request
    let impact value = value.Impact

type ReasoningCandidateValidation =
    | Valid of ValidatedReasoningRequest
    | Invalid of code: string * detail: string

type ReasoningCandidateValidator =
    { ValidateAsync:
        AuthorizationScope
            -> ReasoningSchema
            -> ReasoningInterpretation
            -> CancellationToken
            -> Task<ReasoningCandidateValidation> }

type ReasoningReviewApproval =
    private
        { InterpretationId: Guid
          FormalRequestDigest: string
          Reviewer: AuthorizationIdentity
          ReviewedAt: DateTimeOffset
          Rationale: string }

[<RequireQualifiedAccess>]
module ReasoningReviewApproval =
    let create interpretation requesterAuthorization reviewerAuthorization reviewedAt rationale =
        BridgeContract.canonical (nameof rationale) rationale |> ignore

        let requester = AuthorizationScope.identity requesterAuthorization
        let reviewer = AuthorizationScope.identity reviewerAuthorization

        if
            requester.TenantId <> reviewer.TenantId
            || requester.GroupId <> reviewer.GroupId
            || requester.WorkspaceId <> reviewer.WorkspaceId
            || requester.SessionId <> reviewer.SessionId
        then
            invalidArg (nameof reviewerAuthorization) "Reasoning review cannot cross an ownership boundary."

        if requester.UserId = reviewer.UserId then
            invalidArg (nameof reviewerAuthorization) "High-impact reasoning review requires separation of duties."

        { InterpretationId = ReasoningInterpretation.id interpretation
          FormalRequestDigest =
            interpretation
            |> ReasoningInterpretation.formalRequest
            |> ReasoningArtifact.digest
          Reviewer = reviewer
          ReviewedAt = reviewedAt
          Rationale = rationale }

    let matches authorization interpretation value =
        let requester = AuthorizationScope.identity authorization

        value.InterpretationId = ReasoningInterpretation.id interpretation
        && value.FormalRequestDigest = (interpretation
                                        |> ReasoningInterpretation.formalRequest
                                        |> ReasoningArtifact.digest)
        && value.Reviewer.TenantId = requester.TenantId
        && value.Reviewer.GroupId = requester.GroupId
        && value.Reviewer.WorkspaceId = requester.WorkspaceId
        && value.Reviewer.SessionId = requester.SessionId
        && value.Reviewer.UserId <> requester.UserId

type ReasoningBridgePolicy =
    private
        { MinimumConfidence: float
          AmbiguityMargin: float
          RequireHighImpactReview: bool }

[<RequireQualifiedAccess>]
module ReasoningBridgePolicy =
    let create minimumConfidence ambiguityMargin requireHighImpactReview =
        BridgeContract.confidence (nameof minimumConfidence) minimumConfidence |> ignore
        BridgeContract.confidence (nameof ambiguityMargin) ambiguityMargin |> ignore

        { MinimumConfidence = minimumConfidence
          AmbiguityMargin = ambiguityMargin
          RequireHighImpactReview = requireHighImpactReview }

    let minimumConfidence value = value.MinimumConfidence
    let ambiguityMargin value = value.AmbiguityMargin
    let requireHighImpactReview value = value.RequireHighImpactReview

type ReasoningExplanationDraft =
    { Narrative: string
      CitationAddresses: string list }

type ReasoningExplanationGenerator =
    { GenerateAsync: ReasoningInterpretation -> ReasoningResult -> CancellationToken -> Task<ReasoningExplanationDraft> }

type GroundedReasoningExplanation =
    private
        { Narrative: string
          Outcome: ReasoningOutcome
          Interpretation: ReasoningArtifact
          Citations: ReasoningCitation list }

[<RequireQualifiedAccess>]
module GroundedReasoningExplanation =
    let internal create narrative outcome interpretation citations =
        { Narrative = narrative
          Outcome = outcome
          Interpretation = interpretation
          Citations = citations }

    let narrative value = value.Narrative
    let outcome value = value.Outcome
    let interpretation value = value.Interpretation
    let citations value = value.Citations

[<RequireQualifiedAccess>]
type ReasoningBridgeOutcome =
    | Rejected of failures: (Guid * string * string) list
    | ClarificationRequired of interpretations: ReasoningInterpretation list
    | ReviewRequired of request: ValidatedReasoningRequest
    | Completed of formalResult: ReasoningResult * explanation: GroundedReasoningExplanation
