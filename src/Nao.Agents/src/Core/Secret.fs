namespace Nao.Agents

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

[<Struct; CustomEquality; CustomComparison>]
type SecretReference =
    private
    | SecretReference of string

    override this.ToString() =
        let (SecretReference value) = this
        value

    override this.Equals(other) =
        match other with
        | :? SecretReference as reference ->
            let (SecretReference left) = this
            let (SecretReference right) = reference
            StringComparer.Ordinal.Equals(left, right)
        | _ -> false

    override this.GetHashCode() =
        let (SecretReference value) = this
        StringComparer.Ordinal.GetHashCode value

    interface IComparable with
        member this.CompareTo other =
            match other with
            | :? SecretReference as reference ->
                let (SecretReference left) = this
                let (SecretReference right) = reference
                StringComparer.Ordinal.Compare(left, right)
            | _ -> invalidArg (nameof other) "Cannot compare values of different types."

[<RequireQualifiedAccess>]
module SecretReference =

    let parse (value: string) =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg (nameof value) "Secret reference must be non-blank and have no surrounding whitespace."

        SecretReference value

    let value (SecretReference value) = value

[<Sealed>]
type SecretValue internal (reference: SecretReference, value: string) =
    member _.Reference = reference
    member internal _.Value = value
    override _.ToString() = "[REDACTED]"

[<RequireQualifiedAccess>]
module SecretValue =

    let create reference (value: string) =
        if isNull value then
            nullArg (nameof value)

        if value.Length = 0 then
            invalidArg (nameof value) "Resolved secret value must not be empty."

        SecretValue(reference, value)

    let reference (secret: SecretValue) = secret.Reference

    let useValue operation (secret: SecretValue) =
        if isNull (box operation) then
            nullArg (nameof operation)

        operation secret.Value

[<Struct; CustomEquality; CustomComparison>]
type SecretVersion =
    private
    | SecretVersion of string

    override this.ToString() =
        let (SecretVersion value) = this
        value

    override this.Equals(other) =
        match other with
        | :? SecretVersion as version ->
            let (SecretVersion left) = this
            let (SecretVersion right) = version
            StringComparer.Ordinal.Equals(left, right)
        | _ -> false

    override this.GetHashCode() =
        let (SecretVersion value) = this
        StringComparer.Ordinal.GetHashCode value

    interface IComparable with
        member this.CompareTo other =
            match other with
            | :? SecretVersion as version ->
                let (SecretVersion left) = this
                let (SecretVersion right) = version
                StringComparer.Ordinal.Compare(left, right)
            | _ -> invalidArg (nameof other) "Cannot compare values of different types."

[<RequireQualifiedAccess>]
module SecretVersion =
    let parse (value: string) =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg (nameof value) "Secret versions must be non-blank and have no surrounding whitespace."

        SecretVersion value

    let initial = SecretVersion "1"
    let value (SecretVersion value) = value

    let internal next (SecretVersion value) =
        let mutable numeric = 0L

        if not (Int64.TryParse(value, &numeric)) || numeric <= 0L then
            invalidOp "The in-memory secret store requires numeric secret versions."

        if numeric = Int64.MaxValue then
            invalidOp "Secret version space is exhausted."

        SecretVersion(string (numeric + 1L))

[<Sealed>]
type SecretRedactor private () =
    let values = ConcurrentDictionary<string, unit>(StringComparer.Ordinal)

    member internal _.Register(secret: SecretValue) =
        SecretValue.useValue (fun value -> values.TryAdd(value, ()) |> ignore) secret

    member _.Redact(value: string) =
        if isNull value || values.IsEmpty then
            value
        else
            values.Keys
            |> Seq.sortByDescending _.Length
            |> Seq.fold
                (fun sanitized secret -> sanitized.Replace(secret, "[REDACTED]", StringComparison.Ordinal))
                value

    member internal _.Snapshot() = values.Keys |> Seq.toArray

    static member internal Create() = SecretRedactor()

