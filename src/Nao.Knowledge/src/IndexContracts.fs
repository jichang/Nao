namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<Struct>]
type KnowledgeIndexNamespace = private KnowledgeIndexNamespace of string

[<RequireQualifiedAccess>]
module KnowledgeIndexNamespace =
    let tryParse (value: string) =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            None
        else
            Some(KnowledgeIndexNamespace value)

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () ->
            invalidArg (nameof value) "Index namespace must be non-blank and have no surrounding whitespace.")

    let value (KnowledgeIndexNamespace value) = value

type KnowledgeIndexAccess =
    private
        { Users: Set<UserId>
          Groups: Set<GroupId>
          Roles: Set<string> }

[<RequireQualifiedAccess>]
module KnowledgeIndexAccess =
    let private validateRoles roles =
        if
            roles
            |> Set.exists (fun role -> String.IsNullOrWhiteSpace role || role <> role.Trim())
        then
            invalidArg (nameof roles) "Index ACL roles must be non-blank and have no surrounding whitespace."

        roles

    let ownerOnly =
        { Users = Set.empty
          Groups = Set.empty
          Roles = Set.empty }

    let create users groups roles =
        { Users = Set.ofSeq users
          Groups = Set.ofSeq groups
          Roles = roles |> Set.ofSeq |> validateRoles }

    let users value = value.Users
    let groups value = value.Groups
    let roles value = value.Roles

    let allows (authorization: AuthorizationScope) owner access =
        let identity = AuthorizationScope.identity authorization
        let principal = AuthorizationScope.principal authorization

        let sameBoundary =
            identity.TenantId = owner.TenantId
            && identity.WorkspaceId = owner.WorkspaceId
            && (owner.SessionId
                |> Option.forall (fun sessionId -> identity.SessionId = Some sessionId))

        sameBoundary
        && (identity = owner
            || access.Users.Contains identity.UserId
            || not (Set.intersect access.Groups (SecurityPrincipal.groupIds principal)).IsEmpty
            || not (Set.intersect access.Roles (SecurityPrincipal.roles principal)).IsEmpty)

