namespace Nao.Knowledge.Postgres

open System
open System.Text.RegularExpressions
open Nao.Knowledge

module internal PostgresIdentifier =
    let private pattern = Regex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)

    let validate name value =
        if isNull value || not (pattern.IsMatch value) then
            invalidArg name "PostgreSQL identifier must be lowercase, unquoted, and at most 63 characters."

        value

type PgvectorIndexConfig =
    private
        { Schema: string
          HnswM: int
          HnswEfConstruction: int }

[<RequireQualifiedAccess>]
module PgvectorIndexConfig =
    let create schema hnswM hnswEfConstruction =
        let schema = PostgresIdentifier.validate (nameof schema) schema

        if hnswM < 2 || hnswM > 100 then
            invalidArg (nameof hnswM) "HNSW m must be between 2 and 100."

        if hnswEfConstruction < 4 || hnswEfConstruction > 1000 then
            invalidArg (nameof hnswEfConstruction) "HNSW ef_construction must be between 4 and 1000."

        if hnswEfConstruction < hnswM then
            invalidArg (nameof hnswEfConstruction) "HNSW ef_construction must not be smaller than m."

        { Schema = schema
          HnswM = hnswM
          HnswEfConstruction = hnswEfConstruction }

    let defaultConfig = create "nao_knowledge" 16 64
    let schema value = value.Schema
    let hnswM value = value.HnswM
    let hnswEfConstruction value = value.HnswEfConstruction

    let internal tableName profile =
        let profileId =
            profile
            |> EmbeddingCapabilityDescriptor.validate
            |> _.ProfileId
            |> EmbeddingProfileId.value

        "vectors_" + profileId.Substring("eprof_".Length, 24)

type PostgresLexicalIndexConfig =
    private
        { Schema: string
          TextSearchConfiguration: string }

[<RequireQualifiedAccess>]
module PostgresLexicalIndexConfig =
    let create schema textSearchConfiguration =
        let schema = PostgresIdentifier.validate (nameof schema) schema

        if
            String.IsNullOrWhiteSpace textSearchConfiguration
            || textSearchConfiguration <> textSearchConfiguration.Trim()
            || textSearchConfiguration.Length > 256
        then
            invalidArg
                (nameof textSearchConfiguration)
                "PostgreSQL text-search configuration must be non-blank, trimmed, and at most 256 characters."

        { Schema = schema
          TextSearchConfiguration = textSearchConfiguration }

    let defaultConfig = create "nao_knowledge" "pg_catalog.simple"
    let schema value = value.Schema
    let textSearchConfiguration value = value.TextSearchConfiguration

    let internal tableName value =
        value.TextSearchConfiguration
        |> ContentHash.computeText
        |> ContentHash.value
        |> fun hash -> "lexical_" + hash.Substring(0, 24)
