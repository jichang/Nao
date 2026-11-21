namespace Nao.Identity.Oidc

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type OAuthProviderDefinition =
    {
        Id: string
        Name: string
        AuthorizationEndpoint: string
        TokenEndpoint: string
        UserInformationEndpoint: string
        IdentityScopes: Set<string>
        /// Suggested scopes for discovery, not an authorization allowlist.
        ServiceScopes: Set<string>
    }

module OAuthProviders =
    let microsoft =
        { Id = "microsoft"
          Name = "Microsoft (personal, work or school)"
          AuthorizationEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize"
          TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token"
          UserInformationEndpoint = "https://graph.microsoft.com/oidc/userinfo"
          IdentityScopes = Set.ofList [ "openid"; "profile"; "email"; "offline_access" ]
          ServiceScopes = Set.ofList [ "User.Read"; "Mail.Read"; "Mail.Send"; "Files.Read"; "Sites.Read.All" ] }

    let google =
        { Id = "google"
          Name = "Google"
          AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth"
          TokenEndpoint = "https://oauth2.googleapis.com/token"
          UserInformationEndpoint = "https://openidconnect.googleapis.com/v1/userinfo"
          IdentityScopes = Set.ofList [ "openid"; "profile"; "email" ]
          ServiceScopes =
            Set.ofList
                [ "https://www.googleapis.com/auth/gmail.readonly"
                  "https://www.googleapis.com/auth/gmail.send"
                  "https://www.googleapis.com/auth/drive.readonly" ] }

    let find id =
        [ microsoft; google ]
        |> List.tryFind (fun provider -> provider.Id = id)
        |> Option.defaultWith (fun () -> invalidArg (nameof id) "Unsupported OAuth provider.")

    let normalizeScope provider (scope: string) =
        if provider = "microsoft" then
            scope.Replace("https://graph.microsoft.com/", "", StringComparison.OrdinalIgnoreCase)
        else
            scope

    let scopes provider (value: string) =
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        |> Seq.map (normalizeScope provider)
        |> Set.ofSeq

type OAuthClientRegistration =
    { Provider: string
      ClientId: string
      ClientSecret: SecretValue }

/// Uses provider-pinned HTTPS endpoints. Supply an HttpClient with redirects disabled.
type OAuthTokenRefresher(client: HttpClient, registrations: OAuthClientRegistration list) =
    interface IOAuthTokenRefresher with
        member _.RefreshAsync(providerId, previous, cancellationToken) =
            task {
                let provider = OAuthProviders.find providerId

                let registration =
                    registrations
                    |> List.tryFind (fun registration -> registration.Provider = providerId)
                    |> Option.defaultWith (fun () -> invalidOp "OAuth provider is not configured.")

                let refresh =
                    previous.RefreshToken
                    |> Option.defaultWith (fun () -> invalidOp "A refresh token is required.")

                let fields =
                    [ "grant_type", "refresh_token"
                      "client_id", registration.ClientId
                      "client_secret", SecretValue.useValue id registration.ClientSecret
                      "refresh_token", SecretValue.useValue id refresh ]
                    |> List.map (fun (key, value) -> KeyValuePair(key, value))

                use request = new HttpRequestMessage(HttpMethod.Post, provider.TokenEndpoint)
                request.Content <- new FormUrlEncodedContent(fields)
                use! response = client.SendAsync(request, cancellationToken)
                use! stream = response.Content.ReadAsStreamAsync cancellationToken
                use! document = JsonDocument.ParseAsync(stream, cancellationToken = cancellationToken)
                let root = document.RootElement

                if not response.IsSuccessStatusCode then
                    let mutable error = Unchecked.defaultof<JsonElement>

                    if
                        response.StatusCode = HttpStatusCode.BadRequest
                        && root.TryGetProperty("error", &error)
                        && error.GetString() = "invalid_grant"
                    then
                        return OAuthRefreshResult.ReauthenticationRequired
                    else
                        return
                            raise (
                                HttpRequestException("OAuth token refresh failed.", null, Nullable response.StatusCode)
                            )
                else
                    if
                        not (
                            String.Equals(
                                root.GetProperty("token_type").GetString(),
                                "Bearer",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    then
                        invalidOp "The OAuth provider returned an unsupported token type."

                    let lifetime = root.GetProperty("expires_in").GetDouble()

                    if lifetime <= 0.0 || lifetime > 31536000.0 then
                        invalidOp "Invalid OAuth token lifetime."

                    let secret (name: string) =
                        root.GetProperty(name).GetString()
                        |> SecretValue.create (SecretReference.parse $"oauth:{providerId}:{name}")

                    let mutable refreshProperty = Unchecked.defaultof<JsonElement>
                    let mutable scopeProperty = Unchecked.defaultof<JsonElement>

                    return
                        OAuthRefreshResult.Refreshed
                            { AccessToken = secret "access_token"
                              RefreshToken =
                                if root.TryGetProperty("refresh_token", &refreshProperty) then
                                    Some(secret "refresh_token")
                                else
                                    previous.RefreshToken
                              // OAuth permits omission when the granted scopes did not change.
                              Scopes =
                                if root.TryGetProperty("scope", &scopeProperty) then
                                    OAuthProviders.scopes providerId (scopeProperty.GetString())
                                else
                                    previous.Scopes
                              ExpiresAt = DateTimeOffset.UtcNow.AddSeconds lifetime }
            }
