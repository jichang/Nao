namespace Nao.Persistence.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type TieredAuthorizationTests() =
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

    let config =
        { TieredMemoryConfig.Default with
            ShortTermCapacity = 2
            MidTermCapacity = 3
            PromotionPolicy = MemoryPromotionPolicy.AccessThreshold 1
            MidTermTtl = Some(TimeSpan.FromHours 1) }

    let entry key value tier timestamp : TieredMemoryEntry =
        { Key = key
          Value = value
          Tier = tier
          Timestamp = timestamp
          AccessCount = 0
          Relevance = 0.5
          Tags = [] }

    let withStorage action =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-tiered-scope-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        let factory =
            DbConnectionFactory.ofFunc (fun () ->
                new SqliteConnection(sprintf "Data Source=%s;Pooling=False" (Path.Combine(root, "tiered.db")))
                :> Data.Common.DbConnection)

        try
            action root factory
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.RetrievalPromotionCapacityAndRetentionAreScopeBoundAcrossBackends() =
        withStorage (fun root factory ->
            let memory = InMemoryTieredMemory.create config None

            for make in
                [ (fun () -> memory)
                  (fun () -> TieredMemories.file root config None)
                  (fun () -> TieredMemories.ado factory config None) ] do
                for index, scope in List.indexed owners do
                    for id in [ "a"; "b"; "c" ] do
                        (make ()).StoreAsync scope (entry id (string index) MemoryTier.ShortTerm cutoff)
                        |> _.GetAwaiter().GetResult()

                for index, scope in List.indexed owners do
                    let current = make ()
                    let stored = current.RetrieveAsync scope (string index) 10 |> _.Result
                    Assert.AreEqual([ "b"; "c" ], stored |> List.map _.Key |> List.sort)

                    Assert.IsTrue(
                        stored
                        |> List.forall (fun item -> item.Value = string index && item.AccessCount = 0)
                    )

                    Assert.AreEqual(1, (current.RetrieveAsync scope (string index) 1).Result.Length)
                    Assert.AreEqual(2, ((make ()).RetrieveFromTierAsync scope MemoryTier.ShortTerm 10).Result.Length)

                    current.RecordAccessAsync scope [ "b"; "b" ] cutoff
                    |> _.GetAwaiter().GetResult()

                    let promoted =
                        (make ()).RetrieveFromTierAsync scope MemoryTier.MidTerm 10 |> _.Result

                    Assert.AreEqual([ "b" ], promoted |> List.map _.Key)
                    Assert.AreEqual(1, promoted.Head.AccessCount)
                    current.PromoteAsync scope "c" MemoryTier.LongTerm |> _.GetAwaiter().GetResult()

                    Assert.AreEqual(
                        [ "c" ],
                        ((make ()).RetrieveFromTierAsync scope MemoryTier.LongTerm 10).Result
                        |> List.map _.Key
                    )

                    current.StoreAsync scope (entry "old" "old" MemoryTier.MidTerm (cutoff.AddHours(-1).AddTicks(-1)))
                    |> _.GetAwaiter().GetResult()

                    current.StoreAsync scope (entry "boundary" "boundary" MemoryTier.MidTerm (cutoff.AddHours(-1)))
                    |> _.GetAwaiter().GetResult()

                    Assert.AreEqual(1, current.EvictAsync scope cutoff |> _.Result)
                    Assert.AreEqual(2, ((make ()).RetrieveFromTierAsync scope MemoryTier.MidTerm 10).Result.Length)
                    Assert.AreEqual(Ok 1, current.DeleteExpiredAsync scope cutoff |> _.Result)
                    Assert.AreEqual(Ok 2, current.DeleteOwnerAsync scope |> _.Result)
                    Assert.AreEqual(Ok 0, (make ()).DeleteOwnerAsync scope |> _.Result)
                    Assert.IsTrue(((make ()).RetrieveAsync scope "anything" 10).Result.IsEmpty)

                    for pending in List.skip (index + 1) owners do
                        let retained =
                            (make ()).RetrieveFromTierAsync pending MemoryTier.ShortTerm 10 |> _.Result

                        Assert.AreEqual(2, retained.Length)
                        Assert.IsTrue(retained |> List.forall (fun item -> item.AccessCount = 0)))

    [<TestMethod>]
    member _.EmbeddingRetrievalIsPureAndNeverSeesForeignContentOrAuthenticationEvidence() =
        withStorage (fun root factory ->
            let calls = System.Collections.Concurrent.ConcurrentBag<string>()

            let provider =
                { EmbedAsync =
                    fun text ->
                        calls.Add text
                        Task.FromResult [| 1.0 |] }

            let principal =
                SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") [ GroupId.parse "group" ]

            let sameOwner =
                AuthorizationScope.tryCreate
                    principal
                    (Some(GroupId.parse "group"))
                    (WorkspaceId.parse "workspace")
                    (Some(SessionId.parse "session"))
                |> Option.get

            for make in
                [ (fun () -> TieredMemories.file root config (Some provider))
                  (fun () -> TieredMemories.ado factory config (Some provider)) ] do
                let before = calls.Count
                let memory = make ()

                memory.StoreAsync owner (entry "same" "own-text" MemoryTier.LongTerm cutoff)
                |> _.GetAwaiter().GetResult()

                memory.StoreAsync owners.[1] (entry "same" "foreign-text" MemoryTier.LongTerm cutoff)
                |> _.GetAwaiter().GetResult()

                Assert.AreEqual(before, calls.Count)
                let ranked = (make ()).RetrieveAsync sameOwner "query" 1 |> _.Result
                Assert.AreEqual([ "own-text" ], ranked |> List.map _.Value)
                Assert.AreEqual(0, ranked.Head.AccessCount)
                Assert.AreEqual(before + 2, calls.Count)
                Assert.AreEqual(ranked, ((make ()).RetrieveFromTierAsync owner MemoryTier.LongTerm 10).Result)
                Assert.AreEqual(before + 2, calls.Count)

            Assert.IsTrue(calls |> Seq.forall (fun text -> text = "own-text" || text = "query"))
            let lines = File.ReadAllLines(Path.Combine(root, "tiered", key owner + ".jsonl"))
            Assert.AreEqual(1, lines.Length)

            let rows =
                Ado.query
                    factory
                    "SELECT stream, payload FROM nao_events WHERE stream = @stream"
                    [ "@stream", box ("tiered/" + key owner) ]
                    (fun row -> Ado.getString row "payload")
                |> _.Result

            Assert.AreEqual(1, rows.Length)

            for payload in List.ofArray lines @ rows do
                let document = FSharpJson.deserialize<TieredDocument> payload
                Assert.AreEqual(1, document.Version)
                Assert.AreEqual(config, document.Policy)

                for secret in
                    [ "private-service"
                      "private-role"
                      "private-claim"
                      "private-value"
                      "MultiFactor"
                      "Owner" ] do
                    Assert.IsFalse(payload.Contains secret))

    [<TestMethod>]
    member _.FailedAppendCannotPublishSpeculativePromotionEvictionOrDeletion() =
        let lines = ResizeArray<string>()
        let mutable failing = false

        let store =
            { Append =
                fun line ->
                    if failing then
                        raise (IOException "write failed")
                    else
                        lines.Add line
              LoadAll = fun () -> List.ofSeq lines }

        let make () =
            PersistentTieredMemory.create (fun _ -> store) config None

        let memory = make ()

        memory.StoreAsync owner (entry "original" "original" MemoryTier.MidTerm cutoff)
        |> _.GetAwaiter().GetResult()

        failing <- true

        let operations =
            [ (fun () -> memory.StoreAsync owner (entry "new" "new" MemoryTier.LongTerm cutoff) :> Task)
              (fun () -> memory.RecordAccessAsync owner [ "original" ] cutoff :> Task)
              (fun () -> memory.PromoteAsync owner "original" MemoryTier.LongTerm :> Task)
              (fun () -> memory.EvictAsync owner (cutoff.AddHours 2) :> Task) ]

        for operation in operations do
            Assert.ThrowsExactlyAsync<IOException>(operation)
            |> _.GetAwaiter().GetResult()
            |> ignore

        Assert.IsTrue(memory.DeleteOwnerAsync owner |> _.Result |> Result.isError)

        Assert.IsTrue(
            memory.DeleteExpiredAsync owner (cutoff.AddTicks 1)
            |> _.Result
            |> Result.isError
        )

        for current in [ memory; make () ] do
            let retained = current.RetrieveAsync owner "original" 10 |> _.Result
            Assert.AreEqual([ entry "original" "original" MemoryTier.MidTerm cutoff ], retained)

        Assert.AreEqual(1, lines.Count)

    [<TestMethod>]
    member _.ForeignCorruptionDoesNotBlockOwnReadsOrWrites() =
        withStorage (fun root factory ->
            let other = owners.[1]

            for make, corrupt in
                [ (fun () -> TieredMemories.file root config None),
                  (fun () -> File.AppendAllText(Path.Combine(root, "tiered", key other + ".jsonl"), "{invalid\n"))
                  (fun () -> TieredMemories.ado factory config None),
                  (fun () ->
                      Ado.executeNonQuery
                          factory
                          "UPDATE nao_events SET payload = 'invalid' WHERE stream = @stream"
                          [ "@stream", box ("tiered/" + key other) ]
                      |> _.GetAwaiter().GetResult()
                      |> ignore) ] do
                let memory = make ()

                memory.StoreAsync other (entry "private" "foreign" MemoryTier.LongTerm cutoff)
                |> _.GetAwaiter().GetResult()

                corrupt ()

                memory.StoreAsync owner (entry "own" "own" MemoryTier.LongTerm cutoff)
                |> _.GetAwaiter().GetResult()

                Assert.AreEqual(1, ((make ()).RetrieveAsync owner "own" 10).Result.Length)
                Assert.AreEqual(Ok 1, memory.DeleteOwnerAsync owner |> _.Result)

                Assert.ThrowsExactlyAsync<InvalidDataException>(fun () ->
                    memory.RetrieveAsync other "foreign" 10 :> Task)
                |> _.GetAwaiter().GetResult()
                |> ignore

                Assert.IsTrue(memory.DeleteOwnerAsync other |> _.Result |> Result.isError))

    [<TestMethod>]
    member _.ManualAndRecencyPoliciesPreserveExplicitEvictionWithAutoEvictionDisabled() =
        withStorage (fun root factory ->
            for index, policy in
                [ 0, MemoryPromotionPolicy.Manual
                  1, MemoryPromotionPolicy.RecencyBased(TimeSpan.FromMinutes 5.0) ] do
                let policyConfig =
                    { config with
                        ShortTermCapacity = 1
                        MidTermCapacity = 1
                        AutoEvict = false
                        PromotionPolicy = policy }

                let owner = scope "tenant" "user" None "workspace" (Some(string index))
                let inMemory = InMemoryTieredMemory.create policyConfig None

                for make in
                    [ (fun () -> inMemory)
                      (fun () -> TieredMemories.file root policyConfig None)
                      (fun () -> TieredMemories.ado factory policyConfig None) ] do
                    let memory = make ()

                    memory.StoreAsync owner (entry "old" "old" MemoryTier.ShortTerm (cutoff.AddHours(-1)))
                    |> _.GetAwaiter().GetResult()

                    memory.StoreAsync owner (entry "recent" "recent" MemoryTier.ShortTerm cutoff)
                    |> _.GetAwaiter().GetResult()

                    Assert.AreEqual(2, ((make ()).RetrieveFromTierAsync owner MemoryTier.ShortTerm 10).Result.Length)
                    memory.RecordAccessAsync owner [ "recent" ] cutoff |> _.GetAwaiter().GetResult()
                    memory.PromoteAsync owner "old" MemoryTier.MidTerm |> _.GetAwaiter().GetResult()

                    Assert.AreEqual(
                        index + 1,
                        ((make ()).RetrieveFromTierAsync owner MemoryTier.MidTerm 10).Result.Length
                    )

                    Assert.AreEqual(index, memory.EvictAsync owner cutoff |> _.Result)
                    let recent = (make ()).RetrieveAsync owner "recent" 1 |> _.Result |> List.exactlyOne
                    Assert.AreEqual(1, recent.AccessCount)
                    Assert.AreEqual(cutoff, recent.Timestamp)

                    Assert.AreEqual(
                        (if index = 0 then
                             MemoryTier.ShortTerm
                         else
                             MemoryTier.MidTerm),
                        recent.Tier
                    ))

    [<TestMethod>]
    member _.ChangedPolicyAndUnsupportedVersionRejectBeforeCallbacksOrMutation() =
        let mutable appended = false
        let mutable embedded = false

        let provider =
            { EmbedAsync =
                fun _ ->
                    embedded <- true
                    Task.FromResult [| 1.0 |] }

        for version, policy in [ 99, config; 1, { config with ShortTermCapacity = 1 } ] do
            let document: TieredDocument =
                { Version = version
                  Policy = policy
                  Event = TieredEvent.Store(entry "a" "a" MemoryTier.ShortTerm cutoff) }

            let store =
                { Append = fun _ -> appended <- true
                  LoadAll = fun () -> [ FSharpJson.serialize document ] }

            let memory = PersistentTieredMemory.create (fun _ -> store) config (Some provider)

            Assert.ThrowsExactlyAsync<InvalidDataException>(fun () -> memory.RetrieveAsync owner "query" 10 :> Task)
            |> _.GetAwaiter().GetResult()
            |> ignore

            Assert.ThrowsExactlyAsync<InvalidDataException>(fun () ->
                memory.RecordAccessAsync owner [ "a" ] cutoff :> Task)
            |> _.GetAwaiter().GetResult()
            |> ignore

        Assert.IsFalse appended
        Assert.IsFalse embedded