type KnowledgeIndexDocument =
    private
        { Namespace: KnowledgeIndexNamespace
          Owner: AuthorizationIdentity
          Access: KnowledgeIndexAccess
          SourceId: KnowledgeSourceId
          DocumentVersionId: KnowledgeDocumentVersionId
          ChunkId: KnowledgeChunkId
          ContentHash: ContentHash
          Text: string
          Metadata: Map<string, string>
          SourceModifiedAt: DateTimeOffset option
          IngestedAt: DateTimeOffset
          IndexedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeIndexDocument =
    let private prefixed prefix metadata =
        metadata |> Map.toSeq |> Seq.map (fun (key, value) -> prefix + key, value)

    let private validateMetadata metadata =
        metadata
        |> Map.iter (fun key value ->
            if String.IsNullOrWhiteSpace key || key <> key.Trim() then
                invalidArg (nameof metadata) "Index metadata keys must be non-blank and trimmed."

            if isNull value then
                nullArg (nameof metadata))

        metadata

    let create
        namespaceName
        access
        (source: KnowledgeSource)
        (version: KnowledgeDocumentVersion)
        (chunk: KnowledgeChunk)
        indexedAt
        =
        if version.SourceId <> source.Id then
            invalidArg (nameof version) "Indexed document version does not belong to the source."

        if chunk.DocumentVersionId <> version.Id then
            invalidArg (nameof chunk) "Indexed chunk does not belong to the document version."

        match source.Lifecycle.DeletionStatus, version.Lifecycle.DeletionStatus, chunk.Lifecycle.DeletionStatus with
        | KnowledgeDeletionStatus.Active, KnowledgeDeletionStatus.Active, KnowledgeDeletionStatus.Active -> ()
        | _ -> invalidArg (nameof chunk) "Deleted knowledge cannot enter a searchable index."

        let metadata =
            Seq.concat
                [ prefixed "source." source.Metadata
                  prefixed "version." version.Metadata
                  prefixed "chunk." chunk.Metadata ]
            |> Map.ofSeq

        { Namespace = namespaceName
          Owner = source.Owner
          Access = access
          SourceId = source.Id
          DocumentVersionId = version.Id
          ChunkId = chunk.Id
          ContentHash = chunk.ContentHash
          Text = chunk.Text
          Metadata = metadata
          SourceModifiedAt = version.SourceModifiedAt
          IngestedAt = version.IngestedAt
          IndexedAt = indexedAt }

    let restore
        namespaceName
        owner
        access
        sourceId
        documentVersionId
        chunkId
        contentHash
        text
        metadata
        sourceModifiedAt
        ingestedAt
        indexedAt
        =
        if isNull text then
            nullArg (nameof text)

        if ContentHash.computeText text <> contentHash then
            invalidArg (nameof contentHash) "Restored index text does not match its content hash."

        if indexedAt < ingestedAt then
            invalidArg (nameof indexedAt) "Index timestamp cannot precede ingestion."

        { Namespace = namespaceName
          Owner = owner
          Access = access
          SourceId = sourceId
          DocumentVersionId = documentVersionId
          ChunkId = chunkId
          ContentHash = contentHash
          Text = text
          Metadata = validateMetadata metadata
          SourceModifiedAt = sourceModifiedAt
          IngestedAt = ingestedAt
          IndexedAt = indexedAt }

    let namespaceName value = value.Namespace
    let owner value = value.Owner
    let access value = value.Access
    let sourceId value = value.SourceId
    let documentVersionId value = value.DocumentVersionId
    let chunkId value = value.ChunkId
    let contentHash value = value.ContentHash
    let text value = value.Text
    let metadata value = value.Metadata
    let sourceModifiedAt value = value.SourceModifiedAt
    let ingestedAt value = value.IngestedAt
    let indexedAt value = value.IndexedAt

type KnowledgeIndexFilter =
    private
        { SourceIds: Set<KnowledgeSourceId>
          DocumentVersionIds: Set<KnowledgeDocumentVersionId>
          MetadataEquals: Map<string, string>
          SourceModifiedFrom: DateTimeOffset option
          SourceModifiedUntil: DateTimeOffset option
          IngestedFrom: DateTimeOffset option
          IngestedUntil: DateTimeOffset option }

[<RequireQualifiedAccess>]
module KnowledgeIndexFilter =
    let private validateRange name fromValue untilValue =
        match fromValue, untilValue with
        | Some lower, Some upper when upper < lower ->
            invalidArg name "Index filter upper time bound must not precede its lower bound."
        | _ -> ()

    let private validateMetadata metadata =
        metadata
        |> Map.iter (fun key value ->
            if String.IsNullOrWhiteSpace key || key <> key.Trim() then
                invalidArg (nameof metadata) "Metadata filter keys must be non-blank and trimmed."

            if isNull value then
                nullArg (nameof metadata))

        metadata

    let create
        sourceIds
        documentVersionIds
        metadataEquals
        sourceModifiedFrom
        sourceModifiedUntil
        ingestedFrom
        ingestedUntil
        =
        validateRange "source modification range" sourceModifiedFrom sourceModifiedUntil
        validateRange "ingestion range" ingestedFrom ingestedUntil

        { SourceIds = Set.ofSeq sourceIds
          DocumentVersionIds = Set.ofSeq documentVersionIds
          MetadataEquals = validateMetadata metadataEquals
          SourceModifiedFrom = sourceModifiedFrom
          SourceModifiedUntil = sourceModifiedUntil
          IngestedFrom = ingestedFrom
          IngestedUntil = ingestedUntil }

    let none = create [] [] Map.empty None None None None
    let sourceIds value = value.SourceIds
    let documentVersionIds value = value.DocumentVersionIds
    let metadataEquals value = value.MetadataEquals
    let sourceModifiedFrom value = value.SourceModifiedFrom
    let sourceModifiedUntil value = value.SourceModifiedUntil
    let ingestedFrom value = value.IngestedFrom
    let ingestedUntil value = value.IngestedUntil

type VectorIndexRecord =
    private
        { Document: KnowledgeIndexDocument
          Profile: EmbeddingCapabilityDescriptor
          Values: float array }

[<RequireQualifiedAccess>]
module VectorIndexRecord =
    let create document embedding =
        if PersistedEmbedding.owner embedding <> KnowledgeIndexDocument.owner document then
            invalidArg (nameof embedding) "Embedding owner does not match the indexed document."

        if
            PersistedEmbedding.chunkId embedding <> KnowledgeIndexDocument.chunkId document
            || PersistedEmbedding.contentHash embedding
               <> KnowledgeIndexDocument.contentHash document
        then
            invalidArg (nameof embedding) "Embedding identity does not match the indexed document."

        { Document = document
          Profile = PersistedEmbedding.profile embedding
          Values = PersistedEmbedding.values embedding }

    let document value = value.Document
    let profile value = value.Profile
    let values value = Array.copy value.Values

type VectorIndexQuery =
    private
        { Namespace: KnowledgeIndexNamespace
          Profile: EmbeddingCapabilityDescriptor
          Values: float array
          Filter: KnowledgeIndexFilter
          Limit: int }

[<RequireQualifiedAccess>]
module VectorIndexQuery =
    let create namespaceName profile (values: float array) filter limit =
        let profile = EmbeddingCapabilityDescriptor.validate profile

        if isNull values || values.Length <> profile.Dimensions then
            invalidArg (nameof values) "Vector query dimensions do not match the embedding profile."

        if values |> Array.exists (Double.IsFinite >> not) then
            invalidArg (nameof values) "Vector query contains a non-finite value."

        if limit <= 0 then
            invalidArg (nameof limit) "Vector query limit must be positive."

        { Namespace = namespaceName
          Profile = profile
          Values = Array.copy values
          Filter = filter
          Limit = limit }

    let namespaceName value = value.Namespace
    let profile value = value.Profile
    let values value = Array.copy value.Values
    let filter value = value.Filter
    let limit value = value.Limit

type VectorIndexHit =
    private
        { Document: KnowledgeIndexDocument
          ProfileId: EmbeddingProfileId
          Score: float }

[<RequireQualifiedAccess>]
module VectorIndexHit =
    let create document profileId score =
        if not (Double.IsFinite score) then
            invalidArg (nameof score) "Vector index score must be finite."

        { Document = document
          ProfileId = profileId
          Score = score }

    let document value = value.Document
    let profileId value = value.ProfileId
    let score value = value.Score

type LexicalIndexQuery =
    private
        { Namespace: KnowledgeIndexNamespace
          Query: string
          Filter: KnowledgeIndexFilter
          Limit: int }

[<RequireQualifiedAccess>]
module LexicalIndexQuery =
    let create namespaceName query filter limit =
        if String.IsNullOrWhiteSpace query then
            invalidArg (nameof query) "Lexical query must be non-blank."

        if limit <= 0 then
            invalidArg (nameof limit) "Lexical query limit must be positive."

        { Namespace = namespaceName
          Query = query
          Filter = filter
          Limit = limit }

    let namespaceName value = value.Namespace
    let query value = value.Query
    let filter value = value.Filter
    let limit value = value.Limit

type LexicalIndexHit =
    private
        { Document: KnowledgeIndexDocument
          Score: float }

[<RequireQualifiedAccess>]
module LexicalIndexHit =
    let create document score =
        if not (Double.IsFinite score) then
            invalidArg (nameof score) "Lexical index score must be finite."

        { Document = document; Score = score }

    let document value = value.Document
    let score value = value.Score

type KnowledgeGraphEdge =
    private
        { Namespace: KnowledgeIndexNamespace
          Owner: AuthorizationIdentity
          Access: KnowledgeIndexAccess
          FromChunkId: KnowledgeChunkId
          ToChunkId: KnowledgeChunkId
          Kind: string
          Weight: float
          Metadata: Map<string, string> }

[<RequireQualifiedAccess>]
module KnowledgeGraphEdge =
    let create namespaceName owner access fromChunkId toChunkId kind weight metadata =
        if fromChunkId = toChunkId then
            invalidArg (nameof toChunkId) "A graph edge cannot connect a chunk to itself."

        if String.IsNullOrWhiteSpace kind || kind <> kind.Trim() then
            invalidArg (nameof kind) "Graph edge kind must be non-blank and trimmed."

        if not (Double.IsFinite weight) || weight < 0.0 then
            invalidArg (nameof weight) "Graph edge weight must be finite and non-negative."

        let metadata =
            metadata
            |> Map.map (fun key value ->
                if String.IsNullOrWhiteSpace key || key <> key.Trim() then
                    invalidArg (nameof metadata) "Graph metadata keys must be non-blank and trimmed."

                if isNull value then
                    nullArg (nameof metadata)

                value)

        { Namespace = namespaceName
          Owner = owner
          Access = access
          FromChunkId = fromChunkId
          ToChunkId = toChunkId
          Kind = kind
          Weight = weight
          Metadata = metadata }

    let namespaceName value = value.Namespace
    let owner value = value.Owner
    let access value = value.Access
    let fromChunkId value = value.FromChunkId
    let toChunkId value = value.ToChunkId
    let kind value = value.Kind
    let weight value = value.Weight
    let metadata value = value.Metadata

type GraphIndexQuery =
    private
        { Namespace: KnowledgeIndexNamespace
          StartChunkIds: Set<KnowledgeChunkId>
          EdgeKinds: Set<string>
          MaximumDepth: int
          Limit: int }

[<RequireQualifiedAccess>]
module GraphIndexQuery =
    let create namespaceName startChunkIds edgeKinds maximumDepth limit =
        let startChunkIds = Set.ofSeq startChunkIds
        let edgeKinds = Set.ofSeq edgeKinds

        if startChunkIds.IsEmpty then
            invalidArg (nameof startChunkIds) "Graph query requires at least one starting chunk."

        if
            edgeKinds
            |> Set.exists (fun kind -> String.IsNullOrWhiteSpace kind || kind <> kind.Trim())
        then
            invalidArg (nameof edgeKinds) "Graph query edge kinds must be non-blank and trimmed."

        if maximumDepth <= 0 then
            invalidArg (nameof maximumDepth) "Graph query maximum depth must be positive."

        if limit <= 0 then
            invalidArg (nameof limit) "Graph query limit must be positive."

        { Namespace = namespaceName
          StartChunkIds = startChunkIds
          EdgeKinds = edgeKinds
          MaximumDepth = maximumDepth
          Limit = limit }

    let namespaceName value = value.Namespace
    let startChunkIds value = value.StartChunkIds
    let edgeKinds value = value.EdgeKinds
    let maximumDepth value = value.MaximumDepth
    let limit value = value.Limit

type GraphIndexHit =
    private
        { ChunkId: KnowledgeChunkId
          Depth: int
          Score: float
          Path: KnowledgeChunkId list }

[<RequireQualifiedAccess>]
module GraphIndexHit =
    let create chunkId depth score path =
        if depth <= 0 then
            invalidArg (nameof depth) "Graph hit depth must be positive."

        if not (Double.IsFinite score) then
            invalidArg (nameof score) "Graph hit score must be finite."

        if List.isEmpty path || List.last path <> chunkId || path.Length <> depth + 1 then
            invalidArg (nameof path) "Graph hit path must end at the hit chunk and correspond to its depth."

        { ChunkId = chunkId
          Depth = depth
          Score = score
          Path = path }

    let chunkId value = value.ChunkId
    let depth value = value.Depth
    let score value = value.Score
    let path value = value.Path

type KnowledgeIndexMutation<'Record> =
    private
        { Namespace: KnowledgeIndexNamespace
          Upserts: 'Record list
          DeleteChunkIds: Set<KnowledgeChunkId>
          DeleteFilter: KnowledgeIndexFilter option
          RetireSourceIds: Set<KnowledgeSourceId> }

[<RequireQualifiedAccess>]
module KnowledgeIndexMutation =
    let create
        namespaceName
        (upserts: 'Record list)
        (deleteChunkIds: KnowledgeChunkId seq)
        (deleteFilter: KnowledgeIndexFilter option)
        (retireSourceIds: KnowledgeSourceId seq)
        =
        if isNull (box upserts) then
            nullArg (nameof upserts)

        let deleteChunkIds = Set.ofSeq deleteChunkIds
        let retireSourceIds = Set.ofSeq retireSourceIds

        if
            List.isEmpty upserts
            && deleteChunkIds.IsEmpty
            && deleteFilter.IsNone
            && retireSourceIds.IsEmpty
        then
            invalidArg (nameof upserts) "An index mutation must upsert, delete, or retire at least one record."

        { Namespace = namespaceName
          Upserts = upserts
          DeleteChunkIds = deleteChunkIds
          DeleteFilter = deleteFilter
          RetireSourceIds = retireSourceIds }

    let namespaceName value = value.Namespace
    let upserts value = value.Upserts
    let deleteChunkIds value = value.DeleteChunkIds
    let deleteFilter value = value.DeleteFilter
    let retireSourceIds value = value.RetireSourceIds

type KnowledgeIndexMutationResult =
    private
        { Namespace: KnowledgeIndexNamespace
          UpsertedRecords: int
          DeletedRecords: int
          RetiredSources: int
          CompletedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeIndexMutationResult =
    let create namespaceName upsertedRecords deletedRecords retiredSources completedAt =
        if upsertedRecords < 0 then
            invalidArg (nameof upsertedRecords) "Upserted index record count cannot be negative."

        if deletedRecords < 0 then
            invalidArg (nameof deletedRecords) "Deleted index record count cannot be negative."

        if retiredSources < 0 then
            invalidArg (nameof retiredSources) "Retired index source count cannot be negative."

        { Namespace = namespaceName
          UpsertedRecords = upsertedRecords
          DeletedRecords = deletedRecords
          RetiredSources = retiredSources
          CompletedAt = completedAt }

    let namespaceName value = value.Namespace
    let upsertedRecords value = value.UpsertedRecords
    let deletedRecords value = value.DeletedRecords
    let retiredSources value = value.RetiredSources
    let completedAt value = value.CompletedAt

type KnowledgeVectorIndex =
    { ApplyAsync:
        AuthorizationScope
            -> KnowledgeIndexMutation<VectorIndexRecord>
            -> CancellationToken
            -> Task<KnowledgeIndexMutationResult>
      QueryAsync: AuthorizationScope -> VectorIndexQuery -> CancellationToken -> Task<VectorIndexHit list> }

type KnowledgeLexicalIndex =
    { ApplyAsync:
        AuthorizationScope
            -> KnowledgeIndexMutation<KnowledgeIndexDocument>
            -> CancellationToken
            -> Task<KnowledgeIndexMutationResult>
      QueryAsync: AuthorizationScope -> LexicalIndexQuery -> CancellationToken -> Task<LexicalIndexHit list> }

type KnowledgeGraphIndex =
    { UpsertEdgesAsync: AuthorizationScope -> KnowledgeGraphEdge list -> CancellationToken -> Task
      DeleteChunksAsync:
          AuthorizationScope -> KnowledgeIndexNamespace -> Set<KnowledgeChunkId> -> CancellationToken -> Task<int>
      QueryAsync: AuthorizationScope -> GraphIndexQuery -> CancellationToken -> Task<GraphIndexHit list> }
