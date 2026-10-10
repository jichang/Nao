namespace Nao.Knowledge

open System
open System.Collections.Generic
open System.Text
open Nao.Agents

[<RequireQualifiedAccess>]
type KnowledgeContextSourcePreferencePriority =
    | AuthorityThenCurrent
    | CurrentThenAuthority

type KnowledgeContextSourcePreferencePolicy =
    private
        { Priority: KnowledgeContextSourcePreferencePriority }

[<RequireQualifiedAccess>]
module KnowledgeContextSourcePreferencePolicy =
    let create priority = { Priority = priority }
    let priority value = value.Priority

type KnowledgeContextAssemblyConfig =
    private
        { Tokenizer: KnowledgeTokenizer
          SourcePreference: KnowledgeContextSourcePreferencePolicy
          MaximumTokens: int
          MaximumCandidates: int
          MaximumPerSource: int
          MinimumCandidates: int }

[<RequireQualifiedAccess>]
module KnowledgeContextAssemblyConfig =
    let create tokenizer sourcePreference maximumTokens maximumCandidates maximumPerSource minimumCandidates =
        if isNull (box tokenizer) || isNull (box tokenizer.Tokenize) then
            nullArg (nameof tokenizer)

        if isNull (box sourcePreference) then
            nullArg (nameof sourcePreference)

        if maximumTokens <= 0 then
            invalidArg (nameof maximumTokens) "Context token limit must be positive."

        if maximumCandidates <= 0 then
            invalidArg (nameof maximumCandidates) "Context candidate limit must be positive."

        if maximumPerSource <= 0 || maximumPerSource > maximumCandidates then
            invalidArg (nameof maximumPerSource) "Context per-source limit must fit the candidate limit."

        if minimumCandidates <= 0 || minimumCandidates > maximumCandidates then
            invalidArg (nameof minimumCandidates) "Context minimum candidates must fit the candidate limit."

        { Tokenizer = tokenizer
          SourcePreference = sourcePreference
          MaximumTokens = maximumTokens
          MaximumCandidates = maximumCandidates
          MaximumPerSource = maximumPerSource
          MinimumCandidates = minimumCandidates }

    let tokenizer value = value.Tokenizer
    let sourcePreference value = value.SourcePreference
    let maximumTokens value = value.MaximumTokens
    let maximumCandidates value = value.MaximumCandidates
    let maximumPerSource value = value.MaximumPerSource
    let minimumCandidates value = value.MinimumCandidates

[<RequireQualifiedAccess>]
type KnowledgeContextSuppressionReason =
    | CandidateLimit
    | SourceLimit of KnowledgeSourceId
    | TokenLimit
    | StaleDocumentVersion of documentId: KnowledgeDocumentId * candidateVersion: int * currentVersion: int
    | ConflictingDocumentSources of documentId: KnowledgeDocumentId * sourceIds: KnowledgeSourceId list
    | ConflictingDocumentVersion of
        documentId: KnowledgeDocumentId *
        documentVersion: int *
        versionIds: KnowledgeDocumentVersionId list

[<RequireQualifiedAccess>]
type KnowledgeContextCandidateOutcome =
    | Included of contextRank: int
    | Suppressed of KnowledgeContextSuppressionReason

[<Struct>]
type KnowledgeContextPreferenceEvidence =
    private
        { Priority: KnowledgeContextSourcePreferencePriority
          SourceTrust: TrustAssessment
          VersionTrust: TrustAssessment
          EffectiveTrust: KnowledgeTrust
          Freshness: DateTimeOffset
          RerankerRank: int
          PreferenceRank: int }

[<RequireQualifiedAccess>]
module KnowledgeContextPreferenceEvidence =
    let priority value = value.Priority
    let sourceTrust value = value.SourceTrust
    let versionTrust value = value.VersionTrust
    let effectiveTrust value = value.EffectiveTrust
    let freshness value = value.Freshness
    let rerankerRank value = value.RerankerRank
    let preferenceRank value = value.PreferenceRank

type KnowledgeContextCandidateDecision =
    private
        { Candidate: KnowledgeRerankedCandidate
          Preference: KnowledgeContextPreferenceEvidence
          Outcome: KnowledgeContextCandidateOutcome }

