namespace Nao.Eval

open System
open System.Globalization
open System.Text
open System.Text.Json
open Nao.Knowledge

type KnowledgeExpectedFact =
    private
        { Id: string
          Text: string
          SupportingChunkIds: Set<KnowledgeChunkId> }

[<RequireQualifiedAccess>]
module KnowledgeExpectedFact =
    let create id text supportingChunkIds =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "Expected fact identity must be non-blank and trimmed."

        if String.IsNullOrWhiteSpace text || text <> text.Trim() then
            invalidArg (nameof text) "Expected fact text must be non-blank and trimmed."

        let supportingChunkIds = Set.ofSeq supportingChunkIds

        if supportingChunkIds.IsEmpty then
            invalidArg (nameof supportingChunkIds) "Expected facts require at least one supporting chunk."

        { Id = id
          Text = text
          SupportingChunkIds = supportingChunkIds }

    let id value = value.Id
    let text value = value.Text
    let supportingChunkIds value = value.SupportingChunkIds

[<RequireQualifiedAccess>]
type KnowledgeProhibitedResultTarget =
    | Document of KnowledgeDocumentId
    | Chunk of KnowledgeChunkId

[<RequireQualifiedAccess>]
module KnowledgeProhibitedResultTarget =
    let key =
        function
        | KnowledgeProhibitedResultTarget.Document documentId -> "document:" + KnowledgeDocumentId.value documentId
        | KnowledgeProhibitedResultTarget.Chunk chunkId -> "chunk:" + KnowledgeChunkId.value chunkId

type KnowledgeProhibitedResult =
    private
        { Target: KnowledgeProhibitedResultTarget
          Reason: string }

[<RequireQualifiedAccess>]
module KnowledgeProhibitedResult =
    let create target reason =
        if String.IsNullOrWhiteSpace reason || reason <> reason.Trim() then
            invalidArg (nameof reason) "Prohibited-result reasons must be non-blank and trimmed."

        { Target = target; Reason = reason }

    let target value = value.Target
    let reason value = value.Reason

[<RequireQualifiedAccess>]
type KnowledgeQueryType =
    | FactLookup
    | Procedural
    | Comparison
    | Exploratory
    | Troubleshooting

[<RequireQualifiedAccess>]
module KnowledgeQueryType =
    let key =
        function
        | KnowledgeQueryType.FactLookup -> "fact-lookup"
        | KnowledgeQueryType.Procedural -> "procedural"
        | KnowledgeQueryType.Comparison -> "comparison"
        | KnowledgeQueryType.Exploratory -> "exploratory"
        | KnowledgeQueryType.Troubleshooting -> "troubleshooting"

    let parse =
        function
        | "fact-lookup" -> KnowledgeQueryType.FactLookup
        | "procedural" -> KnowledgeQueryType.Procedural
        | "comparison" -> KnowledgeQueryType.Comparison
        | "exploratory" -> KnowledgeQueryType.Exploratory
        | "troubleshooting" -> KnowledgeQueryType.Troubleshooting
        | value -> invalidArg (nameof value) "Unknown knowledge query type."

type KnowledgeEvaluationDimensions =
    private
        { SourceIds: Set<KnowledgeSourceId>
          Formats: Set<string>
          LanguageTag: string
          TenantPolicy: string
          QueryType: KnowledgeQueryType }

[<RequireQualifiedAccess>]
module KnowledgeEvaluationDimensions =
    let create sourceIds formats languageTag tenantPolicy queryType =
        let sourceIds = Set.ofSeq sourceIds

        if sourceIds.IsEmpty then
            invalidArg (nameof sourceIds) "Knowledge evaluation dimensions require at least one source."

        let formats =
            formats
            |> Seq.map (fun value ->
                if String.IsNullOrWhiteSpace value then
                    invalidArg (nameof formats) "Knowledge evaluation formats must be non-blank."

                value.Trim().ToLowerInvariant())
            |> Set.ofSeq

        if formats.IsEmpty then
            invalidArg (nameof formats) "Knowledge evaluation dimensions require at least one format."

        if String.IsNullOrWhiteSpace languageTag || languageTag <> languageTag.Trim() then
            invalidArg (nameof languageTag) "Knowledge evaluation language must be non-blank and trimmed."

        let languageTag =
            try
                CultureInfo.GetCultureInfo(languageTag).Name
            with :? CultureNotFoundException ->
                invalidArg (nameof languageTag) "Knowledge evaluation language must be a valid culture tag."

        if String.IsNullOrWhiteSpace tenantPolicy || tenantPolicy <> tenantPolicy.Trim() then
            invalidArg (nameof tenantPolicy) "Knowledge evaluation tenant policy must be non-blank and trimmed."

        { SourceIds = sourceIds
          Formats = formats
          LanguageTag = languageTag
          TenantPolicy = tenantPolicy
          QueryType = queryType }

    let sourceIds value = value.SourceIds
    let formats value = value.Formats
    let languageTag value = value.LanguageTag
    let tenantPolicy value = value.TenantPolicy
    let queryType value = value.QueryType