[<Sealed>]
type SecretRedactionStream internal (secrets: string array) =
    let mutable pending = ""

    let redact (value: string) =
        secrets
        |> Seq.sortByDescending _.Length
        |> Seq.fold
            (fun (sanitized: string) secret -> sanitized.Replace(secret, "[REDACTED]", StringComparison.Ordinal))
            value

    member _.Push(value: string, finalChunk: bool) =
        let combined = pending + (value |> Option.ofObj |> Option.defaultValue "")

        let retainedLength =
            if finalChunk then
                0
            else
                secrets
                |> Seq.collect (fun secret -> seq { 1 .. min (secret.Length - 1) combined.Length })
                |> Seq.filter (fun length ->
                    secrets
                    |> Seq.exists (fun secret ->
                        secret.StartsWith(combined.Substring(combined.Length - length), StringComparison.Ordinal)))
                |> Seq.fold max 0

        let emittedLength = combined.Length - retainedLength
        let emitted = combined.Substring(0, emittedLength) |> redact
        pending <- combined.Substring(emittedLength)
        emitted

    member this.Flush() = this.Push("", true)

[<RequireQualifiedAccess>]
module SecretRedaction =
    let private active = AsyncLocal<SecretRedactor option>()

    let internal register secret =
        active.Value |> Option.iter (fun redactor -> redactor.Register secret)

    let redact value =
        active.Value
        |> Option.map (fun redactor -> redactor.Redact value)
        |> Option.defaultValue value

    let redactOption = Option.map redact

    let redactMap values =
        values
        |> Map.toSeq
        |> Seq.map (fun (key, value) -> redact key, redact value)
        |> Map.ofSeq

    let stream () =
        active.Value
        |> Option.map _.Snapshot()
        |> Option.defaultValue Array.empty
        |> SecretRedactionStream

    let runAsync (operation: unit -> Task<'Value>) =
        task {
            match active.Value with
            | Some _ -> return! operation ()
            | None ->
                active.Value <- Some(SecretRedactor.Create())

                try
                    return! operation ()
                finally
                    active.Value <- None
        }

type SecretResolutionEvidence =
    { ProviderId: string
      ProviderVersion: string
      Reference: SecretReference
      SecretVersion: SecretVersion
      ResolvedAt: DateTimeOffset }

type SecretResolution =
    private
        { Value: SecretValue
          Evidence: SecretResolutionEvidence }

[<RequireQualifiedAccess>]
type SecretLookup =
    | Available of version: SecretVersion * value: SecretValue
    | Revoked of version: SecretVersion * revokedAt: DateTimeOffset
    | Missing

exception SecretRevokedException of reference: SecretReference * version: SecretVersion * revokedAt: DateTimeOffset

[<NoEquality; NoComparison>]
type SecretProvider =
    private
        { ProviderId: string
          ProviderVersion: string
          ResolveAsync: AuthorizationScope -> SecretReference -> CancellationToken -> Task<SecretLookup> }

[<RequireQualifiedAccess>]
type SecretMutationKind =
    | Rotated
    | Revoked

type SecretMutationEvidence =
    { ProviderId: string
      ProviderVersion: string
      Reference: SecretReference
      Kind: SecretMutationKind
      PreviousVersion: SecretVersion
      Version: SecretVersion
      ChangedAt: DateTimeOffset }

[<NoEquality; NoComparison>]
type SecretManager =
    private
        { Provider: SecretProvider
          RotateAsync:
              AuthorizationScope
                  -> SecretReference
                  -> SecretVersion
                  -> SecretValue
                  -> CancellationToken
                  -> Task<SecretMutationEvidence>
          RevokeAsync:
              AuthorizationScope
                  -> SecretReference
                  -> SecretVersion
                  -> CancellationToken
                  -> Task<SecretMutationEvidence> }

[<RequireQualifiedAccess>]
module SecretManager =
    let create provider rotateAsync revokeAsync =
        if isNull (box rotateAsync) then
            nullArg (nameof rotateAsync)

        if isNull (box revokeAsync) then
            nullArg (nameof revokeAsync)

        { Provider = provider
          RotateAsync = rotateAsync
          RevokeAsync = revokeAsync }

    let provider store = store.Provider

    let rotateAsync authorization reference expectedVersion value cancellationToken store =
        store.RotateAsync authorization reference expectedVersion value cancellationToken

    let revokeAsync authorization reference expectedVersion cancellationToken store =
        store.RevokeAsync authorization reference expectedVersion cancellationToken

[<RequireQualifiedAccess>]
module SecretResolution =

    let value resolution = resolution.Value
    let evidence resolution = resolution.Evidence

[<RequireQualifiedAccess>]
module SecretProvider =

    let private required name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name "Value must be non-blank and have no surrounding whitespace."

        value

    let create providerId providerVersion resolveAsync =
        let providerId = required (nameof providerId) providerId
        let providerVersion = required (nameof providerVersion) providerVersion

        if isNull (box resolveAsync) then
            nullArg (nameof resolveAsync)

        { ProviderId = providerId
          ProviderVersion = providerVersion
          ResolveAsync = resolveAsync }

    let resolveAsync authorization reference (cancellationToken: CancellationToken) provider =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            let! lookup =
                provider.ResolveAsync authorization reference cancellationToken
                |> _.WaitAsync(cancellationToken)

            match lookup with
            | SecretLookup.Missing ->
                return
                    raise (
                        KeyNotFoundException($"Secret reference '{SecretReference.value reference}' was not resolved.")
                    )
            | SecretLookup.Revoked(version, revokedAt) ->
                return raise (SecretRevokedException(reference, version, revokedAt))
            | SecretLookup.Available(_, value) when SecretValue.reference value <> reference ->
                return
                    raise (
                        InvalidOperationException(
                            $"Secret provider '{provider.ProviderId}' returned material for a different reference."
                        )
                    )
            | SecretLookup.Available(version, value) ->
                SecretRedaction.register value

                return
                    { Value = value
                      Evidence =
                        { ProviderId = provider.ProviderId
                          ProviderVersion = provider.ProviderVersion
                          Reference = reference
                          SecretVersion = version
                          ResolvedAt = DateTimeOffset.UtcNow } }
        }

    let inMemory providerId providerVersion entries =
        let providerId = required (nameof providerId) providerId
        let sync = obj ()

        let values =
            ConcurrentDictionary<string * SecretReference, SecretVersion * SecretValue option * DateTimeOffset option>()

        for owner, value in entries do
            if
                not (
                    values.TryAdd(
                        (AuthorizationIdentity.storageKey owner, SecretValue.reference value),
                        (SecretVersion.initial, Some value, None)
                    )
                )
            then
                invalidArg (nameof entries) "In-memory secret provider entries must be unique by owner and reference."

        let key (authorization: AuthorizationScope) (reference: SecretReference) =
            AuthorizationScope.identity authorization |> AuthorizationIdentity.storageKey, reference

        let provider =
            create providerId providerVersion (fun authorization reference cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()

                match values.TryGetValue(key authorization reference) with
                | true, (version, Some value, None) -> Task.FromResult(SecretLookup.Available(version, value))
                | true, (version, None, Some revokedAt) -> Task.FromResult(SecretLookup.Revoked(version, revokedAt))
                | true, _ -> invalidOp "Secret state is internally inconsistent."
                | false, _ -> Task.FromResult SecretLookup.Missing)

        let change
            (kind: SecretMutationKind)
            (authorization: AuthorizationScope)
            (reference: SecretReference)
            (expectedVersion: SecretVersion)
            (cancellationToken: CancellationToken)
            (update: SecretVersion -> DateTimeOffset -> SecretVersion * SecretValue option * DateTimeOffset option)
            =
            cancellationToken.ThrowIfCancellationRequested()

            lock sync (fun () ->
                let storageKey = key authorization reference

                match values.TryGetValue storageKey with
                | false, _ ->
                    raise (
                        KeyNotFoundException(
                            $"Secret reference '{SecretReference.value reference}' was not found for mutation."
                        )
                    )
                | true, (currentVersion, _, _) when currentVersion <> expectedVersion ->
                    raise (
                        InvalidOperationException(
                            $"Secret reference '{SecretReference.value reference}' changed from expected version {expectedVersion}."
                        )
                    )
                | true, _ ->
                    let changedAt = DateTimeOffset.UtcNow
                    let version = SecretVersion.next expectedVersion
                    values.[storageKey] <- update version changedAt

                    { ProviderId = providerId
                      ProviderVersion = providerVersion
                      Reference = reference
                      Kind = kind
                      PreviousVersion = expectedVersion
                      Version = version
                      ChangedAt = changedAt })
            |> Task.FromResult

        SecretManager.create
            provider
            (fun authorization reference expectedVersion value cancellationToken ->
                if SecretValue.reference value <> reference then
                    invalidArg (nameof value) "Rotated secret material must match the requested reference."

                change
                    SecretMutationKind.Rotated
                    authorization
                    reference
                    expectedVersion
                    cancellationToken
                    (fun version _ -> version, Some value, None))
            (fun authorization reference expectedVersion cancellationToken ->
                change
                    SecretMutationKind.Revoked
                    authorization
                    reference
                    expectedVersion
                    cancellationToken
                    (fun version changedAt -> version, None, Some changedAt))
