namespace Nao.Agents

open System
open System.Security.Cryptography
open System.Text

type DurableMemoryLifecyclePolicy =
    private
        { Retention: TimeSpan
          MaximumProvenanceEntries: int }

[<RequireQualifiedAccess>]
module DurableMemoryLifecyclePolicy =
    let create retention maximumProvenanceEntries =
        if retention <= TimeSpan.Zero then
            invalidArg (nameof retention) "Durable memory retention must be positive."

        if maximumProvenanceEntries <= 0 then
            invalidArg (nameof maximumProvenanceEntries) "Durable memory provenance limits must be positive."

        { Retention = retention
          MaximumProvenanceEntries = maximumProvenanceEntries }

    let retention value = value.Retention
    let maximumProvenanceEntries value = value.MaximumProvenanceEntries

    let defaultPolicy = create (TimeSpan.FromDays 365.0) 16

type DurableMemorySourceEvidence =
    private
        { Batch: BackgroundMemoryExtractionBatch
          Assessment: MemoryExtractionAssessment }

[<RequireQualifiedAccess>]
module DurableMemorySourceEvidence =
    let batch value = value.Batch
    let assessment value = value.Assessment

type DurableMemoryValue =
    private
        { Value: string
          ValueHash: string
          Confidence: MemoryExtractionConfidence
          Sources: DurableMemorySourceEvidence list
          ExpiresAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module DurableMemoryValue =
    let value memory = memory.Value
    let valueHash memory = memory.ValueHash
    let confidence memory = memory.Confidence
    let sources memory = memory.Sources
    let expiresAt memory = memory.ExpiresAt

type DurableMemoryConflict =
    private
        { Alternatives: DurableMemoryValue list
          DetectedAt: DateTimeOffset
          ExpiresAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module DurableMemoryConflict =
    let alternatives value = value.Alternatives
    let detectedAt value = value.DetectedAt
    let expiresAt value = value.ExpiresAt

type UserMemoryForgetRequest =
    private
        { MemoryId: Guid
          Requester: AuthorizationIdentity
          Correlation: CorrelationContext
          Reason: string
          RequestedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module UserMemoryForgetRequest =
    let create authorization correlation memoryId reason requestedAt =
        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box correlation) then
            nullArg (nameof correlation)

        if AuthorizationScope.sessionId authorization |> Option.isNone then
            invalidArg (nameof authorization) "User memory forgetting requires an authenticated session."

        if String.IsNullOrWhiteSpace reason || reason <> reason.Trim() then
            invalidArg (nameof reason) "User memory forgetting requires a non-blank trimmed reason."

        { MemoryId = memoryId
          Requester = AuthorizationScope.identity authorization
          Correlation = correlation
          Reason = reason
          RequestedAt = requestedAt }

    let memoryId value = value.MemoryId
    let requester value = value.Requester
    let correlation value = value.Correlation
    let reason value = value.Reason
    let requestedAt value = value.RequestedAt

type UserMemoryForgetEvidence =
    private
        { Requester: AuthorizationIdentity
          Correlation: CorrelationContext
          Reason: string
          RequestedAt: DateTimeOffset
          AppliedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module UserMemoryForgetEvidence =
    let requester value = value.Requester
    let correlation value = value.Correlation
    let reason value = value.Reason
    let requestedAt value = value.RequestedAt
    let appliedAt value = value.AppliedAt

[<RequireQualifiedAccess>]
type DurableMemoryState =
    | Active of DurableMemoryValue
    | Contested of DurableMemoryConflict
    | Expired of expiredAt: DateTimeOffset
    | Forgotten of UserMemoryForgetEvidence

type DurableMemoryRecord =
    private
        { Id: Guid
          Owner: AuthorizationIdentity
          Key: string option
          Version: int
          State: DurableMemoryState
          UpdatedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module DurableMemoryRecord =
    let id value = value.Id
    let owner value = value.Owner
    let key value = value.Key
    let version value = value.Version
    let state value = value.State
    let updatedAt value = value.UpdatedAt

[<RequireQualifiedAccess>]
type DurableMemoryLifecycleResult =
    | Added of DurableMemoryRecord
    | Reinforced of DurableMemoryRecord
    | Contested of DurableMemoryRecord
    | Corrected of DurableMemoryRecord
    | Expired of DurableMemoryRecord
    | Forgotten of DurableMemoryRecord

[<RequireQualifiedAccess>]
module DurableMemoryLifecycleResult =
    let record =
        function
        | DurableMemoryLifecycleResult.Added value
        | DurableMemoryLifecycleResult.Reinforced value
        | DurableMemoryLifecycleResult.Contested value
        | DurableMemoryLifecycleResult.Corrected value
        | DurableMemoryLifecycleResult.Expired value
        | DurableMemoryLifecycleResult.Forgotten value -> value

[<RequireQualifiedAccess>]
module DurableMemoryLifecycle =
    let private hash (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private sourceEvent batch =
        batch
        |> BackgroundMemoryExtractionBatch.source
        |> CrossSessionUserMemoryCandidate.candidate
        |> DurableSessionMemoryCandidate.event

    let private acceptedEvidence batch assessment =
        if
            batch
            |> BackgroundMemoryExtractionBatch.accepted
            |> List.contains assessment
            |> not
        then
            invalidArg (nameof assessment) "Only accepted proposals from the supplied extraction batch may persist."

        { Batch = batch
          Assessment = assessment }

    let private memoryValue
        (policy: DurableMemoryLifecyclePolicy)
        (at: DateTimeOffset)
        (evidence: DurableMemorySourceEvidence)
        =
        let proposal =
            evidence
            |> DurableMemorySourceEvidence.assessment
            |> MemoryExtractionAssessment.proposal

        let value = MemoryExtractionProposal.value proposal

        { Value = value
          ValueHash = hash value
          Confidence = MemoryExtractionProposal.confidence proposal
          Sources = [ evidence ]
          ExpiresAt = at.Add(DurableMemoryLifecyclePolicy.retention policy) }

    let private requireOwner authorization owner =
        if AuthorizationScope.identity authorization <> owner then
            raise (UnauthorizedAccessException "Durable memory lifecycle operations require the exact owner.")

    let private requireVersion expectedVersion record =
        if expectedVersion <> record.Version then
            invalidOp
                $"Durable memory version conflict: expected {expectedVersion}, current version is {record.Version}."

    let introduce authorization policy at batch assessment =
        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box policy) then
            nullArg (nameof policy)

        if isNull (box batch) then
            nullArg (nameof batch)

        let owner =
            batch
            |> BackgroundMemoryExtractionBatch.source
            |> CrossSessionUserMemoryCandidate.owner

        requireOwner authorization owner
        let evidence = acceptedEvidence batch assessment
        let proposal = MemoryExtractionAssessment.proposal assessment
        let memory = memoryValue policy at evidence

        DurableMemoryLifecycleResult.Added
            { Id = Guid.NewGuid()
              Owner = owner
              Key = Some(MemoryExtractionProposal.key proposal)
              Version = 1
              State = DurableMemoryState.Active memory
              UpdatedAt = at }

    let applyExtracted authorization policy at expectedVersion batch assessment record =
        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box policy) then
            nullArg (nameof policy)

        if isNull (box batch) then
            nullArg (nameof batch)

        if isNull (box record) then
            nullArg (nameof record)

        requireOwner authorization record.Owner
        requireVersion expectedVersion record

        let batchOwner =
            batch
            |> BackgroundMemoryExtractionBatch.source
            |> CrossSessionUserMemoryCandidate.owner

        if batchOwner <> record.Owner then
            raise (UnauthorizedAccessException "Extracted memory ownership does not match the durable record.")

        let evidence = acceptedEvidence batch assessment
        let proposal = MemoryExtractionAssessment.proposal assessment
        let proposalKey = MemoryExtractionProposal.key proposal

        if
            record.Key
            |> Option.exists (fun key -> not (String.Equals(key, proposalKey, StringComparison.OrdinalIgnoreCase)))
        then
            invalidArg (nameof assessment) "Extracted memory keys must match the durable record key."

        let next = memoryValue policy at evidence
        let eventKind = batch |> sourceEvent |> SessionMemoryEvent.kind

        let updated state result =
            result
                { record with
                    Version = record.Version + 1
                    State = state
                    UpdatedAt = at }

        match record.State with
        | DurableMemoryState.Expired _
        | DurableMemoryState.Forgotten _ -> invalidOp "Expired or forgotten durable memory records are terminal."
        | DurableMemoryState.Active current when current.Value = next.Value ->
            let sources =
                (current.Sources @ next.Sources)
                |> List.rev
                |> List.truncate (DurableMemoryLifecyclePolicy.maximumProvenanceEntries policy)
                |> List.rev

            let confidence =
                if
                    MemoryExtractionConfidence.value next.Confidence > MemoryExtractionConfidence.value
                        current.Confidence
                then
                    next.Confidence
                else
                    current.Confidence

            DurableMemoryState.Active
                { current with
                    Confidence = confidence
                    Sources = sources
                    ExpiresAt = next.ExpiresAt }
            |> updated
            <| DurableMemoryLifecycleResult.Reinforced
        | DurableMemoryState.Active _ when eventKind = SessionMemoryEventKind.UserCorrection ->
            DurableMemoryState.Active next |> updated
            <| DurableMemoryLifecycleResult.Corrected
        | DurableMemoryState.Active current ->
            DurableMemoryState.Contested
                { Alternatives = [ current; next ]
                  DetectedAt = at
                  ExpiresAt = max current.ExpiresAt next.ExpiresAt }
            |> updated
            <| DurableMemoryLifecycleResult.Contested
        | DurableMemoryState.Contested _ when eventKind = SessionMemoryEventKind.UserCorrection ->
            DurableMemoryState.Active next |> updated
            <| DurableMemoryLifecycleResult.Corrected
        | DurableMemoryState.Contested conflict ->
            let alternatives =
                if conflict.Alternatives |> List.exists (fun value -> value.Value = next.Value) then
                    conflict.Alternatives
                else
                    conflict.Alternatives @ [ next ]

            DurableMemoryState.Contested
                { Alternatives = alternatives
                  DetectedAt = conflict.DetectedAt
                  ExpiresAt = max conflict.ExpiresAt next.ExpiresAt }
            |> updated
            <| DurableMemoryLifecycleResult.Contested

    let expire authorization at expectedVersion record =
        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box record) then
            nullArg (nameof record)

        requireOwner authorization record.Owner
        requireVersion expectedVersion record

        let expiresAt =
            match record.State with
            | DurableMemoryState.Active value -> value.ExpiresAt
            | DurableMemoryState.Contested conflict -> conflict.ExpiresAt
            | DurableMemoryState.Expired _
            | DurableMemoryState.Forgotten _ -> invalidOp "The durable memory record is already terminal."

        if at < expiresAt then
            invalidArg (nameof at) "Durable memory cannot expire before its retention deadline."

        DurableMemoryLifecycleResult.Expired
            { record with
                Key = None
                Version = record.Version + 1
                State = DurableMemoryState.Expired at
                UpdatedAt = at }

    let forget authorization appliedAt expectedVersion request record =
        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box request) then
            nullArg (nameof request)

        if isNull (box record) then
            nullArg (nameof record)

        requireVersion expectedVersion record

        if
            AuthorizationScope.identity authorization
            <> UserMemoryForgetRequest.requester request
        then
            raise (UnauthorizedAccessException "User memory forgetting requires the exact requesting session.")

        let requesterOwner =
            { UserMemoryForgetRequest.requester request with
                SessionId = None }

        if requesterOwner <> record.Owner then
            raise (UnauthorizedAccessException "User memory forgetting cannot cross an ownership boundary.")

        if UserMemoryForgetRequest.memoryId request <> record.Id then
            invalidArg (nameof request) "User memory forgetting targeted a different durable memory record."

        if UserMemoryForgetRequest.requestedAt request > appliedAt then
            invalidArg (nameof appliedAt) "User memory forgetting cannot be applied before it is requested."

        match record.State with
        | DurableMemoryState.Forgotten _ -> invalidOp "The durable memory record is already forgotten."
        | _ ->
            DurableMemoryLifecycleResult.Forgotten
                { record with
                    Key = None
                    Version = record.Version + 1
                    State =
                        DurableMemoryState.Forgotten
                            { Requester = UserMemoryForgetRequest.requester request
                              Correlation = UserMemoryForgetRequest.correlation request
                              Reason = UserMemoryForgetRequest.reason request
                              RequestedAt = UserMemoryForgetRequest.requestedAt request
                              AppliedAt = appliedAt }
                    UpdatedAt = appliedAt }
