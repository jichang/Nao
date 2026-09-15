namespace Nao.Knowledge

open System
open System.Security.Cryptography
open System.Text
open Nao.Agents

module private Validation =
    let canonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name "Value must be non-blank and have no surrounding whitespace."

        value

    let metadata (values: Map<string, string>) =
        values
        |> Map.iter (fun key value ->
            canonical "metadata key" key |> ignore

            if isNull value then
                invalidArg "metadata value" "Metadata values must not be null.")

        values

    let positive name (value: int) =
        if value <= 0 then
            invalidArg name "Value must be positive."

        value

    let nonNegative name (value: int) =
        if value < 0 then
            invalidArg name "Value must not be negative."

        value

    let nonNegativeInt64 name (value: int64) =
        if value < 0L then
            invalidArg name "Value must not be negative."

        value

module private StableIdentity =
    let private isLowerHex value =
        value
        |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')

    let derive prefix (parts: string list) =
        parts
        |> String.concat "\u001f"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()
        |> sprintf "%s_%s" prefix

    let tryParse prefix constructor (value: string) =
        let expectedPrefix = prefix + "_"

        if
            not (isNull value)
            && value.StartsWith(expectedPrefix, StringComparison.Ordinal)
            && value.Length = expectedPrefix.Length + 64
            && isLowerHex value[expectedPrefix.Length ..]
        then
            Some(constructor value)
        else
            None

    let parse name prefix constructor value =
        tryParse prefix constructor value
        |> Option.defaultWith (fun () -> invalidArg name $"Invalid canonical {prefix} identifier.")

[<Struct>]
type KnowledgeSourceId = private KnowledgeSourceId of string

[<Struct>]
type KnowledgeDocumentId = private KnowledgeDocumentId of string

[<Struct>]
type KnowledgeDocumentVersionId = private KnowledgeDocumentVersionId of string

[<Struct>]
type ContentUnitId = private ContentUnitId of string

[<Struct>]
type KnowledgeChunkId = private KnowledgeChunkId of string

[<Struct>]
type KnowledgeEnrichmentId = private KnowledgeEnrichmentId of string

[<Struct>]
type NearDuplicateId = private NearDuplicateId of string

[<Struct>]
type KnowledgeIndexEntryId = private KnowledgeIndexEntryId of string

[<Struct>]
type RetrievalResultId = private RetrievalResultId of Guid

[<Struct>]
type CitationId = private CitationId of Guid

[<Struct>]
type ContentHash = private ContentHash of string

[<Struct>]
type Confidence = private Confidence of float

[<Struct>]
type KnowledgeSchemaVersion = private KnowledgeSchemaVersion of int

module KnowledgeSourceId =
    let parse value =
        Validation.canonical (nameof value) value |> KnowledgeSourceId

    let value (KnowledgeSourceId value) = value

module KnowledgeDocumentId =
    let parse value =
        Validation.canonical (nameof value) value |> KnowledgeDocumentId

    let value (KnowledgeDocumentId value) = value

module ContentHash =
    let computeBytes (bytes: byte array) =
        if isNull bytes then
            nullArg (nameof bytes)

        bytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()
        |> ContentHash

    let computeText (value: string) =
        if isNull value then
            nullArg (nameof value)

        value |> Encoding.UTF8.GetBytes |> computeBytes

    let tryParse (value: string) =
        if
            not (isNull value)
            && value.Length = 64
            && value
               |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')
        then
            Some(ContentHash value)
        else
            None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Content hash must be canonical lowercase SHA-256.")

    let value (ContentHash value) = value

module KnowledgeDocumentVersionId =
    let derive documentId version contentHash =
        Validation.positive (nameof version) version |> ignore

        StableIdentity.derive
            "dver"
            [ KnowledgeDocumentId.value documentId
              string version
              ContentHash.value contentHash ]
        |> KnowledgeDocumentVersionId

    let value (KnowledgeDocumentVersionId value) = value
    let tryParse = StableIdentity.tryParse "dver" KnowledgeDocumentVersionId

    let parse value =
        StableIdentity.parse (nameof value) "dver" KnowledgeDocumentVersionId value

