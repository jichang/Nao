namespace Nao.Identity.Oidc.Tests

open System.Security.Claims
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Identity.Oidc

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type OidcPrincipalAdapterTests() =
    let mapping =
        { TenantClaim = "tid"
          SubjectClaim = "sub"
          ServiceClaim = Some "client_id"
          ServiceDelegationClaim = Some "delegated_service"
          ImpersonatedUserClaim = Some "effective_user"
          ImpersonationReasonClaim = Some "impersonation_reason"
          GroupClaims = [ "groups" ]
          RoleClaims = [ "roles" ]
          AdditionalClaims = [ "region" ]
          ResolveAuthenticationStrength =
            OidcAuthenticationStrength.fromClaim
                "acr"
                (Map.ofList
                    [ "urn:single-factor", AuthenticationStrength.SingleFactor
                      "urn:mfa", AuthenticationStrength.MultiFactor
                      "urn:phishing-resistant", AuthenticationStrength.PhishingResistant ]) }

    let identity authenticated (claims: (string * string) list) =
        let claims = claims |> List.map (fun (claimType, value) -> Claim(claimType, value))

        if authenticated then
            ClaimsIdentity(claims, "oidc")
        else
            ClaimsIdentity(claims)

    let directClaims =
        [ "tid", "tenant"
          "sub", "user"
          "groups", "engineering"
          "groups", "operations"
          "roles", "operator"
          "roles", "reader"
          "region", "eu-west"
          "acr", "urn:mfa" ]

    let requireSuccess (result: Result<SecurityPrincipal, OidcPrincipalFailure>) =
        match result with
        | Ok principal -> principal
        | Error failure -> failwith failure.Message

    let assertFailure expectedKind expectedClaim (result: Result<SecurityPrincipal, OidcPrincipalFailure>) =
        match result with
        | Ok _ -> failwith "Expected OIDC principal mapping to fail."
        | Error failure ->
            Assert.AreEqual(expectedKind, failure.Kind)
            Assert.AreEqual(expectedClaim, failure.ClaimType)

    [<TestMethod>]
    member _.MapsValidatedOidcClaimsWithoutProviderCoupling() =
        let principal =
            identity true directClaims
            |> OidcPrincipalAdapter.fromIdentity mapping
            |> requireSuccess

        Assert.AreEqual(TenantId.parse "tenant", SecurityPrincipal.tenantId principal)
        Assert.AreEqual(UserId.parse "user", SecurityPrincipal.subjectId principal)
        Assert.AreEqual(UserId.parse "user", SecurityPrincipal.userId principal)
        Assert.AreEqual(None, SecurityPrincipal.serviceId principal)
        Assert.AreEqual([], SecurityPrincipal.serviceLineage principal)
        Assert.AreEqual(None, SecurityPrincipal.impersonation principal)

        CollectionAssert.AreEquivalent(
            [| GroupId.parse "engineering"; GroupId.parse "operations" |],
            SecurityPrincipal.groupIds principal |> Set.toArray
        )

        CollectionAssert.AreEquivalent([| "operator"; "reader" |], SecurityPrincipal.roles principal |> Set.toArray)

        Assert.AreEqual(Map.ofList [ "region", "eu-west" ], SecurityPrincipal.claims principal)
        Assert.AreEqual(AuthenticationStrength.MultiFactor, SecurityPrincipal.authenticationStrength principal)

    [<TestMethod>]
    member _.MapsOrderedServiceDelegationAndExplicitImpersonation() =
        let servicePrincipal =
            identity
                true
                (directClaims
                 @ [ "client_id", "gateway"
                     "delegated_service", "coordinator"
                     "delegated_service", "worker" ])
            |> OidcPrincipalAdapter.fromIdentity mapping
            |> requireSuccess

        Assert.AreEqual(
            [ ServiceId.parse "gateway"
              ServiceId.parse "coordinator"
              ServiceId.parse "worker" ],
            SecurityPrincipal.serviceLineage servicePrincipal
        )

        let impersonated =
            identity
                true
                (directClaims
                 @ [ "effective_user", "customer"; "impersonation_reason", "support-case-123" ])
            |> OidcPrincipalAdapter.fromIdentity mapping
            |> requireSuccess

        Assert.AreEqual(UserId.parse "user", SecurityPrincipal.subjectId impersonated)
        Assert.AreEqual(UserId.parse "customer", SecurityPrincipal.userId impersonated)

        Assert.AreEqual(
            Some
                { EffectiveUserId = UserId.parse "customer"
                  Reason = "support-case-123" },
            SecurityPrincipal.impersonation impersonated
        )

    [<TestMethod>]
    member _.RejectsUnauthenticatedOrAmbiguousClaimsPrincipals() =
        identity false directClaims
        |> OidcPrincipalAdapter.fromIdentity mapping
        |> assertFailure OidcPrincipalFailureKind.Unauthenticated None

        ClaimsPrincipal()
        |> OidcPrincipalAdapter.fromPrincipal mapping
        |> assertFailure OidcPrincipalFailureKind.Unauthenticated None

        ClaimsPrincipal([ identity true directClaims; identity true directClaims ])
        |> OidcPrincipalAdapter.fromPrincipal mapping
        |> assertFailure OidcPrincipalFailureKind.AmbiguousClaim None

    [<TestMethod>]
    member _.RejectsMissingDuplicateAndMalformedIdentityClaims() =
        directClaims
        |> List.filter (fst >> (<>) "tid")
        |> identity true
        |> OidcPrincipalAdapter.fromIdentity mapping
        |> assertFailure OidcPrincipalFailureKind.MissingClaim (Some "tid")

        ("tid", "other") :: directClaims
        |> identity true
        |> OidcPrincipalAdapter.fromIdentity mapping
        |> assertFailure OidcPrincipalFailureKind.AmbiguousClaim (Some "tid")

        directClaims
        |> List.map (fun (claimType, value) ->
            if claimType = "sub" then
                claimType, " user"
            else
                claimType, value)
        |> identity true
        |> OidcPrincipalAdapter.fromIdentity mapping
        |> assertFailure OidcPrincipalFailureKind.InvalidClaim (Some "sub")

        ("region", "other") :: directClaims
        |> identity true
        |> OidcPrincipalAdapter.fromIdentity mapping
        |> assertFailure OidcPrincipalFailureKind.AmbiguousClaim (Some "region")

    [<TestMethod>]
    member _.RejectsInvalidAuthenticationImpersonationAndServiceAuthority() =
        directClaims
        |> List.map (fun (claimType, value) ->
            if claimType = "acr" then
                claimType, "unknown"
            else
                claimType, value)
        |> identity true
        |> OidcPrincipalAdapter.fromIdentity mapping
        |> assertFailure OidcPrincipalFailureKind.InvalidClaim None

        (directClaims @ [ "effective_user", "customer" ])
        |> identity true
        |> OidcPrincipalAdapter.fromIdentity mapping
        |> assertFailure OidcPrincipalFailureKind.InvalidClaim None

        (directClaims
         @ [ "effective_user", "customer"
             "impersonation_reason", "support-case-123"
             "client_id", "gateway" ])
        |> identity true
        |> OidcPrincipalAdapter.fromIdentity mapping
        |> assertFailure OidcPrincipalFailureKind.InvalidClaim None

        (directClaims @ [ "delegated_service", "worker" ])
        |> identity true
        |> OidcPrincipalAdapter.fromIdentity mapping
        |> assertFailure OidcPrincipalFailureKind.InvalidClaim (Some "delegated_service")

        (directClaims @ [ "client_id", "gateway"; "delegated_service", "gateway" ])
        |> identity true
        |> OidcPrincipalAdapter.fromIdentity mapping
        |> assertFailure OidcPrincipalFailureKind.InvalidClaim None

    [<TestMethod>]
    member _.RejectsAmbiguousClaimMappingConfiguration() =
        identity true directClaims
        |> OidcPrincipalAdapter.fromIdentity { mapping with SubjectClaim = "tid" }
        |> assertFailure OidcPrincipalFailureKind.InvalidConfiguration None

        identity true directClaims
        |> OidcPrincipalAdapter.fromIdentity
            { mapping with
                ImpersonationReasonClaim = None }
        |> assertFailure OidcPrincipalFailureKind.InvalidConfiguration None

        identity true directClaims
        |> OidcPrincipalAdapter.fromIdentity
            { mapping with
                AdditionalClaims = [ "roles" ] }
        |> assertFailure OidcPrincipalFailureKind.InvalidConfiguration None