[<RequireQualifiedAccess>]
module KnowledgeContextCandidateDecision =
    let candidate value = value.Candidate
    let preference value = value.Preference
    let outcome value = value.Outcome

type KnowledgeContextCitation =
    private
        { Marker: string
          SourceId: KnowledgeSourceId
          DocumentId: KnowledgeDocumentId
          DocumentVersion: int
          DocumentVersionId: KnowledgeDocumentVersionId
          ChunkId: KnowledgeChunkId
          Locations: SourceLocation list
          StatementOffset: int
          StatementLength: int
          QuotedContentHash: ContentHash }

[<RequireQualifiedAccess>]
module KnowledgeContextCitation =
    let marker value = value.Marker
    let sourceId value = value.SourceId
    let documentId value = value.DocumentId
    let documentVersion value = value.DocumentVersion
    let documentVersionId value = value.DocumentVersionId
    let chunkId value = value.ChunkId
    let locations value = value.Locations
    let statementOffset value = value.StatementOffset
    let statementLength value = value.StatementLength
    let quotedContentHash value = value.QuotedContentHash

type KnowledgeContextItem =
    private
        { Candidate: KnowledgeRerankedCandidate
          Citation: KnowledgeContextCitation
          Text: string
          TokenCount: int }

[<RequireQualifiedAccess>]
module KnowledgeContextItem =
    let candidate value = value.Candidate
    let citation value = value.Citation

    let marker value =
        value.Citation |> KnowledgeContextCitation.marker

    let text value = value.Text
    let tokenCount value = value.TokenCount

[<RequireQualifiedAccess>]
type KnowledgeContextInsufficiency =
    | NoCandidates
    | BelowMinimumCandidates of required: int * assembled: int

type KnowledgeAssembledContext =
    private
        { Assembler: ComponentVersion
          Reranking: KnowledgeReranking
          Config: KnowledgeContextAssemblyConfig
          Items: KnowledgeContextItem list
          Decisions: KnowledgeContextCandidateDecision list
          Text: string
          TokenCount: int }

[<RequireQualifiedAccess>]
module KnowledgeAssembledContext =
    let assembler value = value.Assembler
    let reranking value = value.Reranking
    let config value = value.Config
    let items value = value.Items
    let decisions value = value.Decisions
    let text value = value.Text
    let tokenCount value = value.TokenCount

[<RequireQualifiedAccess>]
type KnowledgeContextAssemblyResult =
    | Assembled of KnowledgeAssembledContext
    | Insufficient of KnowledgeContextInsufficiency * KnowledgeContextCandidateDecision list