module ContentUnitId =
    let derive documentVersionId sequence contentHash =
        Validation.nonNegative (nameof sequence) sequence |> ignore

        StableIdentity.derive
            "unit"
            [ KnowledgeDocumentVersionId.value documentVersionId
              string sequence
              ContentHash.value contentHash ]
        |> ContentUnitId

    let value (ContentUnitId value) = value
    let tryParse = StableIdentity.tryParse "unit" ContentUnitId

    let parse value =
        StableIdentity.parse (nameof value) "unit" ContentUnitId value

module KnowledgeChunkId =
    let derive documentVersionId chunker sequence contentHash =
        Validation.nonNegative (nameof sequence) sequence |> ignore

        StableIdentity.derive
            "chunk"
            [ KnowledgeDocumentVersionId.value documentVersionId
              chunker
              string sequence
              ContentHash.value contentHash ]
        |> KnowledgeChunkId

    let value (KnowledgeChunkId value) = value
    let tryParse = StableIdentity.tryParse "chunk" KnowledgeChunkId

    let parse value =
        StableIdentity.parse (nameof value) "chunk" KnowledgeChunkId value

module KnowledgeIndexEntryId =
    let derive indexName chunkId =
        StableIdentity.derive
            "index"
            [ Validation.canonical (nameof indexName) indexName
              KnowledgeChunkId.value chunkId ]
        |> KnowledgeIndexEntryId

    let value (KnowledgeIndexEntryId value) = value
    let tryParse = StableIdentity.tryParse "index" KnowledgeIndexEntryId

    let parse value =
        StableIdentity.parse (nameof value) "index" KnowledgeIndexEntryId value

module RetrievalResultId =
    let generate () = RetrievalResultId(Guid.NewGuid())
    let value (RetrievalResultId value) = value

    let tryParse (value: string) =
        match Guid.TryParse value with
        | true, id -> Some(RetrievalResultId id)
        | _ -> None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid retrieval-result ID.")

    let serialize = value >> _.ToString("D")

module CitationId =
    let generate () = CitationId(Guid.NewGuid())
    let value (CitationId value) = value

    let tryParse (value: string) =
        match Guid.TryParse value with
        | true, id -> Some(CitationId id)
        | _ -> None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid citation ID.")

    let serialize = value >> _.ToString("D")

module Confidence =
    let tryCreate value =
        if Double.IsFinite value && value >= 0.0 && value <= 1.0 then
            Some(Confidence value)
        else
            None

    let create value =
        tryCreate value
        |> Option.defaultWith (fun () ->
            invalidArg (nameof value) "Confidence must be finite and between zero and one.")

    let value (Confidence value) = value

module KnowledgeSchemaVersion =
    let current = KnowledgeSchemaVersion 1
    let value (KnowledgeSchemaVersion value) = value

    let parse value =
        if value = 1 then
            current
        else
            invalidArg (nameof value) "Unsupported knowledge schema version."

type ComponentVersion =
    { ComponentId: string; Version: string }

module ComponentVersion =
    let create componentId version =
        { ComponentId = Validation.canonical (nameof componentId) componentId
          Version = Validation.canonical (nameof version) version }

module KnowledgeEnrichmentId =
    let derive chunkId generator configurationHash kind value (confidence: float) evidenceHashes =
        StableIdentity.derive
            "enrich"
            [ KnowledgeChunkId.value chunkId
              generator.ComponentId
              generator.Version
              ContentHash.value configurationHash
              kind
              value
              confidence.ToString("R", Globalization.CultureInfo.InvariantCulture)
              String.concat "\u001f" evidenceHashes ]
        |> KnowledgeEnrichmentId

    let value (KnowledgeEnrichmentId value) = value
    let tryParse = StableIdentity.tryParse "enrich" KnowledgeEnrichmentId

    let parse value =
        StableIdentity.parse (nameof value) "enrich" KnowledgeEnrichmentId value

module NearDuplicateId =
    let derive detector configurationHash canonicalChunkId duplicateChunkId =
        StableIdentity.derive
            "duplicate"
            [ detector.ComponentId
              detector.Version
              ContentHash.value configurationHash
              KnowledgeChunkId.value canonicalChunkId
              KnowledgeChunkId.value duplicateChunkId ]
        |> NearDuplicateId

    let value (NearDuplicateId value) = value
    let tryParse = StableIdentity.tryParse "duplicate" NearDuplicateId

    let parse value =
        StableIdentity.parse (nameof value) "duplicate" NearDuplicateId value

