namespace Nao.Agents

open System
open System.Security.Cryptography
open System.Text

[<Struct>]
type ArtifactSchemaVersion = private ArtifactSchemaVersion of int

[<RequireQualifiedAccess>]
module ArtifactSchemaVersion =
    let current = ArtifactSchemaVersion 1

    let parse =
        function
        | 1 -> current
        | value -> invalidArg (nameof value) $"Unsupported artifact schema version '{value}'."

    let value (ArtifactSchemaVersion value) = value

[<Struct>]
type ArtifactContentHash = private ArtifactContentHash of string

[<RequireQualifiedAccess>]
module ArtifactContentHash =
    let compute (payload: string) =
        if isNull payload then
            nullArg (nameof payload)

        payload
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()
        |> ArtifactContentHash

    let tryParse (value: string) =
        if
            not (String.IsNullOrWhiteSpace value)
            && value.Length = 64
            && value
               |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')
        then
            Some(ArtifactContentHash value)
        else
            None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () ->
            invalidArg (nameof value) "Artifact content hash must be canonical lowercase SHA-256.")

    let value (ArtifactContentHash value) = value

[<Struct>]
type ArtifactAddress = private ArtifactAddress of string

[<RequireQualifiedAccess>]
module ArtifactAddress =
    let parse (value: string) =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri -> ArtifactAddress uri.AbsoluteUri
        | false, _ -> invalidArg (nameof value) "Artifact address must be an absolute URI."

    let forProduced artifactId =
        ArtifactAddress($"nao://artifacts/{ArtifactId.serialize artifactId}")

    let value (ArtifactAddress value) = value

[<RequireQualifiedAccess>]
type ArtifactRole =
    | Consumed
    | Produced

[<RequireQualifiedAccess>]
module ArtifactRole =
    let serialize =
        function
        | ArtifactRole.Consumed -> "consumed"
        | ArtifactRole.Produced -> "produced"

    let parse =
        function
        | "consumed" -> ArtifactRole.Consumed
        | "produced" -> ArtifactRole.Produced
        | value -> invalidArg (nameof value) "Artifact role must be 'consumed' or 'produced'."

type ArtifactLineage =
    private
        { RecordedBy: ExecutionId
          RecordedAt: DateTimeOffset
          ParentArtifactIds: ArtifactId list }

[<RequireQualifiedAccess>]
module ArtifactLineage =
    let create recordedBy recordedAt parentArtifactIds =
        if recordedAt = DateTimeOffset.MinValue then
            invalidArg (nameof recordedAt) "Artifact recording time is required."

        let distinctParents = parentArtifactIds |> List.distinct

        if distinctParents.Length <> parentArtifactIds.Length then
            invalidArg (nameof parentArtifactIds) "Artifact parent identities must be unique."

        { RecordedBy = recordedBy
          RecordedAt = recordedAt
          ParentArtifactIds = parentArtifactIds }

    let recordedBy lineage = lineage.RecordedBy
    let recordedAt lineage = lineage.RecordedAt
    let parentArtifactIds lineage = lineage.ParentArtifactIds

/// Immutable, addressable object consumed or produced by an execution.
type Artifact =
    private
        { SchemaVersion: ArtifactSchemaVersion
          Id: ArtifactId
          IdempotencyKey: IdempotencyKey
          Role: ArtifactRole
          Address: ArtifactAddress
          Kind: string
          ContentType: string
          Payload: string
          ContentHash: ArtifactContentHash
          Lineage: ArtifactLineage }

[<RequireQualifiedAccess>]
module Artifact =
    let private required name (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg name "Artifact value is required."

        value.Trim()

    let restore
        schemaVersion
        id
        idempotencyKey
        role
        address
        kind
        contentType
        payload
        contentHash
        recordedBy
        recordedAt
        parentArtifactIds
        =
        if schemaVersion <> ArtifactSchemaVersion.current then
            invalidArg (nameof schemaVersion) "Artifact schema version is not supported."

        if parentArtifactIds |> List.contains id then
            invalidArg (nameof parentArtifactIds) "An artifact cannot derive from itself."

        let redactedPayload = SecretRedaction.redact payload
        let actualHash = ArtifactContentHash.compute redactedPayload

        if actualHash <> contentHash then
            invalidArg (nameof contentHash) "Artifact content hash does not match the redacted payload."

        { SchemaVersion = schemaVersion
          Id = id
          IdempotencyKey = idempotencyKey
          Role = role
          Address = address
          Kind = required (nameof kind) kind
          ContentType = required (nameof contentType) contentType
          Payload = redactedPayload
          ContentHash = contentHash
          Lineage = ArtifactLineage.create recordedBy recordedAt parentArtifactIds }

    let private create role address recordedBy recordedAt parentArtifactIds idempotencyKey kind contentType payload =
        let id = ArtifactId.generate ()
        let artifactAddress = address id
        let redactedPayload = SecretRedaction.redact payload

        restore
            ArtifactSchemaVersion.current
            id
            idempotencyKey
            role
            artifactAddress
            kind
            contentType
            redactedPayload
            (ArtifactContentHash.compute redactedPayload)
            recordedBy
            recordedAt
            parentArtifactIds

    let produced recordedBy recordedAt parentArtifactIds idempotencyKey kind contentType payload =
        create
            ArtifactRole.Produced
            ArtifactAddress.forProduced
            recordedBy
            recordedAt
            parentArtifactIds
            idempotencyKey
            kind
            contentType
            payload

    let consumed recordedBy recordedAt parentArtifactIds idempotencyKey address kind contentType payload =
        create
            ArtifactRole.Consumed
            (fun _ -> address)
            recordedBy
            recordedAt
            parentArtifactIds
            idempotencyKey
            kind
            contentType
            payload

    let schemaVersion artifact = artifact.SchemaVersion
    let id artifact = artifact.Id
    let idempotencyKey artifact = artifact.IdempotencyKey
    let role artifact = artifact.Role
    let address artifact = artifact.Address
    let kind artifact = artifact.Kind
    let contentType artifact = artifact.ContentType
    let payload artifact = artifact.Payload
    let contentHash artifact = artifact.ContentHash
    let lineage artifact = artifact.Lineage
