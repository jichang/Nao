namespace Nao.Agents

open System

type CrossSessionMemorySubject =
    private
        { TenantId: TenantId
          GroupId: GroupId option
          UserId: UserId }

[<RequireQualifiedAccess>]
module CrossSessionMemorySubject =
    let ofIdentity (identity: AuthorizationIdentity) =
        { TenantId = identity.TenantId
          GroupId = identity.GroupId
          UserId = identity.UserId }

    let tenantId value = value.TenantId
    let groupId value = value.GroupId
    let userId value = value.UserId

[<RequireQualifiedAccess>]
type CrossSessionMemoryConsentScope =
    | Memory of eventId: Guid
    | Category of SessionMemoryEventKind
    | Workspace of WorkspaceId
    | User

type CrossSessionMemoryConsent =
    private
        { Id: Guid
          Version: int
          Subject: CrossSessionMemorySubject
          Scope: CrossSessionMemoryConsentScope
          GrantedAt: DateTimeOffset
          ExpiresAt: DateTimeOffset option
          RevokedAt: DateTimeOffset option }

[<RequireQualifiedAccess>]
module CrossSessionMemoryConsent =
    let currentVersion = 1

    let grant subject scope grantedAt expiresAt =
        if isNull (box subject) then
            nullArg (nameof subject)

        match expiresAt with
        | Some value when value <= grantedAt ->
            invalidArg (nameof expiresAt) "Cross-session memory consent must expire after it is granted."
        | _ ->
            { Id = Guid.NewGuid()
              Version = currentVersion
              Subject = subject
              Scope = scope
              GrantedAt = grantedAt
              ExpiresAt = expiresAt
              RevokedAt = None }

    let revoke revokedAt consent =
        if revokedAt < consent.GrantedAt then
            invalidArg (nameof revokedAt) "Cross-session memory consent cannot be revoked before it is granted."

        if consent.RevokedAt.IsSome then
            invalidOp "Cross-session memory consent has already been revoked."

        { consent with
            RevokedAt = Some revokedAt }

    let id value = value.Id
    let version value = value.Version
    let subject value = value.Subject
    let scope value = value.Scope
    let grantedAt value = value.GrantedAt
    let expiresAt value = value.ExpiresAt
    let revokedAt value = value.RevokedAt

type CrossSessionMemoryPolicyDecision =
    private
        { PolicyId: string
          PolicyVersion: string
          CandidateEventId: Guid
          TargetOwner: AuthorizationIdentity
          Allowed: bool
          EvaluatedAt: DateTimeOffset
          ValidUntil: DateTimeOffset }

[<RequireQualifiedAccess>]
module CrossSessionMemoryPolicyDecision =
    let create policyId policyVersion candidateEventId targetOwner allowed evaluatedAt validUntil =
        if String.IsNullOrWhiteSpace policyId || policyId <> policyId.Trim() then
            invalidArg (nameof policyId) "Cross-session memory policy identity must be non-blank and trimmed."

        if String.IsNullOrWhiteSpace policyVersion || policyVersion <> policyVersion.Trim() then
            invalidArg (nameof policyVersion) "Cross-session memory policy version must be non-blank and trimmed."

        if targetOwner.SessionId.IsSome then
            invalidArg (nameof targetOwner) "Cross-session memory policy targets cannot contain a session identity."

        if validUntil <= evaluatedAt then
            invalidArg (nameof validUntil) "Cross-session memory policy decisions require a future validity deadline."

        { PolicyId = policyId
          PolicyVersion = policyVersion
          CandidateEventId = candidateEventId
          TargetOwner = targetOwner
          Allowed = allowed
          EvaluatedAt = evaluatedAt
          ValidUntil = validUntil }

    let policyId value = value.PolicyId
    let policyVersion value = value.PolicyVersion
    let candidateEventId value = value.CandidateEventId
    let targetOwner value = value.TargetOwner
    let allowed value = value.Allowed
    let evaluatedAt value = value.EvaluatedAt
    let validUntil value = value.ValidUntil

[<RequireQualifiedAccess>]
type CrossSessionMemoryDenialReason =
    | AuthorizationScopeMismatch
    | ConsentMissing
    | ConsentSubjectMismatch
    | ConsentScopeMismatch
    | ConsentNotYetActive
    | ConsentExpired
    | ConsentRevoked
    | PolicyCandidateMismatch
    | PolicyOwnerMismatch
    | PolicyDecisionNotYetActive
    | PolicyDecisionExpired
    | PolicyDenied

