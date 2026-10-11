namespace Nao.Knowledge

[<RequireQualifiedAccess>]
type KnowledgeUnsupportedClaimBehavior =
    | Abstain
    | Repair
    | Warn
    | Reject

type KnowledgeGroundedSupportPolicyConfig =
    private
        { UnsupportedClaimBehavior: KnowledgeUnsupportedClaimBehavior }

[<RequireQualifiedAccess>]
module KnowledgeGroundedSupportPolicyConfig =
    let create unsupportedClaimBehavior =
        { UnsupportedClaimBehavior = unsupportedClaimBehavior }

    let unsupportedClaimBehavior value = value.UnsupportedClaimBehavior

    let defaultConfig = create KnowledgeUnsupportedClaimBehavior.Reject

type KnowledgeGroundedAnswerRepairRequest =
    private
        { Evaluation: KnowledgeGroundedClaimSupportEvaluation
          UnsupportedClaims: KnowledgeClaimSupportAssessment list
          Instruction: string }

[<RequireQualifiedAccess>]
module KnowledgeGroundedAnswerRepairRequest =
    let evaluation value = value.Evaluation
    let unsupportedClaims value = value.UnsupportedClaims
    let instruction value = value.Instruction

[<RequireQualifiedAccess>]
type KnowledgeGroundedSupportPolicyResult =
    | Accepted of KnowledgeGroundedClaimSupportEvaluation
    | Abstained of KnowledgeGroundedClaimSupportEvaluation * KnowledgeGroundedInsufficiency
    | RepairRequired of KnowledgeGroundedAnswerRepairRequest
    | Warned of KnowledgeGroundedClaimSupportEvaluation
    | Rejected of KnowledgeGroundedClaimSupportEvaluation

[<RequireQualifiedAccess>]
module KnowledgeGroundedSupportPolicy =
    let private repairInstruction =
        "Return a new grounded answer proposal that revises or removes every unsupported material claim and cites only evidence that entails each retained claim."

    let apply config supportResult =
        if isNull (box config) then
            nullArg (nameof config)

        let evaluation =
            match supportResult with
            | KnowledgeGroundedClaimSupportResult.Supported value
            | KnowledgeGroundedClaimSupportResult.Unsupported value -> value

        let unsupportedClaims =
            KnowledgeGroundedClaimSupportEvaluation.unsupportedClaims evaluation

        if List.isEmpty unsupportedClaims then
            KnowledgeGroundedSupportPolicyResult.Accepted evaluation
        else
            match KnowledgeGroundedSupportPolicyConfig.unsupportedClaimBehavior config with
            | KnowledgeUnsupportedClaimBehavior.Abstain ->
                let insufficiency =
                    KnowledgeGroundedInsufficiency.create
                        KnowledgeGroundedInsufficiencyReason.PolicyRejected
                        "One or more material claims are unsupported by the cited evidence."

                KnowledgeGroundedSupportPolicyResult.Abstained(evaluation, insufficiency)
            | KnowledgeUnsupportedClaimBehavior.Repair ->
                KnowledgeGroundedSupportPolicyResult.RepairRequired
                    { Evaluation = evaluation
                      UnsupportedClaims = unsupportedClaims
                      Instruction = repairInstruction }
            | KnowledgeUnsupportedClaimBehavior.Warn -> KnowledgeGroundedSupportPolicyResult.Warned evaluation
            | KnowledgeUnsupportedClaimBehavior.Reject -> KnowledgeGroundedSupportPolicyResult.Rejected evaluation
