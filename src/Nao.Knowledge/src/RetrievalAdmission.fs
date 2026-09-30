namespace Nao.Knowledge

open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type KnowledgeRetrievalAdmissionDecision =
    | Retrieve
    | Skip

[<RequireQualifiedAccess>]
type KnowledgeRetrievalAdmissionReason =
    | ExplicitKnowledgeIntent
    | UnspecifiedIntent
    | ExplicitNonKnowledgeIntent

type KnowledgeRetrievalAdmission =
    private
        { NormalizedQuery: KnowledgeNormalizedQuery
          Policy: ComponentVersion
          Decision: KnowledgeRetrievalAdmissionDecision
          Reason: KnowledgeRetrievalAdmissionReason }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalAdmission =
    let normalizedQuery value = value.NormalizedQuery

    let request value =
        value.NormalizedQuery |> KnowledgeNormalizedQuery.request

    let policy value = value.Policy
    let decision value = value.Decision
    let reason value = value.Reason

[<RequireQualifiedAccess>]
type KnowledgeQueryUnderstandingResult =
    | RetrievalSkipped of KnowledgeRetrievalAdmission
    | RetrievalPlanned of KnowledgeRetrievalAdmission * KnowledgeQueryTransformationPlan

[<RequireQualifiedAccess>]
module KnowledgeRetrievalAdmissionPolicy =
    let descriptor = ComponentVersion.create "nao-retrieval-admission" "1"

    let evaluate (normalizedQuery: KnowledgeNormalizedQuery) =
        let request = KnowledgeNormalizedQuery.request normalizedQuery

        let decision, reason =
            match KnowledgeRetrievalRequest.intent request with
            | KnowledgeTaskIntent.KnowledgeSeeking ->
                KnowledgeRetrievalAdmissionDecision.Retrieve, KnowledgeRetrievalAdmissionReason.ExplicitKnowledgeIntent
            | KnowledgeTaskIntent.Unspecified ->
                KnowledgeRetrievalAdmissionDecision.Retrieve, KnowledgeRetrievalAdmissionReason.UnspecifiedIntent
            | KnowledgeTaskIntent.NonKnowledge ->
                KnowledgeRetrievalAdmissionDecision.Skip, KnowledgeRetrievalAdmissionReason.ExplicitNonKnowledgeIntent

        { NormalizedQuery = normalizedQuery
          Policy = descriptor
          Decision = decision
          Reason = reason }

[<RequireQualifiedAccess>]
module KnowledgeQueryUnderstanding =
    let planAsync
        (model: KnowledgeQueryTransformationModel option)
        (normalizedQuery: KnowledgeNormalizedQuery)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeQueryUnderstandingResult> =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let admission = KnowledgeRetrievalAdmissionPolicy.evaluate normalizedQuery

            match KnowledgeRetrievalAdmission.decision admission with
            | KnowledgeRetrievalAdmissionDecision.Skip ->
                return KnowledgeQueryUnderstandingResult.RetrievalSkipped admission
            | KnowledgeRetrievalAdmissionDecision.Retrieve ->
                let! plan = KnowledgeQueryTransformation.transformAsync model normalizedQuery cancellationToken
                return KnowledgeQueryUnderstandingResult.RetrievalPlanned(admission, plan)
        }
