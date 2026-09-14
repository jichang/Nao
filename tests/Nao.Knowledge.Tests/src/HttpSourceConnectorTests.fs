namespace Nao.Knowledge.Tests

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type HttpSourceConnectorTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let now = DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)

    let source uri =
        KnowledgeSource.create
            (KnowledgeSourceId.parse "web-doc")
            uri
            (AuthorizationScope.identity authorization)
            DataClassification.Internal
            (TrustAssessment.create KnowledgeTrust.Unverified "external web source" now)
            Map.empty
            now
            (KnowledgeLifecycle.active None false)

    let response status (content: byte array) =
        { StatusCode = status
          Location = None
          MediaType = Some "text/html; charset=utf-8"
          ContentLength = Some(int64 content.Length)
          ETag = Some "\"v1\""
          LastModified = Some now
          Content = content }

    let request source checkpoint network =
        { Authorization = authorization
          Source = source
          Mode = SourceConnectorMode.Pull
          Checkpoint = checkpoint
          Policy =
            { AuthorizeAsync = fun _ _ _ -> Task.FromResult true
              Network = network }
          RequestedAt = now }

    let config transport robots =
        { UserAgent = "NaoBot/1.0"
          MaximumResponseBytes = 1024L
          MaximumRedirects = 2
          AllowedMediaTypes = Set.ofList [ "text/html"; "text/plain" ]
          Transport = transport
          Robots = robots }

    let synchronize connector request =
        connector.SynchronizeAsync request CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Conditional checkpoint turns not modified into no changes``() =
        let uri = Uri "https://docs.example.test/page"
        let requests = ResizeArray<HttpSourceRequest>()
        let mutable call = 0

        let transport =
            { SendAsync =
                fun request _ ->
                    requests.Add request
                    call <- call + 1

                    if call = 1 then
                        Task.FromResult(response 200 (Encoding.UTF8.GetBytes "first"))
                    else
                        Task.FromResult(
                            { response 304 Array.empty with
                                ContentLength = None
                                MediaType = None }
                        ) }

        let connector =
            HttpSourceConnector.create (config transport { IsAllowedAsync = fun _ _ _ _ -> Task.FromResult true })

        let network = SourceNetworkPolicy.Allowlist [ Uri "https://docs.example.test/" ]
        let initial = synchronize connector (request (source uri) None network)

        let unchanged =
            synchronize connector (request (source uri) (Some initial.Checkpoint) network)

        Assert.AreEqual(1, initial.Changes.Length)
        Assert.AreEqual(0, unchanged.Changes.Length)
        Assert.AreEqual(Some "\"v1\"", requests.[1].IfNoneMatch)
        Assert.AreEqual(Some now, requests.[1].IfModifiedSince)

    [<TestMethod>]
    member _.``Redirect destination is independently allowlisted authorized and checked by robots``() =
        let start = Uri "https://start.example.test/page"
        let destination = Uri "https://cdn.example.test/content"
        let fetched = ResizeArray<Uri>()
        let robots = ResizeArray<Uri>()
        let authorized = ResizeArray<string>()

        let transport =
            { SendAsync =
                fun request _ ->
                    fetched.Add request.Uri

                    if request.Uri = start then
                        Task.FromResult(
                            { response 302 Array.empty with
                                Location = Some destination
                                MediaType = None
                                ContentLength = None }
                        )
                    else
                        Task.FromResult(response 200 (Encoding.UTF8.GetBytes "content")) }

        let connector =
            HttpSourceConnector.create (
                config
                    transport
                    { IsAllowedAsync =
                        fun uri _ _ _ ->
                            robots.Add uri
                            Task.FromResult true }
            )

        let policy =
            { AuthorizeAsync =
                fun _ access _ ->
                    authorized.Add(ResourceAccess.resource access)
                    Task.FromResult true
              Network =
                SourceNetworkPolicy.Allowlist [ Uri "https://start.example.test/"; Uri "https://cdn.example.test/" ] }

        let batch =
            synchronize
                connector
                { request (source start) None policy.Network with
                    Policy = policy }

        Assert.AreEqual(
            destination,
            batch.Changes
            |> List.exactlyOne
            |> function
                | SourceChange.Added item -> item.Uri
                | _ -> null
        )

        CollectionAssert.AreEqual([| start; destination |], fetched.ToArray())
        CollectionAssert.AreEqual([| start; destination |], robots.ToArray())
        Assert.IsTrue(authorized.Contains start.AbsoluteUri)
        Assert.IsTrue(authorized.Contains destination.AbsoluteUri)

    [<TestMethod>]
    member _.``Redirect to a non-allowlisted origin fails before destination transport``() =
        let start = Uri "https://start.example.test/page"
        let destination = Uri "https://blocked.example.test/content"
        let fetched = ResizeArray<Uri>()

        let transport =
            { SendAsync =
                fun request _ ->
                    fetched.Add request.Uri

                    Task.FromResult(
                        { response 302 Array.empty with
                            Location = Some destination
                            MediaType = None
                            ContentLength = None }
                    ) }

        let connector =
            HttpSourceConnector.create (config transport { IsAllowedAsync = fun _ _ _ _ -> Task.FromResult true })

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            synchronize
                connector
                (request (source start) None (SourceNetworkPolicy.Allowlist [ Uri "https://start.example.test/" ]))
            |> ignore)
        |> ignore

        CollectionAssert.AreEqual([| start |], fetched.ToArray())

    [<TestMethod>]
    member _.``Robots media type and content limits fail closed``() =
        let uri = Uri "https://docs.example.test/page"
        let mutable transportCalls = 0

        let transport response =
            { SendAsync =
                fun _ _ ->
                    transportCalls <- transportCalls + 1
                    Task.FromResult response }

        let deniedByRobots =
            HttpSourceConnector.create (
                config
                    (transport (response 200 (Encoding.UTF8.GetBytes "content")))
                    { IsAllowedAsync = fun _ _ _ _ -> Task.FromResult false }
            )

        let allowedNetwork =
            SourceNetworkPolicy.Allowlist [ Uri "https://docs.example.test/" ]

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            synchronize deniedByRobots (request (source uri) None allowedNetwork) |> ignore)
        |> ignore

        Assert.AreEqual(0, transportCalls)

        let wrongMedia =
            HttpSourceConnector.create (
                config
                    (transport
                        { response 200 (Encoding.UTF8.GetBytes "content") with
                            MediaType = Some "application/octet-stream" })
                    { IsAllowedAsync = fun _ _ _ _ -> Task.FromResult true }
            )

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            synchronize wrongMedia (request (source uri) None allowedNetwork) |> ignore)
        |> ignore

        let oversized =
            HttpSourceConnector.create
                { config
                      (transport
                          { response 200 (Array.zeroCreate 1025) with
                              ContentLength = Some 1025L })
                      { IsAllowedAsync = fun _ _ _ _ -> Task.FromResult true } with
                    MaximumResponseBytes = 1024L }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            synchronize oversized (request (source uri) None allowedNetwork) |> ignore)
        |> ignore