type SourceAcquisition =
    { Connector: ComponentVersion
      SourceContentHash: ContentHash
      AcquiredAt: DateTimeOffset
      Metadata: Map<string, string> }

module SourceAcquisition =
    let create connector sourceContentHash acquiredAt metadata =
        { Connector = connector
          SourceContentHash = sourceContentHash
          AcquiredAt = acquiredAt
          Metadata = Validation.metadata metadata }

[<RequireQualifiedAccess>]
type KnowledgeTrust =
    | Unverified
    | SourceAsserted
    | OrganizationVerified
    | Authoritative

type TrustAssessment =
    { Level: KnowledgeTrust
      Basis: string
      AssessedAt: DateTimeOffset }

module TrustAssessment =
    let create level basis assessedAt =
        { Level = level
          Basis = Validation.canonical (nameof basis) basis
          AssessedAt = assessedAt }

[<RequireQualifiedAccess>]
type KnowledgeDeletionStatus =
    | Active
    | Tombstoned of deletedAt: DateTimeOffset * reason: string
    | Purged of deletedAt: DateTimeOffset

type KnowledgeLifecycle =
    { RetainUntil: DateTimeOffset option
      LegalHold: bool
      DeletionStatus: KnowledgeDeletionStatus }

module KnowledgeLifecycle =
    let active retainUntil legalHold =
        { RetainUntil = retainUntil
          LegalHold = legalHold
          DeletionStatus = KnowledgeDeletionStatus.Active }

    let tombstone deletedAt reason lifecycle =
        if lifecycle.LegalHold then
            invalidOp "Knowledge under legal hold cannot be tombstoned."

        { lifecycle with
            DeletionStatus = KnowledgeDeletionStatus.Tombstoned(deletedAt, Validation.canonical (nameof reason) reason) }

    let purge deletedAt lifecycle =
        if lifecycle.LegalHold then
            invalidOp "Knowledge under legal hold cannot be purged."

        match lifecycle.DeletionStatus with
        | KnowledgeDeletionStatus.Active -> invalidOp "Knowledge must be tombstoned before it can be purged."
        | KnowledgeDeletionStatus.Tombstoned _
        | KnowledgeDeletionStatus.Purged _ ->
            { lifecycle with
                DeletionStatus = KnowledgeDeletionStatus.Purged deletedAt }

type KnowledgeSource =
    { SchemaVersion: KnowledgeSchemaVersion
      Id: KnowledgeSourceId
      Uri: Uri
      Owner: AuthorizationIdentity
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset
      Classification: DataClassification
      Trust: TrustAssessment
      Metadata: Map<string, string>
      Lifecycle: KnowledgeLifecycle }

module KnowledgeSource =
    let create id (uri: Uri) owner classification trust metadata createdAt lifecycle =
        if isNull uri || not uri.IsAbsoluteUri then
            invalidArg (nameof uri) "Knowledge source URI must be absolute."

        { SchemaVersion = KnowledgeSchemaVersion.current
          Id = id
          Uri = uri
          Owner = owner
          CreatedAt = createdAt
          UpdatedAt = createdAt
          Classification = classification
          Trust = trust
          Metadata = Validation.metadata metadata
          Lifecycle = lifecycle }

type KnowledgeDocument =
    { SchemaVersion: KnowledgeSchemaVersion
      Id: KnowledgeDocumentId
      SourceId: KnowledgeSourceId
      CanonicalUri: Uri
      CreatedAt: DateTimeOffset
      Metadata: Map<string, string>
      Lifecycle: KnowledgeLifecycle }

module KnowledgeDocument =
    let create id sourceId (canonicalUri: Uri) metadata createdAt lifecycle =
        if isNull canonicalUri || not canonicalUri.IsAbsoluteUri then
            invalidArg (nameof canonicalUri) "Document URI must be absolute."

        { SchemaVersion = KnowledgeSchemaVersion.current
          Id = id
          SourceId = sourceId
          CanonicalUri = canonicalUri
          CreatedAt = createdAt
          Metadata = Validation.metadata metadata
          Lifecycle = lifecycle }

