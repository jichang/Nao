namespace Nao.Knowledge

open System
open System.Globalization
open System.Text

[<RequireQualifiedAccess>]
module ShingleNearDuplicateDetector =
    type private Fingerprint =
        { Chunk: KnowledgeChunk
          Normalized: string
          Shingles: Set<ContentHash> }

    let private detectorComponent =
        ComponentVersion.create "nao.duplicate.shingle-jaccard" "1"

    let private invariantFloat (value: float) =
        value.ToString("R", CultureInfo.InvariantCulture)

    let private configurationHash (config: ShingleNearDuplicateDetectorConfig) =
        String.concat
            ";"
            [ $"shingle-tokens={config.ShingleTokens.ToString(CultureInfo.InvariantCulture)}"
              $"similarity-threshold={invariantFloat (Confidence.value config.SimilarityThreshold)}"
              $"maximum-chunks={config.MaximumChunks.ToString(CultureInfo.InvariantCulture)}"
              $"maximum-candidate-pairs={config.MaximumCandidatePairs.ToString(CultureInfo.InvariantCulture)}"
              $"maximum-detected-pairs={config.MaximumDetectedPairs.ToString(CultureInfo.InvariantCulture)}"
              $"tokenizer={config.Tokenizer.Descriptor.Component.ComponentId}@{config.Tokenizer.Descriptor.Component.Version}"
              $"tokenizer-configuration={ContentHash.value config.Tokenizer.Descriptor.ConfigurationHash}" ]
        |> ContentHash.computeText

    let private normalizedTokens (tokenizer: KnowledgeTokenizer) (text: string) =
        let spans = tokenizer.Tokenize text

        if isNull (box spans) then
            invalidOp "Near-duplicate tokenizer returned a null token collection."

        spans
        |> List.pairwise
        |> List.iter (fun (left, right) ->
            if right.CharacterOffset < left.CharacterOffset + left.CharacterLength then
                invalidOp "Near-duplicate tokenizer returned overlapping or unordered spans.")

        spans
        |> List.choose (fun span ->
            if
                span.CharacterOffset < 0
                || span.CharacterLength <= 0
                || span.CharacterOffset > text.Length
                || span.CharacterLength > text.Length - span.CharacterOffset
            then
                invalidOp "Near-duplicate tokenizer returned a span outside the supplied text."

            let token = text.Substring(span.CharacterOffset, span.CharacterLength)

            if token |> Seq.exists Char.IsLetterOrDigit then
                token.Normalize(NormalizationForm.FormKC).ToLowerInvariant() |> Some
            else
                None)

    let private shingles size tokens =
        match tokens with
        | [] -> Set.empty
        | values when values.Length < size ->
            values |> String.concat "\u001f" |> ContentHash.computeText |> Set.singleton
        | values ->
            values
            |> List.windowed size
            |> List.map (String.concat "\u001f" >> ContentHash.computeText)
            |> Set.ofList

    let private fingerprint (config: ShingleNearDuplicateDetectorConfig) (chunk: KnowledgeChunk) =
        let tokens = normalizedTokens config.Tokenizer chunk.Text

        { Chunk = chunk
          Normalized = String.concat " " tokens
          Shingles = shingles config.ShingleTokens tokens }

    let private candidatePairs maximumPairs fingerprints =
        let buckets =
            fingerprints
            |> List.mapi (fun index fingerprint -> fingerprint.Shingles |> Seq.map (fun shingle -> shingle, index))
            |> Seq.concat
            |> Seq.groupBy fst

        let pairs = Collections.Generic.HashSet<struct (int * int)>()

        for _, entries in buckets do
            let indices = entries |> Seq.map snd |> Seq.distinct |> Seq.sort |> Seq.toArray

            for leftIndex in 0 .. indices.Length - 2 do
                for rightIndex in leftIndex + 1 .. indices.Length - 1 do
                    pairs.Add(struct (indices[leftIndex], indices[rightIndex])) |> ignore

                    if pairs.Count > maximumPairs then
                        invalidOp "Near-duplicate candidate count exceeds the configured limit."

        pairs |> Seq.sort |> List.ofSeq

    let private similarity left right =
        let intersection = Set.intersect left right |> Set.count
        let union = Set.union left right |> Set.count

        if union = 0 then 0.0 else float intersection / float union

    let private canonicalPair left right =
        if
            StringComparer.Ordinal.Compare(KnowledgeChunkId.value left.Chunk.Id, KnowledgeChunkId.value right.Chunk.Id)
            <= 0
        then
            left, right
        else
            right, left

    let create (config: ShingleNearDuplicateDetectorConfig) =
        if isNull (box config.Tokenizer) then
            nullArg (nameof config.Tokenizer)

        if config.ShingleTokens <= 0 then
            invalidArg (nameof config.ShingleTokens) "Shingle token count must be positive."

        if
            Confidence.value config.SimilarityThreshold <= 0.0
            || config.MaximumChunks <= 0
            || config.MaximumCandidatePairs <= 0
            || config.MaximumDetectedPairs <= 0
        then
            invalidArg (nameof config) "Duplicate thresholds and limits must be positive."

        let descriptor =
            { Component = detectorComponent
              ConfigurationHash = configurationHash config
              Deterministic = config.Tokenizer.Descriptor.Deterministic }

        { Descriptor = descriptor
          DetectAsync =
            fun request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    if isNull (box request.Authorization) then
                        nullArg (nameof request.Authorization)

                    if isNull (box request.Chunks) then
                        nullArg (nameof request.Chunks)

                    if request.Chunks.Length > config.MaximumChunks then
                        invalidArg (nameof request) "Chunk count exceeds the configured duplicate-analysis limit."

                    let chunkIds =
                        request.Chunks
                        |> List.map (fun (chunk: KnowledgeChunk) -> chunk.Id)
                        |> Set.ofList

                    if chunkIds.Count <> request.Chunks.Length then
                        invalidArg (nameof request) "Duplicate analysis requires unique chunks."

                    let fingerprints =
                        request.Chunks
                        |> List.map (fun chunk ->
                            cancellationToken.ThrowIfCancellationRequested()
                            fingerprint config chunk)

                    let candidates = candidatePairs config.MaximumCandidatePairs fingerprints

                    let duplicates =
                        candidates
                        |> List.choose (fun struct (leftIndex, rightIndex) ->
                            cancellationToken.ThrowIfCancellationRequested()
                            let left = fingerprints[leftIndex]
                            let right = fingerprints[rightIndex]
                            let score = similarity left.Shingles right.Shingles

                            if score < Confidence.value config.SimilarityThreshold then
                                None
                            else
                                let canonical, duplicate = canonicalPair left right

                                let kind =
                                    if canonical.Normalized = duplicate.Normalized then
                                        ContentDuplicateKind.Exact
                                    else
                                        ContentDuplicateKind.Near

                                Some
                                    { SchemaVersion = KnowledgeSchemaVersion.current
                                      Id =
                                        NearDuplicateId.derive
                                            descriptor.Component
                                            descriptor.ConfigurationHash
                                            canonical.Chunk.Id
                                            duplicate.Chunk.Id
                                      CanonicalChunkId = canonical.Chunk.Id
                                      DuplicateChunkId = duplicate.Chunk.Id
                                      Kind = kind
                                      Similarity = Confidence.create score
                                      Detector = descriptor.Component
                                      DetectorConfigurationHash = descriptor.ConfigurationHash
                                      DetectedAt = request.DetectedAt })
                        |> List.sortBy (fun duplicate ->
                            KnowledgeChunkId.value duplicate.CanonicalChunkId,
                            KnowledgeChunkId.value duplicate.DuplicateChunkId)

                    if duplicates.Length > config.MaximumDetectedPairs then
                        invalidOp "Near-duplicate result count exceeds the configured limit."

                    return
                        { Duplicates = duplicates
                          AnalyzedChunks = fingerprints.Length
                          CandidatePairs = candidates.Length }
                } }