[<RequireQualifiedAccess>]
type KnowledgeEvaluationSegmentDimension =
    | Source
    | Format
    | Language
    | TenantPolicy
    | QueryType

type KnowledgeEvaluationSegment =
    private
        { Dimension: KnowledgeEvaluationSegmentDimension
          Value: string }

[<RequireQualifiedAccess>]
module KnowledgeEvaluationSegment =
    let private create dimension value =
        { Dimension = dimension; Value = value }

    let dimension value = value.Dimension
    let value value = value.Value

    let ofDimensions dimensions =
        [ yield!
              dimensions
              |> KnowledgeEvaluationDimensions.sourceIds
              |> Seq.map (KnowledgeSourceId.value >> create KnowledgeEvaluationSegmentDimension.Source)
          yield!
              dimensions
              |> KnowledgeEvaluationDimensions.formats
              |> Seq.map (create KnowledgeEvaluationSegmentDimension.Format)
          yield
              dimensions
              |> KnowledgeEvaluationDimensions.languageTag
              |> create KnowledgeEvaluationSegmentDimension.Language
          yield
              dimensions
              |> KnowledgeEvaluationDimensions.tenantPolicy
              |> create KnowledgeEvaluationSegmentDimension.TenantPolicy
          yield
              dimensions
              |> KnowledgeEvaluationDimensions.queryType
              |> KnowledgeQueryType.key
              |> create KnowledgeEvaluationSegmentDimension.QueryType ]

type KnowledgeEvaluationCase =
    private
        { Id: string
          Query: string
          Dimensions: KnowledgeEvaluationDimensions
          RelevantDocumentIds: Set<KnowledgeDocumentId>
          RelevantChunkIds: Set<KnowledgeChunkId>
          ExpectedFacts: KnowledgeExpectedFact list
          ProhibitedResults: KnowledgeProhibitedResult list }

[<RequireQualifiedAccess>]
module KnowledgeEvaluationCase =
    let create id query dimensions relevantDocumentIds relevantChunkIds expectedFacts prohibitedResults =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "Knowledge evaluation case identity must be non-blank and trimmed."

        if String.IsNullOrWhiteSpace query || query <> query.Trim() then
            invalidArg (nameof query) "Knowledge evaluation query must be non-blank and trimmed."

        if isNull (box dimensions) then
            nullArg (nameof dimensions)

        if isNull (box expectedFacts) then
            nullArg (nameof expectedFacts)

        if isNull (box prohibitedResults) then
            nullArg (nameof prohibitedResults)

        let relevantDocumentIds = Set.ofSeq relevantDocumentIds
        let relevantChunkIds = Set.ofSeq relevantChunkIds

        if relevantDocumentIds.IsEmpty && relevantChunkIds.IsEmpty then
            invalidArg
                (nameof relevantChunkIds)
                "Knowledge evaluation cases require at least one relevant document or chunk."

        let duplicateFactIds =
            expectedFacts
            |> List.countBy KnowledgeExpectedFact.id
            |> List.exists (fun (_, count) -> count > 1)

        if duplicateFactIds then
            invalidArg (nameof expectedFacts) "Expected fact identities must be unique within a case."

        expectedFacts
        |> List.iter (fun fact ->
            let unknownSupportingChunks =
                Set.difference (KnowledgeExpectedFact.supportingChunkIds fact) relevantChunkIds

            if not unknownSupportingChunks.IsEmpty then
                invalidArg
                    (nameof expectedFacts)
                    "Every expected fact supporting chunk must be relevant to the same case.")

        let prohibitedTargets =
            prohibitedResults
            |> List.map (KnowledgeProhibitedResult.target >> KnowledgeProhibitedResultTarget.key)

        if prohibitedTargets |> List.distinct |> List.length <> prohibitedTargets.Length then
            invalidArg (nameof prohibitedResults) "Prohibited result targets must be unique within a case."

        prohibitedResults
        |> List.iter (fun prohibited ->
            match KnowledgeProhibitedResult.target prohibited with
            | KnowledgeProhibitedResultTarget.Document documentId when relevantDocumentIds.Contains documentId ->
                invalidArg (nameof prohibitedResults) "A relevant document cannot also be prohibited."
            | KnowledgeProhibitedResultTarget.Chunk chunkId when relevantChunkIds.Contains chunkId ->
                invalidArg (nameof prohibitedResults) "A relevant chunk cannot also be prohibited."
            | _ -> ())

        { Id = id
          Query = query
          Dimensions = dimensions
          RelevantDocumentIds = relevantDocumentIds
          RelevantChunkIds = relevantChunkIds
          ExpectedFacts = expectedFacts
          ProhibitedResults = prohibitedResults }

    let id value = value.Id
    let query value = value.Query
    let dimensions value = value.Dimensions
    let relevantDocumentIds value = value.RelevantDocumentIds
    let relevantChunkIds value = value.RelevantChunkIds
    let expectedFacts value = value.ExpectedFacts
    let prohibitedResults value = value.ProhibitedResults