type KnowledgeDocumentVersion =
    { SchemaVersion: KnowledgeSchemaVersion
      Id: KnowledgeDocumentVersionId
      DocumentId: KnowledgeDocumentId
      SourceId: KnowledgeSourceId
      Version: int
      ContentHash: ContentHash
      Acquisition: SourceAcquisition
      MediaType: string
      Language: string option
      ByteLength: int64
      SourceModifiedAt: DateTimeOffset option
      IngestedAt: DateTimeOffset
      Classification: DataClassification
      Trust: TrustAssessment
      Metadata: Map<string, string>
      Lifecycle: KnowledgeLifecycle }

module KnowledgeDocumentVersion =
    let create
        documentId
        sourceId
        version
        contentHash
        acquisition
        mediaType
        language
        byteLength
        sourceModifiedAt
        ingestedAt
        classification
        trust
        metadata
        lifecycle
        =
        Validation.positive (nameof version) version |> ignore
        Validation.nonNegativeInt64 (nameof byteLength) byteLength |> ignore

        if acquisition.SourceContentHash <> contentHash then
            invalidArg (nameof acquisition) "Acquisition hash must match the immutable document-version content hash."

        language |> Option.iter (Validation.canonical (nameof language) >> ignore)

        { SchemaVersion = KnowledgeSchemaVersion.current
          Id = KnowledgeDocumentVersionId.derive documentId version contentHash
          DocumentId = documentId
          SourceId = sourceId
          Version = version
          ContentHash = contentHash
          Acquisition = acquisition
          MediaType = Validation.canonical (nameof mediaType) mediaType
          Language = language
          ByteLength = byteLength
          SourceModifiedAt = sourceModifiedAt
          IngestedAt = ingestedAt
          Classification = classification
          Trust = trust
          Metadata = Validation.metadata metadata
          Lifecycle = lifecycle }

type SourceLocation =
    { ByteOffset: int64
      ByteLength: int64
      CharacterOffset: int
      CharacterLength: int
      Page: int option
      LineStart: int option
      LineEnd: int option
      Path: string option }

module SourceLocation =
    let create byteOffset byteLength characterOffset characterLength page lineStart lineEnd path =
        Validation.nonNegativeInt64 "byte offset" byteOffset |> ignore
        Validation.nonNegativeInt64 "byte length" byteLength |> ignore
        Validation.nonNegative "character offset" characterOffset |> ignore
        Validation.nonNegative "character length" characterLength |> ignore
        page |> Option.iter (Validation.positive (nameof page) >> ignore)
        lineStart |> Option.iter (Validation.positive (nameof lineStart) >> ignore)
        lineEnd |> Option.iter (Validation.positive (nameof lineEnd) >> ignore)

        match lineStart, lineEnd with
        | Some first, Some last when last < first ->
            invalidArg (nameof lineEnd) "Ending line must not precede the starting line."
        | _ -> ()

        path |> Option.iter (Validation.canonical (nameof path) >> ignore)

        { ByteOffset = byteOffset
          ByteLength = byteLength
          CharacterOffset = characterOffset
          CharacterLength = characterLength
          Page = page
          LineStart = lineStart
          LineEnd = lineEnd
          Path = path }

[<RequireQualifiedAccess>]
type ContentUnitKind =
    | Heading
    | Paragraph
    | Table
    | Code
    | List
    | Link
    | Other of string

[<RequireQualifiedAccess>]
type DerivationStage =
    | SourceAcquisition
    | Parsing
    | Chunking
    | Enrichment
    | Embedding
    | Indexing
    | Retrieval
    | Citation

type DerivationStep =
    { Stage: DerivationStage
      Component: ComponentVersion
      InputHashes: ContentHash list
      OutputHash: ContentHash
      CompletedAt: DateTimeOffset
      Metadata: Map<string, string> }

module DerivationStep =
    let create stage componentVersion inputHashes outputHash completedAt metadata =
        if List.isEmpty inputHashes then
            invalidArg (nameof inputHashes) "A derivation step requires at least one input hash."

        { Stage = stage
          Component = componentVersion
          InputHashes = inputHashes
          OutputHash = outputHash
          CompletedAt = completedAt
          Metadata = Validation.metadata metadata }

