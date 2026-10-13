namespace Nao.Agents

open System

[<RequireQualifiedAccess>]
type SessionMemoryEventKind =
    | UserFact
    | UserPreference
    | UserCorrection
    | ExplicitRememberRequest
    | AssistantOutput
    | ToolOutput
    | SubAgentOutput
    | SystemEvent

[<RequireQualifiedAccess>]
module SessionMemoryEventKind =
    let isUserAsserted =
        function
        | SessionMemoryEventKind.UserFact
        | SessionMemoryEventKind.UserPreference
        | SessionMemoryEventKind.UserCorrection
        | SessionMemoryEventKind.ExplicitRememberRequest -> true
        | SessionMemoryEventKind.AssistantOutput
        | SessionMemoryEventKind.ToolOutput
        | SessionMemoryEventKind.SubAgentOutput
        | SessionMemoryEventKind.SystemEvent -> false

[<RequireQualifiedAccess>]
type SessionMemoryContentClass =
    | Ordinary
    | Sensitive
    | Secret

[<RequireQualifiedAccess>]
type SessionMemoryPersistenceIntent =
    | Durable
    | SessionOnly
    | Ephemeral

type SessionMemoryEvent =
    private
        { Id: Guid
          Owner: AuthorizationIdentity
          Correlation: CorrelationContext
          Kind: SessionMemoryEventKind
          Content: string
          ContentClass: SessionMemoryContentClass
          PersistenceIntent: SessionMemoryPersistenceIntent
          OccurredAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module SessionMemoryEvent =
    let capture authorization correlation kind content contentClass persistenceIntent occurredAt =
        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box correlation) then
            nullArg (nameof correlation)

        let owner = AuthorizationScope.identity authorization

        if owner.SessionId.IsNone then
            invalidArg (nameof authorization) "Session memory events require an authenticated session identity."

        if String.IsNullOrWhiteSpace content || content <> content.Trim() then
            invalidArg (nameof content) "Session memory event content must be non-blank and trimmed."

        { Id = Guid.NewGuid()
          Owner = owner
          Correlation = correlation
          Kind = kind
          Content = content
          ContentClass = contentClass
          PersistenceIntent = persistenceIntent
          OccurredAt = occurredAt }

    let id value = value.Id
    let owner value = value.Owner
    let correlation value = value.Correlation
    let kind value = value.Kind
    let content value = value.Content
    let contentClass value = value.ContentClass
    let persistenceIntent value = value.PersistenceIntent
    let occurredAt value = value.OccurredAt

type SessionMemoryEligibilityPolicy =
    private
        { PolicyId: string
          Version: string
          AllowedKinds: Set<SessionMemoryEventKind>
          MaximumEventAge: TimeSpan }

[<RequireQualifiedAccess>]
module SessionMemoryEligibilityPolicy =
    let create policyId version allowedKinds maximumEventAge =
        if String.IsNullOrWhiteSpace policyId || policyId <> policyId.Trim() then
            invalidArg (nameof policyId) "Session memory policy identity must be non-blank and trimmed."

        if String.IsNullOrWhiteSpace version || version <> version.Trim() then
            invalidArg (nameof version) "Session memory policy version must be non-blank and trimmed."

        let allowedKinds = Set.ofSeq allowedKinds

        if allowedKinds.IsEmpty then
            invalidArg (nameof allowedKinds) "Session memory policy must allow at least one user-asserted event kind."

        if allowedKinds |> Set.exists (SessionMemoryEventKind.isUserAsserted >> not) then
            invalidArg
                (nameof allowedKinds)
                "Assistant, tool, sub-agent, and system events can never be configured for direct durable promotion."

        if maximumEventAge <= TimeSpan.Zero then
            invalidArg (nameof maximumEventAge) "Session memory maximum event age must be positive."

        { PolicyId = policyId
          Version = version
          AllowedKinds = allowedKinds
          MaximumEventAge = maximumEventAge }

    let policyId value = value.PolicyId
    let version value = value.Version
    let allowedKinds value = value.AllowedKinds
    let maximumEventAge value = value.MaximumEventAge

    let defaultPolicy =
        create
            "nao.session-memory-eligibility"
            "1"
            [ SessionMemoryEventKind.UserFact
              SessionMemoryEventKind.UserPreference
              SessionMemoryEventKind.UserCorrection
              SessionMemoryEventKind.ExplicitRememberRequest ]
            (TimeSpan.FromDays 7.0)

[<RequireQualifiedAccess>]
type SessionMemoryIneligibilityReason =
    | AuthorizationScopeMismatch
    | EventFromFuture
    | EventExpired
    | NotUserAsserted
    | EventKindDenied
    | NotMarkedDurable
    | SensitiveContentRequiresReview
    | SecretContentForbidden

type DurableSessionMemoryCandidate =
    private
        { Event: SessionMemoryEvent
          PolicyId: string
          PolicyVersion: string
          EvaluatedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module DurableSessionMemoryCandidate =
    let event value = value.Event
    let policyId value = value.PolicyId
    let policyVersion value = value.PolicyVersion
    let evaluatedAt value = value.EvaluatedAt

[<RequireQualifiedAccess>]
type SessionMemoryEligibilityResult =
    | Eligible of DurableSessionMemoryCandidate
    | Ineligible of SessionMemoryEvent * SessionMemoryIneligibilityReason list

[<RequireQualifiedAccess>]
module SessionMemoryEligibility =
    let evaluate policy authorization evaluatedAt event =
        if isNull (box policy) then
            nullArg (nameof policy)

        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box event) then
            nullArg (nameof event)

        let reasons = ResizeArray<SessionMemoryIneligibilityReason>()

        if AuthorizationScope.identity authorization <> SessionMemoryEvent.owner event then
            reasons.Add SessionMemoryIneligibilityReason.AuthorizationScopeMismatch

        if SessionMemoryEvent.occurredAt event > evaluatedAt then
            reasons.Add SessionMemoryIneligibilityReason.EventFromFuture
        elif
            evaluatedAt - SessionMemoryEvent.occurredAt event > SessionMemoryEligibilityPolicy.maximumEventAge policy
        then
            reasons.Add SessionMemoryIneligibilityReason.EventExpired

        if not (event |> SessionMemoryEvent.kind |> SessionMemoryEventKind.isUserAsserted) then
            reasons.Add SessionMemoryIneligibilityReason.NotUserAsserted

        if
            not (
                policy
                |> SessionMemoryEligibilityPolicy.allowedKinds
                |> Set.contains (SessionMemoryEvent.kind event)
            )
        then
            reasons.Add SessionMemoryIneligibilityReason.EventKindDenied

        if
            SessionMemoryEvent.persistenceIntent event
            <> SessionMemoryPersistenceIntent.Durable
        then
            reasons.Add SessionMemoryIneligibilityReason.NotMarkedDurable

        match SessionMemoryEvent.contentClass event with
        | SessionMemoryContentClass.Ordinary -> ()
        | SessionMemoryContentClass.Sensitive ->
            reasons.Add SessionMemoryIneligibilityReason.SensitiveContentRequiresReview
        | SessionMemoryContentClass.Secret -> reasons.Add SessionMemoryIneligibilityReason.SecretContentForbidden

        if reasons.Count = 0 then
            SessionMemoryEligibilityResult.Eligible
                { Event = event
                  PolicyId = SessionMemoryEligibilityPolicy.policyId policy
                  PolicyVersion = SessionMemoryEligibilityPolicy.version policy
                  EvaluatedAt = evaluatedAt }
        else
            SessionMemoryEligibilityResult.Ineligible(event, List.ofSeq reasons)
