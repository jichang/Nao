namespace Nao.Knowledge

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<Struct>]
type PersistedEmbeddingId = private PersistedEmbeddingId of string

[<RequireQualifiedAccess>]
module PersistedEmbeddingId =
    let derive chunkId profileId =
        String.concat "\u001f" [ KnowledgeChunkId.value chunkId; EmbeddingProfileId.value profileId ]
        |> ContentHash.computeText
        |> ContentHash.value
        |> fun hash -> PersistedEmbeddingId("pembed_" + hash)

    let value (PersistedEmbeddingId value) = value

    let tryParse (value: string) =
        if
            not (isNull value)
            && value.StartsWith("pembed_", StringComparison.Ordinal)
            && value.Length = 71
            && value[7..]
               |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')
        then
            Some(PersistedEmbeddingId value)
        else
            None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid canonical persisted-embedding ID.")

type PersistedEmbedding =
    private
        { Version: int
          Id: PersistedEmbeddingId
          Owner: AuthorizationIdentity
          ChunkId: KnowledgeChunkId
          ContentHash: ContentHash
          Profile: EmbeddingCapabilityDescriptor
          Values: float array
          GeneratedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module PersistedEmbedding =
    let currentVersion = 1

    let create owner profile (generated: GeneratedEmbedding) =
        let profile = EmbeddingCapabilityDescriptor.validate profile

        if generated.ProfileId <> profile.ProfileId then
            invalidArg (nameof generated) "Generated embedding does not match the persistence profile."

        if isNull generated.Values || generated.Values.Length <> profile.Dimensions then
            invalidArg (nameof generated) "Generated embedding dimensions do not match the persistence profile."

        if generated.Values |> Array.exists (Double.IsFinite >> not) then
            invalidArg (nameof generated) "Generated embedding contains a non-finite value."

        { Version = currentVersion
          Id = PersistedEmbeddingId.derive generated.ChunkId generated.ProfileId
          Owner = owner
          ChunkId = generated.ChunkId
          ContentHash = generated.ContentHash
          Profile = profile
          Values = Array.copy generated.Values
          GeneratedAt = generated.GeneratedAt }

    let version value = value.Version
    let id value = value.Id
    let owner value = value.Owner
    let chunkId value = value.ChunkId
    let contentHash value = value.ContentHash
    let profile value = value.Profile
    let values value = Array.copy value.Values
    let generatedAt value = value.GeneratedAt

    let internal clone (value: PersistedEmbedding) =
        { value with
            Values = Array.copy value.Values }

    let internal equivalent first second =
        first.Id = second.Id
        && first.Owner = second.Owner
        && first.ChunkId = second.ChunkId
        && first.ContentHash = second.ContentHash
        && first.Profile = second.Profile
        && first.Values = second.Values

type KnowledgeEmbeddingStore =
    { SaveBatchAsync: AuthorizationScope -> PersistedEmbedding list -> CancellationToken -> Task
      TryGetAsync: AuthorizationScope -> PersistedEmbeddingId -> CancellationToken -> Task<PersistedEmbedding option> }

[<RequireQualifiedAccess>]
module EmbeddingPersistence =
    let saveAsync
        (store: KnowledgeEmbeddingStore)
        (authorization: AuthorizationScope)
        (profile: EmbeddingCapabilityDescriptor)
        (generated: GeneratedEmbedding list)
        (cancellationToken: CancellationToken)
        =
        cancellationToken.ThrowIfCancellationRequested()

        if isNull (box generated) then
            nullArg (nameof generated)

        let persisted =
            generated
            |> List.map (PersistedEmbedding.create (AuthorizationScope.identity authorization) profile)

        if
            (persisted |> List.map PersistedEmbedding.id |> Set.ofList).Count
            <> persisted.Length
        then
            invalidArg (nameof generated) "Embedding persistence batch contains duplicate chunk profiles."

        store.SaveBatchAsync authorization persisted cancellationToken

[<RequireQualifiedAccess>]
module KnowledgeEmbeddingStores =
    let inMemory () =
        let gate = obj ()

        let partitions =
            Dictionary<string, Dictionary<PersistedEmbeddingId, PersistedEmbedding>>()

        let partition authorization =
            authorization |> AuthorizationScope.identity |> AuthorizationIdentity.storageKey

        { SaveBatchAsync =
            fun authorization values cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()
                let identity = AuthorizationScope.identity authorization
                let key = partition authorization

                values
                |> List.iter (fun value ->
                    if PersistedEmbedding.owner value <> identity then
                        raise (
                            UnauthorizedAccessException
                                "Persisted embedding belongs to another authorization identity."
                        ))

                if (values |> List.map PersistedEmbedding.id |> Set.ofList).Count <> values.Length then
                    invalidArg (nameof values) "Embedding store batch contains duplicate identities."

                lock gate (fun () ->
                    let records =
                        match partitions.TryGetValue key with
                        | true, existing -> existing
                        | false, _ ->
                            let created = Dictionary()
                            partitions.Add(key, created)
                            created

                    values
                    |> List.iter (fun value ->
                        let embeddingId = PersistedEmbedding.id value

                        match records.TryGetValue embeddingId with
                        | true, existing when not (PersistedEmbedding.equivalent existing value) ->
                            invalidOp "Persisted embedding ID is already bound to different vector data."
                        | _ -> ())

                    values
                    |> List.iter (fun value ->
                        let embeddingId = PersistedEmbedding.id value

                        if not (records.ContainsKey embeddingId) then
                            records.Add(embeddingId, PersistedEmbedding.clone value)))

                Task.CompletedTask
          TryGetAsync =
            fun authorization embeddingId cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()
                let key = partition authorization

                lock gate (fun () ->
                    match partitions.TryGetValue key with
                    | true, records ->
                        match records.TryGetValue embeddingId with
                        | true, value -> Some(PersistedEmbedding.clone value)
                        | false, _ -> None
                    | false, _ -> None)
                |> Task.FromResult }