type KnowledgeEvaluationDataset =
    private
        { SchemaVersion: int
          Name: string
          Revision: int
          ContentHash: ContentHash
          Cases: KnowledgeEvaluationCase list }

[<RequireQualifiedAccess>]
module KnowledgeEvaluationDataset =
    let currentSchemaVersion = 1

    let private appendValue (builder: StringBuilder) (value: string) =
        builder.Append(value.Length).Append(':').Append(value).Append('|') |> ignore

    let private computeContentHash name revision cases =
        let canonical = StringBuilder()
        appendValue canonical (string currentSchemaVersion)
        appendValue canonical name
        appendValue canonical (string revision)

        for case in cases do
            appendValue canonical (KnowledgeEvaluationCase.id case)
            appendValue canonical (KnowledgeEvaluationCase.query case)

            let dimensions = KnowledgeEvaluationCase.dimensions case

            dimensions
            |> KnowledgeEvaluationDimensions.sourceIds
            |> Seq.map KnowledgeSourceId.value
            |> Seq.sort
            |> Seq.iter (appendValue canonical)

            dimensions
            |> KnowledgeEvaluationDimensions.formats
            |> Seq.sort
            |> Seq.iter (appendValue canonical)

            appendValue canonical (KnowledgeEvaluationDimensions.languageTag dimensions)
            appendValue canonical (KnowledgeEvaluationDimensions.tenantPolicy dimensions)

            dimensions
            |> KnowledgeEvaluationDimensions.queryType
            |> KnowledgeQueryType.key
            |> appendValue canonical

            case
            |> KnowledgeEvaluationCase.relevantDocumentIds
            |> Seq.map KnowledgeDocumentId.value
            |> Seq.sort
            |> Seq.iter (appendValue canonical)

            case
            |> KnowledgeEvaluationCase.relevantChunkIds
            |> Seq.map KnowledgeChunkId.value
            |> Seq.sort
            |> Seq.iter (appendValue canonical)

            case
            |> KnowledgeEvaluationCase.expectedFacts
            |> List.sortBy KnowledgeExpectedFact.id
            |> List.iter (fun fact ->
                appendValue canonical (KnowledgeExpectedFact.id fact)
                appendValue canonical (KnowledgeExpectedFact.text fact)

                fact
                |> KnowledgeExpectedFact.supportingChunkIds
                |> Seq.map KnowledgeChunkId.value
                |> Seq.sort
                |> Seq.iter (appendValue canonical))

            case
            |> KnowledgeEvaluationCase.prohibitedResults
            |> List.sortBy (KnowledgeProhibitedResult.target >> KnowledgeProhibitedResultTarget.key)
            |> List.iter (fun prohibited ->
                appendValue
                    canonical
                    (prohibited
                     |> KnowledgeProhibitedResult.target
                     |> KnowledgeProhibitedResultTarget.key)

                appendValue canonical (KnowledgeProhibitedResult.reason prohibited))

        canonical.ToString() |> ContentHash.computeText

    let private build schemaVersion name revision cases =
        if schemaVersion <> currentSchemaVersion then
            invalidArg (nameof schemaVersion) "Unsupported knowledge evaluation dataset schema version."

        if String.IsNullOrWhiteSpace name || name <> name.Trim() then
            invalidArg (nameof name) "Knowledge evaluation dataset name must be non-blank and trimmed."

        if revision <= 0 then
            invalidArg (nameof revision) "Knowledge evaluation dataset revision must be positive."

        if isNull (box cases) || List.isEmpty cases then
            invalidArg (nameof cases) "Knowledge evaluation datasets require at least one case."

        let duplicateIds =
            cases
            |> List.countBy KnowledgeEvaluationCase.id
            |> List.exists (fun (_, count) -> count > 1)

        if duplicateIds then
            invalidArg (nameof cases) "Knowledge evaluation case identities must be unique."

        { SchemaVersion = schemaVersion
          Name = name
          Revision = revision
          ContentHash = computeContentHash name revision cases
          Cases = cases }

    let create name revision cases =
        build currentSchemaVersion name revision cases

    let restore schemaVersion name revision expectedContentHash cases =
        let dataset = build schemaVersion name revision cases

        if dataset.ContentHash <> expectedContentHash then
            invalidArg (nameof expectedContentHash) "Knowledge evaluation dataset content hash does not match."

        dataset

    let schemaVersion value = value.SchemaVersion
    let name value = value.Name
    let revision value = value.Revision
    let contentHash value = value.ContentHash
    let cases value = value.Cases

