namespace Nao.Identity.Oidc

open System
open System.Security.Claims
open Nao.Agents

[<RequireQualifiedAccess>]
type OidcPrincipalFailureKind =
    | InvalidConfiguration
    | Unauthenticated
    | MissingClaim
    | AmbiguousClaim
    | InvalidClaim

type OidcPrincipalFailure =
    { Kind: OidcPrincipalFailureKind
      ClaimType: string option
      Message: string }

[<NoEquality; NoComparison>]
type OidcClaimMapping =
    { TenantClaim: string
      SubjectClaim: string
      ServiceClaim: string option
      ServiceDelegationClaim: string option
      ImpersonatedUserClaim: string option
      ImpersonationReasonClaim: string option
      GroupClaims: string list
      RoleClaims: string list
      AdditionalClaims: string list
      ResolveAuthenticationStrength: ClaimsIdentity -> Result<AuthenticationStrength, string> }

[<RequireQualifiedAccess>]
module OidcAuthenticationStrength =
    let fixedValue strength : ClaimsIdentity -> Result<AuthenticationStrength, string> = fun _ -> Ok strength

    let fromClaim claimType mappings : ClaimsIdentity -> Result<AuthenticationStrength, string> =
        fun identity ->
            let values =
                identity.Claims
                |> Seq.filter (fun claim -> claim.Type = claimType)
                |> Seq.map _.Value
                |> Seq.toList

            match values with
            | [ value ] ->
                match Map.tryFind value mappings with
                | Some strength -> Ok strength
                | None -> Error(sprintf "Unsupported authentication context '%s'." value)
            | [] -> Error(sprintf "Required authentication context claim '%s' is missing." claimType)
            | _ -> Error(sprintf "Authentication context claim '%s' is ambiguous." claimType)

