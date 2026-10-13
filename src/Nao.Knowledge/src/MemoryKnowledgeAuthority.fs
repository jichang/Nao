namespace Nao.Knowledge

open System
open Nao.Agents

type MemoryKnowledgeReference =
    private
        { Uri: Uri
          ContentHash: ContentHash
          Description: string }

[<RequireQualifiedAccess>]
module MemoryKnowledgeReference =
    let create (uri: Uri) contentHash description =
        if isNull uri || not uri.IsAbsoluteUri then
            invalidArg (nameof uri) "Memory knowledge verification references require an absolute URI."

        if String.IsNullOrWhiteSpace description || description <> description.Trim() then
            invalidArg (nameof description) "Memory knowledge reference descriptions must be non-blank and trimmed."

        { Uri = uri
          ContentHash = contentHash
          Description = description }

    let uri value = value.Uri
    let contentHash value = value.ContentHash
    let description value = value.Description

type MemoryKnowledgeVerifierDescriptor =
    private
        { Component: ComponentVersion
          ConfigurationHash: ContentHash }

[<RequireQualifiedAccess>]
module MemoryKnowledgeVerifierDescriptor =
    let create verifierComponent configurationHash =
        if isNull (box verifierComponent) then
            nullArg (nameof verifierComponent)

        { Component = verifierComponent
          ConfigurationHash = configurationHash }

    let verifierComponent value = value.Component
    let configurationHash value = value.ConfigurationHash

type MemoryKnowledgeVerification =
    private
        { MemoryId: Guid
          MemoryVersion: int
          Owner: AuthorizationIdentity
          ValueHash: ContentHash
          Trust: KnowledgeTrust
          References: MemoryKnowledgeReference list
          Verifier: MemoryKnowledgeVerifierDescriptor
          Rationale: string
          VerifiedAt: DateTimeOffset
          ValidUntil: DateTimeOffset }

[<RequireQualifiedAccess>]
module MemoryKnowledgeVerification =
    let create memoryId memoryVersion owner valueHash trust references verifier rationale verifiedAt validUntil =
        if memoryVersion <= 0 then
            invalidArg (nameof memoryVersion) "Memory knowledge verification requires a positive memory version."

        if owner.SessionId.IsSome then
            invalidArg (nameof owner) "Memory knowledge verification owners cannot contain a session identity."

        match trust with
        | KnowledgeTrust.OrganizationVerified
        | KnowledgeTrust.Authoritative -> ()
        | KnowledgeTrust.Unverified
        | KnowledgeTrust.SourceAsserted ->
            invalidArg
                (nameof trust)
                "Memory knowledge promotion requires organization-verified or authoritative verification."

        let references = List.ofSeq references

        if references.IsEmpty then
            invalidArg (nameof references) "Memory knowledge verification requires external reference evidence."

        if references |> List.distinct |> List.length <> references.Length then
            invalidArg (nameof references) "Memory knowledge verification references must be unique."

        if isNull (box verifier) then
            nullArg (nameof verifier)

        if String.IsNullOrWhiteSpace rationale || rationale <> rationale.Trim() then
            invalidArg (nameof rationale) "Memory knowledge verification rationale must be non-blank and trimmed."

        if validUntil <= verifiedAt then
            invalidArg (nameof validUntil) "Memory knowledge verification must expire after it is performed."

        { MemoryId = memoryId
          MemoryVersion = memoryVersion
          Owner = owner
          ValueHash = valueHash
          Trust = trust
          References = references
          Verifier = verifier
          Rationale = rationale
          VerifiedAt = verifiedAt
          ValidUntil = validUntil }

    let memoryId value = value.MemoryId
    let memoryVersion value = value.MemoryVersion
    let owner value = value.Owner
    let valueHash value = value.ValueHash
    let trust value = value.Trust
    let references value = value.References
    let verifier value = value.Verifier
    let rationale value = value.Rationale
    let verifiedAt value = value.VerifiedAt
    let validUntil value = value.ValidUntil

