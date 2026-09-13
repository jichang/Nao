namespace Nao.Persistence.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type WorkingAuthorizationTests() =
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

    let execution =
        ExecutionId.ofGuid (Guid.Parse "11111111-1111-1111-1111-111111111111")

    let address authorization =
        WorkingMemoryAddress.create authorization execution

    let ownerAddress = address owner

    let key authorization =
        AuthorizationScope.identity authorization |> AuthorizationIdentity.storageKey

    let cutoff = DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)

    let config =
        { WorkingMemoryConfig.Default with
            Capacity = 2
            DefaultTtl = TimeSpan.FromHours 1.0
            DecayRate = 0.5
            EvictionThreshold = 0.2 }

    let item key content attention addedAt expiresAt pinned : WorkingMemoryItem =
        { Key = key
          Content = content
          Attention = attention
          Source = "test"
          AddedAt = addedAt
          ExpiresAt = expiresAt
          Pinned = pinned }

    let withStorage action =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-working-scope-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        let factory =
            DbConnectionFactory.ofFunc (fun () ->
                new SqliteConnection(sprintf "Data Source=%s;Pooling=False" (Path.Combine(root, "working.db")))
                :> Data.Common.DbConnection)

        try
            action root factory
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.CompleteAuthorizationAndExecutionIdentityPartitionEveryOperation() =
        withStorage (fun root factory ->
            let memory = InMemoryWorkingMemory.create config

            let alternateExecution =
                ExecutionId.ofGuid (Guid.Parse "22222222-2222-2222-2222-222222222222")

            let addresses =
                (owners |> List.map address)
                @ [ WorkingMemoryAddress.create owner alternateExecution ]

            let backends =
                [ (fun () -> memory)
                  (fun () -> WorkingMemories.file root config)
                  (fun () -> WorkingMemories.ado factory config) ]

            for make in backends do
                for index, address in List.indexed addresses do
                    let current = make ()

                    current.SetAsync address (item "low" (string index) 0.2 cutoff None false)
                    |> _.GetAwaiter().GetResult()

                    current.SetAsync address (item "high" (string index) 0.8 cutoff None false)
                    |> _.GetAwaiter().GetResult()

                for index, address in List.indexed addresses do
                    let current = make ()
                    let all = current.GetAllAsync address |> _.Result
                    Assert.AreEqual([ "high"; "low" ], all |> List.map _.Key)
                    Assert.IsTrue(all |> List.forall (fun entry -> entry.Content = string index))
                    Assert.IsTrue(all |> List.forall (fun entry -> entry.ExpiresAt = Some(cutoff.AddHours 1)))
                    Assert.AreEqual([ "high" ], current.GetActiveAsync address 0.5 |> _.Result |> List.map _.Key)
                    Assert.AreEqual(Some 0.8, current.GetAsync address "high" |> _.Result |> Option.map _.Attention)
                    Assert.AreEqual(Some 0.8, current.GetAsync address "high" |> _.Result |> Option.map _.Attention)
                    current.FocusAsync address "high" 0.5 |> _.GetAwaiter().GetResult()
                    Assert.AreEqual(Some 1.0, (make ()).GetAsync address "high" |> _.Result |> Option.map _.Attention)
                    current.PinAsync address "low" |> _.GetAwaiter().GetResult()
                    let pinned = (make ()).GetAsync address "low" |> _.Result |> Option.get
                    Assert.IsTrue pinned.Pinned
                    Assert.AreEqual(None, pinned.ExpiresAt)
                    current.UnpinAsync address "low" cutoff |> _.GetAwaiter().GetResult()

                    Assert.AreEqual(
                        Some(cutoff.AddHours 1),
                        (make ()).GetAsync address "low" |> _.Result |> Option.bind _.ExpiresAt
                    )

                    Assert.AreEqual(1, current.DecayAsync address cutoff |> _.Result)
                    Assert.AreEqual(Some 0.5, (make ()).GetAsync address "high" |> _.Result |> Option.map _.Attention)
                    StringAssert.Contains(current.RenderContextAsync address 1 |> _.Result, string index)
                    current.RemoveAsync address "low" |> _.GetAwaiter().GetResult()
                    Assert.AreEqual([ "high" ], (make ()).GetAllAsync address |> _.Result |> List.map _.Key)

                    current.SetAsync address (item "expired" "expired" 0.9 cutoff (Some(cutoff.AddTicks(-1))) false)
                    |> _.GetAwaiter().GetResult()

                    Assert.AreEqual(Ok 1, current.DeleteExpiredAsync address cutoff |> _.Result)
                    Assert.AreEqual(Ok 1, current.DeleteOwnerAsync address |> _.Result)
                    Assert.AreEqual(Ok 0, (make ()).DeleteOwnerAsync address |> _.Result)

                    for pending in List.skip (index + 1) addresses do
                        Assert.AreEqual(2, ((make ()).GetAllAsync pending).Result.Length))

    [<TestMethod>]
    member _.CapacityPinnedExpiryAndDecayBoundariesRemainDeterministic() =
        let memory = InMemoryWorkingMemory.create config

        memory.SetAsync ownerAddress (item "pinned" "pinned" 0.1 cutoff (Some(cutoff.AddTicks(-1))) true)
        |> _.GetAwaiter().GetResult()

        for key, attention in [ "old-low", 0.2; "new-low", 0.2; "high", 0.9 ] do
            let addedAt = if key = "old-low" then cutoff.AddTicks(-1) else cutoff

            memory.SetAsync ownerAddress (item key key attention addedAt None false)
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual([ "high"; "pinned" ], memory.GetAllAsync ownerAddress |> _.Result |> List.map _.Key)
        Assert.AreEqual(Ok 0, memory.DeleteExpiredAsync ownerAddress cutoff |> _.Result)
        Assert.AreEqual(0, memory.DecayAsync ownerAddress cutoff |> _.Result)
        Assert.AreEqual(Some 0.45, memory.GetAsync ownerAddress "high" |> _.Result |> Option.map _.Attention)
        Assert.AreEqual(0, memory.DecayAsync ownerAddress cutoff |> _.Result)
        Assert.AreEqual(1, memory.DecayAsync ownerAddress cutoff |> _.Result)
        Assert.AreEqual([ "pinned" ], memory.GetAllAsync ownerAddress |> _.Result |> List.map _.Key)

    [<TestMethod>]
    member _.PersistentPayloadsExcludeAddressAndAuthenticationEvidenceAndBindPolicy() =
        withStorage (fun root factory ->
            let principal =
                SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") [ GroupId.parse "group" ]

            let sameOwner =
                AuthorizationScope.tryCreate
                    principal
                    (Some(GroupId.parse "group"))
                    (WorkspaceId.parse "workspace")
                    (Some(SessionId.parse "session"))
                |> Option.get
                |> address

            for make in
                [ (fun () -> WorkingMemories.file root config)
                  (fun () -> WorkingMemories.ado factory config) ] do
                let memory = make ()

                memory.SetAsync ownerAddress (item "key" "value" 0.8 cutoff None false)
                |> _.GetAwaiter().GetResult()

                Assert.AreEqual([ "key" ], (make ()).GetAllAsync sameOwner |> _.Result |> List.map _.Key)

            let executionKey = ExecutionId.serialize execution
            let path = Path.Combine(root, "working", key owner, executionKey + ".jsonl")
            let lines = File.ReadAllLines path |> Array.toList

            let rows =
                Ado.query factory "SELECT stream, payload FROM nao_events" [] (fun row ->
                    Ado.getString row "stream", Ado.getString row "payload")
                |> _.Result

            Assert.AreEqual([ "working/" + key owner + "/" + executionKey ], rows |> List.map fst)

            for payload in lines @ (rows |> List.map snd) do
                let document = FSharpJson.deserialize<WorkingMemoryDocument> payload
                Assert.AreEqual(1, document.Version)
                Assert.AreEqual(config, document.Policy)

                for secret in
                    [ executionKey
                      "private-service"
                      "private-role"
                      "private-claim"
                      "private-value"
                      "MultiFactor"
                      "Authorization"
                      "ExecutionId" ] do
                    Assert.IsFalse(payload.Contains secret))

    [<TestMethod>]
    member _.FailedAppendAndPolicyMismatchCannotPublishSpeculativeState() =
        let lines = ResizeArray<string>()
        let mutable failAppend = false

        let store =
            { Append =
                fun line ->
                    if failAppend then
                        raise (IOException "write failed")
                    else
                        lines.Add line
              LoadAll = fun () -> List.ofSeq lines }

        let make () =
            PersistentWorkingMemory.create (fun _ -> store) config

        let memory = make ()

        memory.SetAsync ownerAddress (item "original" "original" 0.8 cutoff None false)
        |> _.GetAwaiter().GetResult()

        failAppend <- true

        let operations =
            [ (fun () -> memory.SetAsync ownerAddress (item "new" "new" 0.9 cutoff None false) :> Task)
              (fun () -> memory.FocusAsync ownerAddress "original" 0.1 :> Task)
              (fun () -> memory.DecayAsync ownerAddress cutoff :> Task)
              (fun () -> memory.PinAsync ownerAddress "original" :> Task)
              (fun () -> memory.UnpinAsync ownerAddress "original" cutoff :> Task)
              (fun () -> memory.RemoveAsync ownerAddress "original" :> Task) ]

        for operation in operations do
            Assert.ThrowsExactlyAsync<IOException>(operation)
            |> _.GetAwaiter().GetResult()
            |> ignore

        Assert.IsTrue(memory.DeleteOwnerAsync ownerAddress |> _.Result |> Result.isError)
        Assert.IsTrue(memory.DeleteExpiredAsync ownerAddress cutoff |> _.Result |> Result.isError)

        for current in [ memory; make () ] do
            Assert.AreEqual([ "original" ], current.GetAllAsync ownerAddress |> _.Result |> List.map _.Key)
            Assert.AreEqual(Some 0.8, current.GetAsync ownerAddress "original" |> _.Result |> Option.map _.Attention)

        failAppend <- false

        let mismatched =
            PersistentWorkingMemory.create (fun _ -> store) { config with Capacity = 1 }

        Assert.ThrowsExactlyAsync<InvalidDataException>(fun () -> mismatched.GetAllAsync ownerAddress :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        let unsupported: WorkingMemoryDocument =
            { Version = 99
              Policy = config
              Event = WorkingMemoryEvent.DeleteOwner }

        let unsupportedStore =
            { Append = fun _ -> Assert.Fail("Unsupported streams must reject before append.")
              LoadAll = fun () -> [ FSharpJson.serialize unsupported ] }

        let unsupportedMemory =
            PersistentWorkingMemory.create (fun _ -> unsupportedStore) config

        Assert.ThrowsExactlyAsync<InvalidDataException>(fun () -> unsupportedMemory.GetAllAsync ownerAddress :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        Assert.AreEqual(1, lines.Count)

    [<TestMethod>]
    member _.CorruptForeignAddressDoesNotBlockOwnOperations() =
        withStorage (fun root factory ->
            let other = address owners.[1]
            let executionKey = ExecutionId.serialize execution

            for make, corrupt in
                [ (fun () -> WorkingMemories.file root config),
                  (fun () ->
                      File.AppendAllText(
                          Path.Combine(root, "working", key owners.[1], executionKey + ".jsonl"),
                          "{invalid\n"
                      ))
                  (fun () -> WorkingMemories.ado factory config),
                  (fun () ->
                      Ado.executeNonQuery
                          factory
                          "UPDATE nao_events SET payload = 'invalid' WHERE stream = @stream"
                          [ "@stream", box ("working/" + key owners.[1] + "/" + executionKey) ]
                      |> _.GetAwaiter().GetResult()
                      |> ignore) ] do
                let memory = make ()

                memory.SetAsync other (item "foreign-private" "foreign" 0.8 cutoff None false)
                |> _.GetAwaiter().GetResult()

                corrupt ()

                memory.SetAsync ownerAddress (item "own" "own" 0.8 cutoff None false)
                |> _.GetAwaiter().GetResult()

                Assert.AreEqual(1, ((make ()).GetAllAsync ownerAddress).Result.Length)
                Assert.AreEqual(Ok 1, memory.DeleteOwnerAsync ownerAddress |> _.Result)

                Assert.ThrowsExactlyAsync<InvalidDataException>(fun () -> memory.GetAllAsync other :> Task)
                |> _.GetAwaiter().GetResult()
                |> ignore

                Assert.IsTrue(memory.DeleteOwnerAsync other |> _.Result |> Result.isError))