type CrossSessionUserMemoryCandidate =
    private
        { Candidate: DurableSessionMemoryCandidate
          Owner: AuthorizationIdentity
          Consent: CrossSessionMemoryConsent
          PolicyDecision: CrossSessionMemoryPolicyDecision
          ApprovedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module CrossSessionUserMemoryCandidate =
    let candidate value = value.Candidate
    let owner value = value.Owner
    let consent value = value.Consent
    let policyDecision value = value.PolicyDecision
    let approvedAt value = value.ApprovedAt

[<RequireQualifiedAccess>]
type CrossSessionMemoryApprovalResult =
    | Approved of CrossSessionUserMemoryCandidate
    | Denied of DurableSessionMemoryCandidate * CrossSessionMemoryDenialReason list

[<RequireQualifiedAccess>]
module CrossSessionMemoryApproval =
    let private crossSessionOwner (identity: AuthorizationIdentity) = { identity with SessionId = None }

    let private consentCovers candidate consent =
        let event = DurableSessionMemoryCandidate.event candidate

        match CrossSessionMemoryConsent.scope consent with
        | CrossSessionMemoryConsentScope.Memory eventId -> eventId = SessionMemoryEvent.id event
        | CrossSessionMemoryConsentScope.Category kind -> kind = SessionMemoryEvent.kind event
        | CrossSessionMemoryConsentScope.Workspace workspaceId ->
            workspaceId = (SessionMemoryEvent.owner event).WorkspaceId
        | CrossSessionMemoryConsentScope.User -> true

    let evaluate authorization approvedAt consent policyDecision candidate =
        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box policyDecision) then
            nullArg (nameof policyDecision)

        if isNull (box candidate) then
            nullArg (nameof candidate)

        let event = DurableSessionMemoryCandidate.event candidate
        let sourceOwner = SessionMemoryEvent.owner event
        let targetOwner = crossSessionOwner sourceOwner
        let reasons = ResizeArray<CrossSessionMemoryDenialReason>()

        if AuthorizationScope.identity authorization <> sourceOwner then
            reasons.Add CrossSessionMemoryDenialReason.AuthorizationScopeMismatch

        match consent with
        | None -> reasons.Add CrossSessionMemoryDenialReason.ConsentMissing
        | Some value ->
            if
                CrossSessionMemoryConsent.subject value
                <> CrossSessionMemorySubject.ofIdentity sourceOwner
            then
                reasons.Add CrossSessionMemoryDenialReason.ConsentSubjectMismatch

            if not (consentCovers candidate value) then
                reasons.Add CrossSessionMemoryDenialReason.ConsentScopeMismatch

            if CrossSessionMemoryConsent.grantedAt value > approvedAt then
                reasons.Add CrossSessionMemoryDenialReason.ConsentNotYetActive

            if
                CrossSessionMemoryConsent.expiresAt value
                |> Option.exists (fun expiry -> expiry <= approvedAt)
            then
                reasons.Add CrossSessionMemoryDenialReason.ConsentExpired

            if
                CrossSessionMemoryConsent.revokedAt value
                |> Option.exists (fun revoked -> revoked <= approvedAt)
            then
                reasons.Add CrossSessionMemoryDenialReason.ConsentRevoked

        if
            CrossSessionMemoryPolicyDecision.candidateEventId policyDecision
            <> SessionMemoryEvent.id event
        then
            reasons.Add CrossSessionMemoryDenialReason.PolicyCandidateMismatch

        if CrossSessionMemoryPolicyDecision.targetOwner policyDecision <> targetOwner then
            reasons.Add CrossSessionMemoryDenialReason.PolicyOwnerMismatch

        if CrossSessionMemoryPolicyDecision.evaluatedAt policyDecision > approvedAt then
            reasons.Add CrossSessionMemoryDenialReason.PolicyDecisionNotYetActive

        if CrossSessionMemoryPolicyDecision.validUntil policyDecision <= approvedAt then
            reasons.Add CrossSessionMemoryDenialReason.PolicyDecisionExpired

        if not (CrossSessionMemoryPolicyDecision.allowed policyDecision) then
            reasons.Add CrossSessionMemoryDenialReason.PolicyDenied

        match consent with
        | Some value when reasons.Count = 0 ->
            CrossSessionMemoryApprovalResult.Approved
                { Candidate = candidate
                  Owner = targetOwner
                  Consent = value
                  PolicyDecision = policyDecision
                  ApprovedAt = approvedAt }
        | _ -> CrossSessionMemoryApprovalResult.Denied(candidate, List.ofSeq reasons)