[<RequireQualifiedAccess>]
module KnowledgeContextAssembler =
    let descriptor = ComponentVersion.create "nao-context-assembler" "1"

    let private render marker text =
        String.concat Environment.NewLine [ marker; text ]

    let private trustRank =
        function
        | KnowledgeTrust.Unverified -> 0
        | KnowledgeTrust.SourceAsserted -> 1
        | KnowledgeTrust.OrganizationVerified -> 2
        | KnowledgeTrust.Authoritative -> 3

    let private preferenceInput candidate =
        let document = KnowledgeRerankedCandidate.document candidate
        let sourceTrust = KnowledgeIndexDocument.sourceTrust document
        let versionTrust = KnowledgeIndexDocument.versionTrust document

        let effectiveTrust =
            if trustRank sourceTrust.Level <= trustRank versionTrust.Level then
                sourceTrust.Level
            else
                versionTrust.Level

        let freshness =
            KnowledgeIndexDocument.sourceModifiedAt document
            |> Option.defaultValue (KnowledgeIndexDocument.ingestedAt document)

        candidate, sourceTrust, versionTrust, effectiveTrust, freshness

    let assemble
        (authorization: AuthorizationScope)
        (config: KnowledgeContextAssemblyConfig)
        (reranking: KnowledgeReranking)
        =
        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box config) then
            nullArg (nameof config)

        if isNull (box reranking) then
            nullArg (nameof reranking)

        let request =
            reranking
            |> KnowledgeReranking.request
            |> KnowledgeRerankRequest.retrievalRequest

        if not (obj.ReferenceEquals(authorization, KnowledgeRetrievalRequest.authorization request)) then
            invalidOp "Context assembly requires the exact retrieval authorization scope."

        let tokenizer = KnowledgeContextAssemblyConfig.tokenizer config
        let items = ResizeArray<KnowledgeContextItem>()
        let decisions = ResizeArray<KnowledgeContextCandidateDecision>()
        let sourceCounts = Dictionary<KnowledgeSourceId, int>()
        let mutable rendered = ""
        let mutable tokenCount = 0

        let rerankedCandidates = KnowledgeReranking.candidates reranking

        rerankedCandidates
        |> List.iter (
            KnowledgeRerankedCandidate.document
            >> KnowledgeRetrievalBoundary.validateDocument request
        )

        let documents = rerankedCandidates |> List.map KnowledgeRerankedCandidate.document

        let currentVersions =
            documents
            |> List.groupBy KnowledgeIndexDocument.documentId
            |> List.map (fun (documentId, grouped) ->
                documentId, grouped |> List.map KnowledgeIndexDocument.documentVersion |> List.max)
            |> Map.ofList

        let conflictingSources =
            documents
            |> List.groupBy KnowledgeIndexDocument.documentId
            |> List.choose (fun (documentId, grouped) ->
                let sourceIds =
                    grouped
                    |> List.map KnowledgeIndexDocument.sourceId
                    |> List.distinct
                    |> List.sortBy KnowledgeSourceId.value

                if sourceIds.Length > 1 then
                    Some(documentId, sourceIds)
                else
                    None)
            |> Map.ofList

        let conflictingVersions =
            documents
            |> List.groupBy (fun document ->
                KnowledgeIndexDocument.documentId document, KnowledgeIndexDocument.documentVersion document)
            |> List.choose (fun (key, grouped) ->
                let versionIds =
                    grouped
                    |> List.map KnowledgeIndexDocument.documentVersionId
                    |> List.distinct
                    |> List.sortBy KnowledgeDocumentVersionId.value

                if versionIds.Length > 1 then
                    Some(key, versionIds)
                else
                    None)
            |> Map.ofList

        let count sourceId =
            match sourceCounts.TryGetValue sourceId with
            | true, value -> value
            | false, _ -> 0

        let priority =
            config
            |> KnowledgeContextAssemblyConfig.sourcePreference
            |> KnowledgeContextSourcePreferencePolicy.priority

        let candidates =
            rerankedCandidates
            |> List.map preferenceInput
            |> List.sortBy (fun (candidate, _, _, effectiveTrust, freshness) ->
                match priority with
                | KnowledgeContextSourcePreferencePriority.AuthorityThenCurrent ->
                    int64 (-trustRank effectiveTrust),
                    -freshness.UtcDateTime.Ticks,
                    KnowledgeRerankedCandidate.rank candidate
                | KnowledgeContextSourcePreferencePriority.CurrentThenAuthority ->
                    -freshness.UtcDateTime.Ticks,
                    int64 (-trustRank effectiveTrust),
                    KnowledgeRerankedCandidate.rank candidate)
            |> List.mapi (fun index (candidate, sourceTrust, versionTrust, effectiveTrust, freshness) ->
                candidate,
                { Priority = priority
                  SourceTrust = sourceTrust
                  VersionTrust = versionTrust
                  EffectiveTrust = effectiveTrust
                  Freshness = freshness
                  RerankerRank = KnowledgeRerankedCandidate.rank candidate
                  PreferenceRank = index + 1 })

        candidates
        |> List.iter (fun (candidate, preference) ->
            let document = KnowledgeRerankedCandidate.document candidate
            let sourceId = KnowledgeIndexDocument.sourceId document
            let documentId = KnowledgeIndexDocument.documentId document
            let documentVersion = KnowledgeIndexDocument.documentVersion document

            let outcome =
                match Map.tryFind documentId conflictingSources with
                | Some sourceIds ->
                    KnowledgeContextCandidateOutcome.Suppressed(
                        KnowledgeContextSuppressionReason.ConflictingDocumentSources(documentId, sourceIds)
                    )
                | None when Map.containsKey (documentId, documentVersion) conflictingVersions ->
                    KnowledgeContextCandidateOutcome.Suppressed(
                        KnowledgeContextSuppressionReason.ConflictingDocumentVersion(
                            documentId,
                            documentVersion,
                            conflictingVersions[documentId, documentVersion]
                        )
                    )
                | None when documentVersion < currentVersions[documentId] ->
                    KnowledgeContextCandidateOutcome.Suppressed(
                        KnowledgeContextSuppressionReason.StaleDocumentVersion(
                            documentId,
                            documentVersion,
                            currentVersions[documentId]
                        )
                    )
                | None when items.Count >= KnowledgeContextAssemblyConfig.maximumCandidates config ->
                    KnowledgeContextCandidateOutcome.Suppressed KnowledgeContextSuppressionReason.CandidateLimit
                | None when count sourceId >= KnowledgeContextAssemblyConfig.maximumPerSource config ->
                    KnowledgeContextCandidateOutcome.Suppressed(KnowledgeContextSuppressionReason.SourceLimit sourceId)
                | None ->
                    let marker =
                        $"[nao-citation:{document |> KnowledgeIndexDocument.chunkId |> KnowledgeChunkId.value}]"

                    let itemText = render marker (KnowledgeIndexDocument.text document)

                    let candidateRendered =
                        if String.IsNullOrEmpty rendered then
                            itemText
                        else
                            String.concat (Environment.NewLine + Environment.NewLine) [ rendered; itemText ]

                    let candidateTokens = tokenizer.Tokenize candidateRendered |> List.length

                    if candidateTokens > KnowledgeContextAssemblyConfig.maximumTokens config then
                        KnowledgeContextCandidateOutcome.Suppressed KnowledgeContextSuppressionReason.TokenLimit
                    else
                        let rank = items.Count + 1
                        let itemTokens = tokenizer.Tokenize itemText |> List.length

                        let itemOffset =
                            if String.IsNullOrEmpty rendered then
                                0
                            else
                                rendered.Length + (Environment.NewLine + Environment.NewLine).Length

                        let statementOffset = itemOffset + marker.Length + Environment.NewLine.Length

                        let citation =
                            { Marker = marker
                              SourceId = sourceId
                              DocumentId = documentId
                              DocumentVersion = documentVersion
                              DocumentVersionId = KnowledgeIndexDocument.documentVersionId document
                              ChunkId = KnowledgeIndexDocument.chunkId document
                              Locations = KnowledgeIndexDocument.locations document
                              StatementOffset = statementOffset
                              StatementLength = (KnowledgeIndexDocument.text document).Length
                              QuotedContentHash = KnowledgeIndexDocument.contentHash document }

                        items.Add
                            { Candidate = candidate
                              Citation = citation
                              Text = KnowledgeIndexDocument.text document
                              TokenCount = itemTokens }

                        sourceCounts[sourceId] <- count sourceId + 1
                        rendered <- candidateRendered
                        tokenCount <- candidateTokens
                        KnowledgeContextCandidateOutcome.Included rank

            decisions.Add
                { Candidate = candidate
                  Preference = preference
                  Outcome = outcome })

        let decisions = List.ofSeq decisions

        if items.Count = 0 then
            KnowledgeContextAssemblyResult.Insufficient(KnowledgeContextInsufficiency.NoCandidates, decisions)
        elif items.Count < KnowledgeContextAssemblyConfig.minimumCandidates config then
            KnowledgeContextAssemblyResult.Insufficient(
                KnowledgeContextInsufficiency.BelowMinimumCandidates(
                    KnowledgeContextAssemblyConfig.minimumCandidates config,
                    items.Count
                ),
                decisions
            )
        else
            KnowledgeContextAssemblyResult.Assembled
                { Assembler = descriptor
                  Reranking = reranking
                  Config = config
                  Items = List.ofSeq items
                  Decisions = decisions
                  Text = rendered
                  TokenCount = tokenCount }