type ContentUnit =
    { SchemaVersion: KnowledgeSchemaVersion
      Id: ContentUnitId
      DocumentVersionId: KnowledgeDocumentVersionId
      Sequence: int
      Kind: ContentUnitKind
      Text: string
      ContentHash: ContentHash
      Location: SourceLocation
      Parser: ComponentVersion
      Derivation: DerivationStep list
      Metadata: Map<string, string>
      Lifecycle: KnowledgeLifecycle }

module ContentUnit =
    let create documentVersionId sequence kind text location parser derivation metadata lifecycle =
        Validation.nonNegative (nameof sequence) sequence |> ignore

        if isNull text then
            nullArg (nameof text)

        match kind with
        | ContentUnitKind.Other name -> Validation.canonical "content unit kind" name |> ignore
        | _ -> ()

        let contentHash = ContentHash.computeText text

        { SchemaVersion = KnowledgeSchemaVersion.current
          Id = ContentUnitId.derive documentVersionId sequence contentHash
          DocumentVersionId = documentVersionId
          Sequence = sequence
          Kind = kind
          Text = text
          ContentHash = contentHash
          Location = location
          Parser = parser
          Derivation = derivation
          Metadata = Validation.metadata metadata
          Lifecycle = lifecycle }

[<RequireQualifiedAccess>]
type KnowledgeChunkRole =
    | Parent
    | Leaf

[<RequireQualifiedAccess>]
type KnowledgeEnrichmentKind =
    | Entity of category: string
    | Topic
    | Summary

type KnowledgeEnrichmentEvidence =
    { ContentUnitId: ContentUnitId
      Location: SourceLocation
      Quote: string
      QuotedContentHash: ContentHash }

module KnowledgeEnrichmentEvidence =
    let create contentUnitId location quote =
        if String.IsNullOrWhiteSpace quote || quote <> quote.Trim() then
            invalidArg (nameof quote) "Enrichment evidence quote must be non-blank and trimmed."

        { ContentUnitId = contentUnitId
          Location = location
          Quote = quote
          QuotedContentHash = ContentHash.computeText quote }

[<RequireQualifiedAccess>]
type KnowledgeEnrichmentTrust = | ModelGeneratedUntrusted

type KnowledgeEnrichment =
    { SchemaVersion: KnowledgeSchemaVersion
      Id: KnowledgeEnrichmentId
      Kind: KnowledgeEnrichmentKind
      Value: string
      Confidence: Confidence
      Evidence: KnowledgeEnrichmentEvidence list
      Generator: ComponentVersion
      GeneratorConfigurationHash: ContentHash
      GeneratedAt: DateTimeOffset
      Trust: KnowledgeEnrichmentTrust }

module KnowledgeEnrichment =
    let private kindKey =
        function
        | KnowledgeEnrichmentKind.Entity category -> "entity:" + Validation.canonical "entity category" category
        | KnowledgeEnrichmentKind.Topic -> "topic"
        | KnowledgeEnrichmentKind.Summary -> "summary"

    let create chunkId kind value confidence evidence generator generatorConfigurationHash generatedAt =
        let value = Validation.canonical (nameof value) value

        if isNull (box evidence) || List.isEmpty evidence then
            invalidArg (nameof evidence) "A model enrichment requires source evidence."

        let evidenceHashes =
            evidence
            |> List.map (fun item ->
                ContentUnitId.value item.ContentUnitId
                + ":"
                + ContentHash.value item.QuotedContentHash)
            |> List.sort

        { SchemaVersion = KnowledgeSchemaVersion.current
          Id =
            KnowledgeEnrichmentId.derive
                chunkId
                generator
                generatorConfigurationHash
                (kindKey kind)
                value
                (Confidence.value confidence)
                evidenceHashes
          Kind = kind
          Value = value
          Confidence = confidence
          Evidence = evidence
          Generator = generator
          GeneratorConfigurationHash = generatorConfigurationHash
          GeneratedAt = generatedAt
          Trust = KnowledgeEnrichmentTrust.ModelGeneratedUntrusted }