[<CLIMutable>]
type KnowledgeExpectedFactDto =
    { Id: string
      Text: string
      SupportingChunkIds: string array }

[<CLIMutable>]
type KnowledgeProhibitedResultDto =
    { Kind: string
      Id: string
      Reason: string }

[<CLIMutable>]
type KnowledgeEvaluationCaseDto =
    { Id: string
      Query: string
      SourceIds: string array
      Formats: string array
      LanguageTag: string
      TenantPolicy: string
      QueryType: string
      RelevantDocumentIds: string array
      RelevantChunkIds: string array
      ExpectedFacts: KnowledgeExpectedFactDto array
      ProhibitedResults: KnowledgeProhibitedResultDto array }

[<CLIMutable>]
type KnowledgeEvaluationDatasetDto =
    { SchemaVersion: int
      Name: string
      Revision: int
      ContentHash: string
      Cases: KnowledgeEvaluationCaseDto array }

[<RequireQualifiedAccess>]
module KnowledgeEvaluationDatasetJson =
    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)

    let private requiredArray fieldName values =
        if isNull values then
            invalidArg fieldName "Knowledge evaluation dataset arrays cannot be null."

        values

    let private factDto fact =
        { Id = KnowledgeExpectedFact.id fact
          Text = KnowledgeExpectedFact.text fact
          SupportingChunkIds =
            fact
            |> KnowledgeExpectedFact.supportingChunkIds
            |> Seq.map KnowledgeChunkId.value
            |> Seq.sort
            |> Array.ofSeq }

    let private prohibitedDto prohibited =
        let kind, id =
            match KnowledgeProhibitedResult.target prohibited with
            | KnowledgeProhibitedResultTarget.Document documentId -> "document", KnowledgeDocumentId.value documentId
            | KnowledgeProhibitedResultTarget.Chunk chunkId -> "chunk", KnowledgeChunkId.value chunkId

        { Kind = kind
          Id = id
          Reason = KnowledgeProhibitedResult.reason prohibited }

    let serialize dataset =
        if isNull (box dataset) then
            nullArg (nameof dataset)

        let dto =
            { SchemaVersion = KnowledgeEvaluationDataset.schemaVersion dataset
              Name = KnowledgeEvaluationDataset.name dataset
              Revision = KnowledgeEvaluationDataset.revision dataset
              ContentHash = dataset |> KnowledgeEvaluationDataset.contentHash |> ContentHash.value
              Cases =
                dataset
                |> KnowledgeEvaluationDataset.cases
                |> List.map (fun case ->
                    { Id = KnowledgeEvaluationCase.id case
                      Query = KnowledgeEvaluationCase.query case
                      SourceIds =
                        case
                        |> KnowledgeEvaluationCase.dimensions
                        |> KnowledgeEvaluationDimensions.sourceIds
                        |> Seq.map KnowledgeSourceId.value
                        |> Seq.sort
                        |> Array.ofSeq
                      Formats =
                        case
                        |> KnowledgeEvaluationCase.dimensions
                        |> KnowledgeEvaluationDimensions.formats
                        |> Seq.sort
                        |> Array.ofSeq
                      LanguageTag =
                        case
                        |> KnowledgeEvaluationCase.dimensions
                        |> KnowledgeEvaluationDimensions.languageTag
                      TenantPolicy =
                        case
                        |> KnowledgeEvaluationCase.dimensions
                        |> KnowledgeEvaluationDimensions.tenantPolicy
                      QueryType =
                        case
                        |> KnowledgeEvaluationCase.dimensions
                        |> KnowledgeEvaluationDimensions.queryType
                        |> KnowledgeQueryType.key
                      RelevantDocumentIds =
                        case
                        |> KnowledgeEvaluationCase.relevantDocumentIds
                        |> Seq.map KnowledgeDocumentId.value
                        |> Seq.sort
                        |> Array.ofSeq
                      RelevantChunkIds =
                        case
                        |> KnowledgeEvaluationCase.relevantChunkIds
                        |> Seq.map KnowledgeChunkId.value
                        |> Seq.sort
                        |> Array.ofSeq
                      ExpectedFacts =
                        case
                        |> KnowledgeEvaluationCase.expectedFacts
                        |> List.sortBy KnowledgeExpectedFact.id
                        |> List.map factDto
                        |> List.toArray
                      ProhibitedResults =
                        case
                        |> KnowledgeEvaluationCase.prohibitedResults
                        |> List.sortBy (KnowledgeProhibitedResult.target >> KnowledgeProhibitedResultTarget.key)
                        |> List.map prohibitedDto
                        |> List.toArray })
                |> List.toArray }

        JsonSerializer.Serialize(dto, options)

    let deserialize json =
        if String.IsNullOrWhiteSpace json then
            invalidArg (nameof json) "Knowledge evaluation dataset JSON must be non-blank."

        let dto = JsonSerializer.Deserialize<KnowledgeEvaluationDatasetDto>(json, options)

        if isNull (box dto) then
            invalidArg (nameof json) "Knowledge evaluation dataset JSON must contain an object."

        let cases =
            requiredArray (nameof dto.Cases) dto.Cases
            |> Array.map (fun case ->
                let expectedFacts =
                    requiredArray (nameof case.ExpectedFacts) case.ExpectedFacts
                    |> Array.map (fun fact ->
                        requiredArray (nameof fact.SupportingChunkIds) fact.SupportingChunkIds
                        |> Array.map KnowledgeChunkId.parse
                        |> KnowledgeExpectedFact.create fact.Id fact.Text)
                    |> List.ofArray

                let prohibitedResults =
                    requiredArray (nameof case.ProhibitedResults) case.ProhibitedResults
                    |> Array.map (fun prohibited ->
                        let target =
                            match prohibited.Kind with
                            | "document" ->
                                KnowledgeProhibitedResultTarget.Document(KnowledgeDocumentId.parse prohibited.Id)
                            | "chunk" -> KnowledgeProhibitedResultTarget.Chunk(KnowledgeChunkId.parse prohibited.Id)
                            | _ ->
                                invalidArg
                                    (nameof prohibited.Kind)
                                    "Prohibited result kind must be 'document' or 'chunk'."

                        KnowledgeProhibitedResult.create target prohibited.Reason)
                    |> List.ofArray

                KnowledgeEvaluationCase.create
                    case.Id
                    case.Query
                    (KnowledgeEvaluationDimensions.create
                        (requiredArray (nameof case.SourceIds) case.SourceIds
                         |> Array.map KnowledgeSourceId.parse)
                        (requiredArray (nameof case.Formats) case.Formats)
                        case.LanguageTag
                        case.TenantPolicy
                        (KnowledgeQueryType.parse case.QueryType))
                    (requiredArray (nameof case.RelevantDocumentIds) case.RelevantDocumentIds
                     |> Array.map KnowledgeDocumentId.parse)
                    (requiredArray (nameof case.RelevantChunkIds) case.RelevantChunkIds
                     |> Array.map KnowledgeChunkId.parse)
                    expectedFacts
                    prohibitedResults)
            |> List.ofArray

        KnowledgeEvaluationDataset.restore
            dto.SchemaVersion
            dto.Name
            dto.Revision
            (ContentHash.parse dto.ContentHash)
            cases