type VerifiedMemoryKnowledgeCandidate =
    private
        { Memory: DurableMemoryRecord
          Key: string
          Value: string
          Verification: MemoryKnowledgeVerification
          Trust: TrustAssessment
          PromotedBy: AuthorizationIdentity
          PermissionEvidence: PermissionDecisionEvidence
          PromotedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module VerifiedMemoryKnowledgeCandidate =
    let memory value = value.Memory
    let key value = value.Key
    let value value = value.Value
    let verification value = value.Verification
    let trust value = value.Trust
    let promotedBy value = value.PromotedBy
    let permissionEvidence value = value.PermissionEvidence
    let promotedAt value = value.PromotedAt

[<RequireQualifiedAccess>]
type MemoryKnowledgePromotionResult =
    | Promoted of VerifiedMemoryKnowledgeCandidate
    | Denied of PermissionDecisionEvidence

[<RequireQualifiedAccess>]
module MemoryKnowledgeAuthority =
    let private resource record =
        ResourceAccess.administration
            $"memory-knowledge-promotion/{DurableMemoryRecord.id record:D}/{DurableMemoryRecord.version record}"
            AdministrationAction.Manage

    let promotionAccess record = resource record

    let promote authorization promotedAt permissionEvidence verification record =
        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box permissionEvidence) then
            nullArg (nameof permissionEvidence)

        if isNull (box verification) then
            nullArg (nameof verification)

        if isNull (box record) then
            nullArg (nameof record)

        let access = resource record

        if permissionEvidence.Access <> access then
            invalidArg (nameof permissionEvidence) "Memory knowledge permission evidence targets another resource."

        if permissionEvidence.EvaluatedAt > promotedAt then
            invalidArg (nameof promotedAt) "Memory knowledge cannot be promoted before permission evaluation."

        if permissionEvidence.Decision <> PermissionDecision.Allow then
            MemoryKnowledgePromotionResult.Denied permissionEvidence
        else
            let key, memoryValue =
                match DurableMemoryRecord.key record, DurableMemoryRecord.state record with
                | Some key, DurableMemoryState.Active value when DurableMemoryValue.expiresAt value > promotedAt ->
                    key, value
                | _, DurableMemoryState.Active _ -> invalidOp "Expired durable memory cannot be promoted to knowledge."
                | _, DurableMemoryState.Contested _ ->
                    invalidOp "Contested durable memory cannot be promoted to knowledge."
                | _, DurableMemoryState.Expired _
                | _, DurableMemoryState.Forgotten _ ->
                    invalidOp "Terminal durable memory cannot be promoted to knowledge."

            let owner = DurableMemoryRecord.owner record
            let valueHash = DurableMemoryValue.valueHash memoryValue |> ContentHash.parse

            if
                MemoryKnowledgeVerification.memoryId verification
                <> DurableMemoryRecord.id record
            then
                invalidArg (nameof verification) "Memory knowledge verification targets another memory record."

            if
                MemoryKnowledgeVerification.memoryVersion verification
                <> DurableMemoryRecord.version record
            then
                invalidArg (nameof verification) "Memory knowledge verification targets another memory version."

            if MemoryKnowledgeVerification.owner verification <> owner then
                invalidArg (nameof verification) "Memory knowledge verification targets another owner."

            if MemoryKnowledgeVerification.valueHash verification <> valueHash then
                invalidArg (nameof verification) "Memory knowledge verification targets another memory value."

            if MemoryKnowledgeVerification.verifiedAt verification > promotedAt then
                invalidArg (nameof promotedAt) "Memory knowledge cannot be promoted before verification."

            if MemoryKnowledgeVerification.validUntil verification <= promotedAt then
                invalidOp "Expired memory knowledge verification cannot authorize promotion."

            let verifierComponent =
                verification
                |> MemoryKnowledgeVerification.verifier
                |> MemoryKnowledgeVerifierDescriptor.verifierComponent

            let basis =
                $"{verifierComponent.ComponentId}@{verifierComponent.Version}: {MemoryKnowledgeVerification.rationale verification}"

            MemoryKnowledgePromotionResult.Promoted
                { Memory = record
                  Key = key
                  Value = DurableMemoryValue.value memoryValue
                  Verification = verification
                  Trust =
                    TrustAssessment.create
                        (MemoryKnowledgeVerification.trust verification)
                        basis
                        (MemoryKnowledgeVerification.verifiedAt verification)
                  PromotedBy = AuthorizationScope.identity authorization
                  PermissionEvidence = permissionEvidence
                  PromotedAt = promotedAt }