type KnowledgeChunk =
    { SchemaVersion: KnowledgeSchemaVersion
      Id: KnowledgeChunkId
      DocumentVersionId: KnowledgeDocumentVersionId
      Sequence: int
      Role: KnowledgeChunkRole
      ParentChunkId: KnowledgeChunkId option
      ContentUnitIds: ContentUnitId list
      Text: string
      ContentHash: ContentHash
      Locations: SourceLocation list
      Chunker: ComponentVersion
      ChunkerConfigurationHash: ContentHash
      Enrichments: KnowledgeEnrichment list
      Derivation: DerivationStep list
      Metadata: Map<string, string>
      Lifecycle: KnowledgeLifecycle }

module KnowledgeChunk =
    let create
        documentVersionId
        sequence
        role
        parentChunkId
        contentUnitIds
        text
        locations
        chunker
        chunkerConfigurationHash
        derivation
        metadata
        lifecycle
        =
        Validation.nonNegative (nameof sequence) sequence |> ignore

        if List.isEmpty contentUnitIds then
            invalidArg (nameof contentUnitIds) "A chunk requires at least one content unit."

        if (contentUnitIds |> Set.ofList |> Set.count) <> contentUnitIds.Length then
            invalidArg (nameof contentUnitIds) "A chunk cannot refer to the same content unit more than once."

        if List.isEmpty locations then
            invalidArg (nameof locations) "A chunk requires at least one source location."

        if isNull text then
            nullArg (nameof text)

        let contentHash = ContentHash.computeText text

        { SchemaVersion = KnowledgeSchemaVersion.current
          Id =
            KnowledgeChunkId.derive
                documentVersionId
                (chunker.ComponentId
                 + "@"
                 + chunker.Version
                 + "#"
                 + ContentHash.value chunkerConfigurationHash)
                sequence
                contentHash
          DocumentVersionId = documentVersionId
          Sequence = sequence
          Role = role
          ParentChunkId = parentChunkId
          ContentUnitIds = contentUnitIds
          Text = text
          ContentHash = contentHash
          Locations = locations
          Chunker = chunker
          ChunkerConfigurationHash = chunkerConfigurationHash
          Enrichments = []
          Derivation = derivation
          Metadata = Validation.metadata metadata
          Lifecycle = lifecycle }

[<RequireQualifiedAccess>]
type EmbeddingNormalization =
    | None
    | UnitLength

[<RequireQualifiedAccess>]
type VectorDistanceMetric =
    | Cosine
    | DotProduct
    | Euclidean

type EmbeddingDescriptor =
    { Model: ComponentVersion
      Dimensions: int
      Normalization: EmbeddingNormalization
      DistanceMetric: VectorDistanceMetric
      Preprocessing: ComponentVersion }

module EmbeddingDescriptor =
    let create model dimensions normalization distanceMetric preprocessing =
        { Model = model
          Dimensions = Validation.positive (nameof dimensions) dimensions
          Normalization = normalization
          DistanceMetric = distanceMetric
          Preprocessing = preprocessing }

type KnowledgeIndexEntry =
    { SchemaVersion: KnowledgeSchemaVersion
      Id: KnowledgeIndexEntryId
      IndexName: string
      ChunkId: KnowledgeChunkId
      DocumentVersionId: KnowledgeDocumentVersionId
      ContentHash: ContentHash
      Embedding: EmbeddingDescriptor option
      IndexedAt: DateTimeOffset
      Derivation: DerivationStep list
      Lifecycle: KnowledgeLifecycle }

module KnowledgeIndexEntry =
    let create indexName (chunk: KnowledgeChunk) embedding indexedAt derivation =
        { SchemaVersion = KnowledgeSchemaVersion.current
          Id = KnowledgeIndexEntryId.derive indexName chunk.Id
          IndexName = Validation.canonical (nameof indexName) indexName
          ChunkId = chunk.Id
          DocumentVersionId = chunk.DocumentVersionId
          ContentHash = chunk.ContentHash
          Embedding = embedding
          IndexedAt = indexedAt
          Derivation = derivation
          Lifecycle = chunk.Lifecycle }

type Citation =
    { SchemaVersion: KnowledgeSchemaVersion
      Id: CitationId
      RetrievalResultId: RetrievalResultId
      SourceId: KnowledgeSourceId
      DocumentId: KnowledgeDocumentId
      DocumentVersionId: KnowledgeDocumentVersionId
      ChunkId: KnowledgeChunkId
      Location: SourceLocation
      QuotedContentHash: ContentHash
      CreatedAt: DateTimeOffset }

