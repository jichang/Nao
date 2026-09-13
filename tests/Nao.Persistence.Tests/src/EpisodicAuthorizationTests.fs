namespace Nao.Persistence.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type EpisodicAuthorizationTests() =
    let scope tenant user group workspace session =
        let principal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse tenant)
                (UserId.parse user)
                (Some(ServiceId.parse "private-service"))
                [ GroupId.parse "group"; GroupId.parse "other" ]
                [ "private-role" ]
                (Map.ofList [ "private-claim", "private-value" ])
                AuthenticationStrength.MultiFactor

        AuthorizationScope.tryCreate
            principal
            (group |> Option.map GroupId.parse)
            (WorkspaceId.parse workspace)
            (session |> Option.map SessionId.parse)
        |> Option.get

    let owner = scope "tenant" "user" (Some "group") "workspace" (Some "session")

    let owners =
        [ owner
          scope "other" "user" (Some "group") "workspace" (Some "session")
          scope "tenant" "other" (Some "group") "workspace" (Some "session")
          scope "tenant" "user" (Some "other") "workspace" (Some "session")
          scope "tenant" "user" None "workspace" (Some "session")
          scope "tenant" "user" (Some "group") "other" (Some "session")
          scope "tenant" "user" (Some "group") "workspace" (Some "other")
          scope "tenant" "user" (Some "group") "workspace" None
          scope "a/b" "c" None "workspace" None
          scope "a_b" "c" None "workspace" None
          scope "a" "b/c" None "workspace" None ]

    let key scope =
        AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey

    let cutoff = DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)

    let episode id context timestamp importance : Episode =
        { Id = id
          Action = "act"
          Observation = "observed"
          Context = context
          Success = true
          Importance = importance
          Timestamp = timestamp
          Tags = [ "tag" ]
          Valence = 0.1
          LinkedEpisodes = [] }

    let provider = { EmbedAsync = fun _ -> Task.FromResult [| 1.0; 0.0 |] }

    let withStorage action =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-episode-scope-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        let factory =
            DbConnectionFactory.ofFunc (fun () ->
                new SqliteConnection(sprintf "Data Source=%s;Pooling=False" (Path.Combine(root, "episodes.db")))
                :> Data.Common.DbConnection)

        try
            action root factory
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.AllQueriesLinksSynthesisAndDeletionRemainWithinCompleteScope() =
        withStorage (fun root factory ->
            let memory = InMemoryEpisodicMemory.create (Some provider)

            let backends =
                [ (fun () -> memory)
                  (fun () -> EpisodicMemories.file root (Some provider))
                  (fun () -> EpisodicMemories.ado factory (Some provider)) ]

            for make in backends do
                for index, scope in List.indexed owners do
                    for id in [ "a"; "b"; "c" ] do
                        (make ()).RecordAsync scope (episode id (string index) cutoff 0.8)
                        |> _.GetAwaiter().GetResult()

                for index, scope in List.indexed owners do
                    let current = make ()

                    for query in
                        [ EpisodeQuery.Recent 10
                          EpisodeQuery.ByTags [ "tag" ]
                          EpisodeQuery.ByTimeRange(cutoff, cutoff)
                          EpisodeQuery.ByOutcome(true, 10)
                          EpisodeQuery.BySimilarity("query", 10) ] do
                        let results = current.QueryAsync scope query |> _.Result
                        Assert.AreEqual(3, results.Length)
                        Assert.IsTrue(results |> List.forall (fun item -> item.Context = string index))

                    Assert.AreEqual(
                        1,
                        (current.QueryAsync scope (EpisodeQuery.BySimilarity("query", 1))).Result.Length
                    )

                    Assert.IsTrue((current.QueryAsync scope (EpisodeQuery.ByOutcome(false, 10))).Result.IsEmpty)

                    for fromId, toId in [ "a", "b"; "a", "c"; "b", "c"; "c", "a" ] do
                        current.LinkAsync scope fromId toId |> _.GetAwaiter().GetResult()

                    let chain = (make ()).GetChainAsync scope "a" |> _.Result
                    Assert.AreEqual([ "a"; "b"; "c" ], chain |> List.map _.Id |> List.sort)
                    Assert.AreEqual(3, ((make ()).QueryAsync scope (EpisodeQuery.Related("a", 1))).Result.Length)
                    Assert.AreEqual(1, (current.QueryAsync scope (EpisodeQuery.Related("a", 0))).Result.Length)
                    Assert.AreEqual(3, (current.SynthesizeAsync scope "query").Result.Length)

                    current.RecordAsync
                        scope
                        { episode "a" "changed" cutoff 0.8 with
                            Success = false }
                    |> _.GetAwaiter().GetResult()

                    Assert.AreEqual(1, ((make ()).QueryAsync scope (EpisodeQuery.ByOutcome(false, 10))).Result.Length)

                    Assert.IsTrue(
                        (current.SynthesizeAsync scope "query").Result
                        |> List.exists (fun text -> text.StartsWith "Avoid:")
                    )

                    current.RecordAsync scope (episode "expired" "expired" (cutoff.AddTicks(-1)) 0.8)
                    |> _.GetAwaiter().GetResult()

                    Assert.AreEqual(Ok 1, current.DeleteExpiredAsync scope cutoff |> _.Result)

                    current.RecordAsync scope (episode "low" "low" cutoff 0.2)
                    |> _.GetAwaiter().GetResult()

                    Assert.AreEqual(1, current.ForgetBelowAsync scope 0.8 |> _.Result)
                    Assert.AreEqual(3, ((make ()).QueryAsync scope (EpisodeQuery.Recent 10)).Result.Length)
                    Assert.AreEqual(Ok 3, current.DeleteOwnerAsync scope |> _.Result)
                    Assert.AreEqual(Ok 0, (make ()).DeleteOwnerAsync scope |> _.Result)
                    Assert.IsTrue(((make ()).GetChainAsync scope "a").Result.IsEmpty)

                    for pending in List.skip (index + 1) owners do
                        Assert.AreEqual(3, ((make ()).QueryAsync pending (EpisodeQuery.Recent 10)).Result.Length))

    [<TestMethod>]
    member _.PersistedEmbeddingsReplayWithoutProviderCallsOrAuthenticationEvidence() =
        withStorage (fun root factory ->
            let calls = ResizeArray<string>()

            let embeddingProvider =
                { EmbedAsync =
                    fun text ->
                        calls.Add text
                        Task.FromResult [| 1.0; 0.0 |] }

            let samePrincipal =
                SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") [ GroupId.parse "group" ]

            let sameOwner =
                AuthorizationScope.tryCreate
                    samePrincipal
                    (Some(GroupId.parse "group"))
                    (WorkspaceId.parse "workspace")
                    (Some(SessionId.parse "session"))
                |> Option.get

            for make in
                [ (fun () -> EpisodicMemories.file root (Some embeddingProvider))
                  (fun () -> EpisodicMemories.ado factory (Some embeddingProvider)) ] do
                let count = calls.Count

                (make ()).RecordAsync owner (episode "same" "context" cutoff 0.8)
                |> _.GetAwaiter().GetResult()

                Assert.AreEqual(count + 1, calls.Count)
                Assert.AreEqual(1, ((make ()).QueryAsync sameOwner (EpisodeQuery.Recent 10)).Result.Length)
                Assert.AreEqual(count + 1, calls.Count)
                Assert.AreEqual(1, ((make ()).QueryAsync owner (EpisodeQuery.BySimilarity("query", 10))).Result.Length)
                Assert.AreEqual(count + 2, calls.Count)

            let lines =
                File.ReadAllLines(Path.Combine(root, "episodic", key owner + ".jsonl"))
                |> Array.toList

            let rows =
                Ado.query factory "SELECT stream, payload FROM nao_events" [] (fun row ->
                    Ado.getString row "stream", Ado.getString row "payload")
                |> _.Result

            Assert.AreEqual([ "episodic/" + key owner ], rows |> List.map fst)

            for payload in lines @ (rows |> List.map snd) do
                let document = FSharpJson.deserialize<EpisodicDocument> payload
                Assert.AreEqual(1, document.Version)

                match document.Event with
                | EpisodicEvent.Record(_, vector) -> CollectionAssert.AreEqual([| 1.0; 0.0 |], vector)
                | _ -> Assert.Fail "Expected a record event."

                for secret in
                    [ "private-service"
                      "private-role"
                      "private-claim"
                      "private-value"
                      "MultiFactor"
                      "Owner" ] do
                    Assert.IsFalse(payload.Contains secret)

            Assert.IsTrue(
                calls
                |> Seq.forall (fun text -> text = "act observed context" || text = "query")
            ))

    [<TestMethod>]
    member _.ProviderAndAppendFailuresDoNotPublishPartialEpisodes() =
        let mutable providerFails = false

        let embeddingProvider =
            { EmbedAsync =
                fun _ ->
                    if providerFails then
                        Task.FromException<float array>(InvalidOperationException "provider failed")
                    else
                        Task.FromResult [| 1.0 |] }

        let lines = ResizeArray<string>()
        let mutable appendFails = false

        let store =
            { Append =
                fun line ->
                    if appendFails then
                        raise (IOException "write failed")
                    else
                        lines.Add line
              LoadAll = fun () -> List.ofSeq lines }

        let persistent =
            PersistentEpisodicMemory.create (fun _ -> store) (Some embeddingProvider)

        for memory in [ InMemoryEpisodicMemory.create (Some embeddingProvider); persistent ] do
            providerFails <- false

            memory.RecordAsync owner (episode "original" "original" cutoff 0.8)
            |> _.GetAwaiter().GetResult()

            providerFails <- true

            Assert.ThrowsExactlyAsync<InvalidOperationException>(fun () ->
                memory.RecordAsync owner (episode "original" "changed" cutoff 0.8) :> Task)
            |> _.GetAwaiter().GetResult()
            |> ignore

            Assert.AreEqual(
                [ "original" ],
                (memory.QueryAsync owner (EpisodeQuery.Recent 10)).Result |> List.map _.Context
            )

        providerFails <- false
        appendFails <- true

        Assert.ThrowsExactlyAsync<IOException>(fun () ->
            persistent.RecordAsync owner (episode "added" "added" cutoff 0.8) :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        Assert.IsTrue(persistent.DeleteOwnerAsync owner |> _.Result |> Result.isError)

        Assert.ThrowsExactlyAsync<IOException>(fun () -> persistent.ForgetBelowAsync owner 1.0 :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        for memory in
            [ persistent
              PersistentEpisodicMemory.create (fun _ -> store) (Some embeddingProvider) ] do
            Assert.AreEqual([ "original" ], (memory.QueryAsync owner (EpisodeQuery.Recent 10)).Result |> List.map _.Id)

        Assert.AreEqual(1, lines.Count)

    [<TestMethod>]
    member _.CorruptForeignStreamDoesNotBlockOwnOperations() =
        withStorage (fun root factory ->
            let other = owners.[1]

            for make, corrupt in
                [ (fun () -> EpisodicMemories.file root None),
                  (fun () -> File.AppendAllText(Path.Combine(root, "episodic", key other + ".jsonl"), "{invalid\n"))
                  (fun () -> EpisodicMemories.ado factory None),
                  (fun () ->
                      Ado.executeNonQuery
                          factory
                          "UPDATE nao_events SET payload = 'invalid' WHERE stream = @stream"
                          [ "@stream", box ("episodic/" + key other) ]
                      |> _.GetAwaiter().GetResult()
                      |> ignore) ] do
                let memory = make ()

                memory.RecordAsync other (episode "foreign-private" "foreign" cutoff 0.8)
                |> _.GetAwaiter().GetResult()

                corrupt ()

                memory.RecordAsync owner (episode "own" "own" cutoff 0.8)
                |> _.GetAwaiter().GetResult()

                Assert.AreEqual(
                    1,
                    ((make ()).QueryAsync owner (EpisodeQuery.BySimilarity("act observed own", 10))).Result.Length
                )

                Assert.AreEqual(Ok 1, memory.DeleteOwnerAsync owner |> _.Result)

                Assert.ThrowsExactlyAsync<InvalidDataException>(fun () ->
                    memory.QueryAsync other (EpisodeQuery.Recent 10) :> Task)
                |> _.GetAwaiter().GetResult()
                |> ignore

                Assert.IsTrue(memory.DeleteOwnerAsync other |> _.Result |> Result.isError))

    [<TestMethod>]
    member _.UnsupportedEventRejectsBeforeEmbeddingOrAppend() =
        let mutable invoked = false
        let mutable appended = false

        let document: EpisodicDocument =
            { Version = 99
              Event = EpisodicEvent.DeleteOwner }

        let store =
            { Append = fun _ -> appended <- true
              LoadAll = fun () -> [ FSharpJson.serialize document ] }

        let provider =
            { EmbedAsync =
                fun _ ->
                    invoked <- true
                    Task.FromResult [| 1.0 |] }

        let memory = PersistentEpisodicMemory.create (fun _ -> store) (Some provider)

        Assert.ThrowsExactlyAsync<InvalidDataException>(fun () ->
            memory.RecordAsync owner (episode "new" "new" cutoff 0.8) :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        Assert.ThrowsExactlyAsync<InvalidDataException>(fun () ->
            memory.QueryAsync owner (EpisodeQuery.BySimilarity("query", 1)) :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        Assert.IsFalse invoked
        Assert.IsFalse appended