[<RequireQualifiedAccess>]
module OidcPrincipalAdapter =
    type private ResultBuilder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return value = Ok value
        member _.ReturnFrom value = value
        member _.Zero() = Ok()
        member _.Delay generator = generator
        member _.Run generator = generator ()
        member _.Combine(value, next) = Result.bind (fun () -> next ()) value

        member _.TryWith(generator, handler) =
            try
                generator ()
            with error ->
                handler error

    let private result = ResultBuilder()

    let private failure kind claimType message =
        Error
            { Kind = kind
              ClaimType = claimType
              Message = message }

    let private validateMapping mapping =
        let configuredClaims =
            [ Some mapping.TenantClaim
              Some mapping.SubjectClaim
              mapping.ServiceClaim
              mapping.ServiceDelegationClaim
              mapping.ImpersonatedUserClaim
              mapping.ImpersonationReasonClaim ]
            @ (mapping.GroupClaims |> List.map Some)
            @ (mapping.RoleClaims |> List.map Some)
            @ (mapping.AdditionalClaims |> List.map Some)
            |> List.choose id

        let invalidClaim =
            configuredClaims
            |> List.tryFind (fun value -> String.IsNullOrWhiteSpace value || value <> value.Trim())

        match invalidClaim with
        | Some _ ->
            failure
                OidcPrincipalFailureKind.InvalidConfiguration
                None
                "Every configured OIDC claim type must be canonical and non-blank."
        | None when configuredClaims |> List.distinct |> List.length <> configuredClaims.Length ->
            failure
                OidcPrincipalFailureKind.InvalidConfiguration
                None
                "Each configured OIDC claim type must have exactly one authorization purpose."
        | None when
            Option.isSome mapping.ImpersonatedUserClaim
            <> Option.isSome mapping.ImpersonationReasonClaim
            ->
            failure
                OidcPrincipalFailureKind.InvalidConfiguration
                None
                "Impersonated-user and impersonation-reason claim mappings must be configured together."
        | None -> Ok()

    let private values (identity: ClaimsIdentity) claimType =
        identity.Claims
        |> Seq.filter (fun claim -> claim.Type = claimType)
        |> Seq.map _.Value
        |> Seq.toList

    let private required identity claimType =
        match values identity claimType with
        | [ value ] -> Ok value
        | [] ->
            failure
                OidcPrincipalFailureKind.MissingClaim
                (Some claimType)
                (sprintf "Required claim '%s' is missing." claimType)
        | _ ->
            failure
                OidcPrincipalFailureKind.AmbiguousClaim
                (Some claimType)
                (sprintf "Required claim '%s' has multiple values." claimType)

    let private optional identity claimType =
        match values identity claimType with
        | [] -> Ok None
        | [ value ] -> Ok(Some value)
        | _ ->
            failure
                OidcPrincipalFailureKind.AmbiguousClaim
                (Some claimType)
                (sprintf "Optional claim '%s' has multiple values." claimType)

    let private parseClaim claimType parser value =
        match parser value with
        | Some parsed -> Ok parsed
        | None ->
            failure
                OidcPrincipalFailureKind.InvalidClaim
                (Some claimType)
                (sprintf "Claim '%s' has an invalid value." claimType)

    let private collectMany (identity: ClaimsIdentity) claimTypes =
        claimTypes |> List.collect (values identity)

    let private collectAdditional identity claimTypes =
        let folder state claimType =
            state
            |> Result.bind (fun claims ->
                optional identity claimType
                |> Result.map (function
                    | Some value -> Map.add claimType value claims
                    | None -> claims))

        claimTypes |> List.fold folder (Ok Map.empty)

    let fromIdentity mapping (identity: ClaimsIdentity) =
        result {
            do! validateMapping mapping

            if isNull identity || not identity.IsAuthenticated then
                return!
                    failure
                        OidcPrincipalFailureKind.Unauthenticated
                        None
                        "OIDC identity must already be authenticated by the host."

            let! tenantValue = required identity mapping.TenantClaim
            let! subjectValue = required identity mapping.SubjectClaim
            let! tenantId = parseClaim mapping.TenantClaim TenantId.tryParse tenantValue
            let! subjectId = parseClaim mapping.SubjectClaim UserId.tryParse subjectValue

            let! serviceId =
                match mapping.ServiceClaim with
                | Some claimType ->
                    optional identity claimType
                    |> Result.bind (function
                        | Some value -> parseClaim claimType ServiceId.tryParse value |> Result.map Some
                        | None -> Ok None)
                | None -> Ok None

            let! serviceDelegations =
                match mapping.ServiceDelegationClaim with
                | Some claimType ->
                    collectMany identity [ claimType ]
                    |> List.fold
                        (fun parsed value ->
                            parsed
                            |> Result.bind (fun services ->
                                parseClaim claimType ServiceId.tryParse value
                                |> Result.map (fun service -> service :: services)))
                        (Ok [])
                    |> Result.map List.rev
                | None -> Ok []

            let! impersonatedUser, impersonationReason =
                match mapping.ImpersonatedUserClaim, mapping.ImpersonationReasonClaim with
                | Some userClaim, Some reasonClaim ->
                    match optional identity userClaim, optional identity reasonClaim with
                    | Ok None, Ok None -> Ok(None, None)
                    | Ok(Some user), Ok(Some reason) ->
                        parseClaim userClaim UserId.tryParse user
                        |> Result.map (fun parsed -> Some parsed, Some reason)
                    | Error error, _
                    | _, Error error -> Error error
                    | _ ->
                        failure
                            OidcPrincipalFailureKind.InvalidClaim
                            None
                            "Impersonated-user and impersonation-reason claims must occur together."
                | _ -> Ok(None, None)

            if Option.isSome impersonatedUser && Option.isSome serviceId then
                return!
                    failure
                        OidcPrincipalFailureKind.InvalidClaim
                        None
                        "User impersonation and service authentication cannot be combined."

            if serviceId.IsNone && not serviceDelegations.IsEmpty then
                return!
                    failure
                        OidcPrincipalFailureKind.InvalidClaim
                        mapping.ServiceDelegationClaim
                        "Service delegation requires an authenticated root service."

            let groupValues = collectMany identity mapping.GroupClaims
            let roleValues = collectMany identity mapping.RoleClaims

            let! groupIds =
                groupValues
                |> List.fold
                    (fun parsed value ->
                        parsed
                        |> Result.bind (fun groups ->
                            parseClaim "group" GroupId.tryParse value
                            |> Result.map (fun group -> group :: groups)))
                    (Ok [])
                |> Result.map List.rev

            let! additionalClaims = collectAdditional identity mapping.AdditionalClaims

            let! authenticationStrength =
                mapping.ResolveAuthenticationStrength identity
                |> Result.mapError (fun message ->
                    { Kind = OidcPrincipalFailureKind.InvalidClaim
                      ClaimType = None
                      Message = message })

            try
                match impersonatedUser, impersonationReason, serviceId with
                | Some effectiveUserId, Some reason, None ->
                    return
                        SecurityPrincipal.createImpersonated
                            tenantId
                            subjectId
                            effectiveUserId
                            reason
                            groupIds
                            roleValues
                            additionalClaims
                            authenticationStrength
                | None, None, Some rootService ->
                    return
                        SecurityPrincipal.createServiceAuthenticated
                            tenantId
                            subjectId
                            (rootService :: serviceDelegations)
                            groupIds
                            roleValues
                            additionalClaims
                            authenticationStrength
                | None, None, None ->
                    return
                        SecurityPrincipal.createAuthenticated
                            tenantId
                            subjectId
                            None
                            groupIds
                            roleValues
                            additionalClaims
                            authenticationStrength
                | _ ->
                    return! failure OidcPrincipalFailureKind.InvalidClaim None "OIDC authority claims are inconsistent."
            with :? ArgumentException as error ->
                return! failure OidcPrincipalFailureKind.InvalidClaim None error.Message
        }

    let fromPrincipal mapping (principal: ClaimsPrincipal) =
        if isNull principal then
            failure OidcPrincipalFailureKind.Unauthenticated None "OIDC principal is missing."
        else
            match principal.Identities |> Seq.toList with
            | [ identity ] -> fromIdentity mapping identity
            | [] -> failure OidcPrincipalFailureKind.Unauthenticated None "OIDC principal has no identity."
            | _ ->
                failure OidcPrincipalFailureKind.AmbiguousClaim None "OIDC principal must contain exactly one identity."