module Citation =
    let create
        retrievalResultId
        (source: KnowledgeSource)
        (document: KnowledgeDocument)
        (version: KnowledgeDocumentVersion)
        (chunk: KnowledgeChunk)
        location
        quotedText
        createdAt
        =
        if version.SourceId <> source.Id then
            invalidArg (nameof version) "Document version does not belong to the cited source."

        if version.DocumentId <> document.Id || document.SourceId <> source.Id then
            invalidArg (nameof document) "Document does not belong to the cited source and version."

        if chunk.DocumentVersionId <> version.Id then
            invalidArg (nameof chunk) "Chunk does not belong to the cited document version."

        if not (chunk.Locations |> List.contains location) then
            invalidArg (nameof location) "Citation location must be one of the chunk's source locations."

        if
            String.IsNullOrEmpty quotedText
            || not (chunk.Text.Contains(quotedText, StringComparison.Ordinal))
        then
            invalidArg (nameof quotedText) "Cited text must be a non-empty exact segment of the chunk."

        { SchemaVersion = KnowledgeSchemaVersion.current
          Id = CitationId.generate ()
          RetrievalResultId = retrievalResultId
          SourceId = source.Id
          DocumentId = document.Id
          DocumentVersionId = version.Id
          ChunkId = chunk.Id
          Location = location
          QuotedContentHash = ContentHash.computeText quotedText
          CreatedAt = createdAt }

type RetrievedPassage =
    { Chunk: KnowledgeChunk
      Citation: Citation
      Score: float
      Confidence: Confidence option
      Strategy: string
      Evidence: Map<string, string> }

module RetrievedPassage =
    let create (chunk: KnowledgeChunk) (citation: Citation) score confidence strategy evidence =
        if not (Double.IsFinite score) then
            invalidArg (nameof score) "Retrieval score must be finite."

        if
            citation.ChunkId <> chunk.Id
            || citation.DocumentVersionId <> chunk.DocumentVersionId
        then
            invalidArg (nameof citation) "Citation must refer to the retrieved chunk."

        { Chunk = chunk
          Citation = citation
          Score = score
          Confidence = confidence
          Strategy = Validation.canonical (nameof strategy) strategy
          Evidence = Validation.metadata evidence }

type RetrievalResult =
    { SchemaVersion: KnowledgeSchemaVersion
      Id: RetrievalResultId
      QueryHash: ContentHash
      RetrievedAt: DateTimeOffset
      Passages: RetrievedPassage list
      Derivation: DerivationStep list }

module RetrievalResult =
    let create id query retrievedAt passages derivation =
        passages
        |> List.iter (fun passage ->
            if passage.Citation.RetrievalResultId <> id then
                invalidArg (nameof passages) "Every citation must belong to this retrieval result.")

        { SchemaVersion = KnowledgeSchemaVersion.current
          Id = id
          QueryHash = ContentHash.computeText query
          RetrievedAt = retrievedAt
          Passages = passages
          Derivation = derivation }

type KnowledgeCacheEntry =
    { SchemaVersion: KnowledgeSchemaVersion
      Id: RetrievalResultId
      QueryHash: ContentHash
      SourceId: KnowledgeSourceId
      DocumentVersionId: KnowledgeDocumentVersionId
      ChunkIds: KnowledgeChunkId list
      CachedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset
      Lifecycle: KnowledgeLifecycle }

module KnowledgeCacheEntry =
    let create id query sourceId documentVersionId chunkIds cachedAt expiresAt lifecycle =
        if List.isEmpty chunkIds then
            invalidArg (nameof chunkIds) "A knowledge cache entry requires at least one chunk."

        if expiresAt <= cachedAt then
            invalidArg (nameof expiresAt) "Knowledge cache expiry must follow its creation time."

        { SchemaVersion = KnowledgeSchemaVersion.current
          Id = id
          QueryHash = ContentHash.computeText query
          SourceId = sourceId
          DocumentVersionId = documentVersionId
          ChunkIds = chunkIds
          CachedAt = cachedAt
          ExpiresAt = expiresAt
          Lifecycle = lifecycle }
