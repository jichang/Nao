namespace Nao.Agents.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type SecretTests() =

    let scope user =
        SecurityPrincipal.create (TenantId.parse "secret-tenant") (UserId.parse user) []
        |> fun principal -> AuthorizationScope.tryCreate principal None (WorkspaceId.parse "secret-workspace") None
        |> Option.get

    [<TestMethod>]
    member _.``Secret references are canonical comparable identifiers``() =
        let first = SecretReference.parse "providers/openai/api-key"
        let second = SecretReference.parse "providers/openai/api-key"

        Assert.AreEqual(first, second)
        Assert.AreEqual("providers/openai/api-key", SecretReference.value first)

        Assert.ThrowsExactly<ArgumentException>(fun () -> SecretReference.parse " padded " |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> SecretReference.parse "" |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> SecretVersion.parse "" |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Secret values do not reveal content through string formatting``() =
        let reference = SecretReference.parse "providers/openai/api-key"
        let secret = SecretValue.create reference "canary-secret-value"

        Assert.AreEqual(reference, SecretValue.reference secret)
        Assert.AreEqual("[REDACTED]", secret.ToString())
        Assert.AreEqual("[REDACTED]", sprintf "%O" secret)
        Assert.IsFalse((sprintf "%A" secret).Contains("canary-secret-value"))
        Assert.AreEqual("canary-secret-value", SecretValue.useValue id secret)

        Assert.ThrowsExactly<ArgumentException>(fun () -> SecretValue.create reference "" |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``In-memory provider isolates owners and records provider provenance``() =
        let ownerScope = scope "owner"
        let otherScope = scope "other"
        let reference = SecretReference.parse "providers/openai/api-key"
        let value = SecretValue.create reference "secret"

        let store =
            SecretProvider.inMemory "test-memory" "1" [ AuthorizationScope.identity ownerScope, value ]

        let resolution =
            SecretProvider.resolveAsync ownerScope reference CancellationToken.None (SecretManager.provider store)
            |> _.Result

        let evidence = SecretResolution.evidence resolution
        Assert.AreEqual(reference, SecretValue.reference (SecretResolution.value resolution))
        Assert.AreEqual("test-memory", evidence.ProviderId)
        Assert.AreEqual("1", evidence.ProviderVersion)
        Assert.AreEqual(reference, evidence.Reference)
        Assert.AreEqual(SecretVersion.initial, evidence.SecretVersion)

        Assert.ThrowsExactly<KeyNotFoundException>(fun () ->
            SecretProvider.resolveAsync otherScope reference CancellationToken.None (SecretManager.provider store)
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            SecretProvider.inMemory
                "test-memory"
                "1"
                [ AuthorizationScope.identity ownerScope, value
                  AuthorizationScope.identity ownerScope, value ]
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``In-memory provider rotates revokes and restores one owner without stale updates``() =
        let ownerScope = scope "owner"
        let otherScope = scope "other"
        let reference = SecretReference.parse "providers/openai/api-key"
        let initial = SecretValue.create reference "initial-secret"

        let store =
            SecretProvider.inMemory "test-memory" "1" [ AuthorizationScope.identity ownerScope, initial ]

        let provider = SecretManager.provider store

        let rotated =
            SecretManager.rotateAsync
                ownerScope
                reference
                SecretVersion.initial
                (SecretValue.create reference "rotated-secret")
                CancellationToken.None
                store
            |> _.Result

        Assert.AreEqual(SecretMutationKind.Rotated, rotated.Kind)
        Assert.AreEqual("test-memory", rotated.ProviderId)
        Assert.AreEqual("1", rotated.ProviderVersion)
        Assert.AreEqual(SecretVersion.initial, rotated.PreviousVersion)
        Assert.AreEqual("2", SecretVersion.value rotated.Version)

        let current =
            SecretProvider.resolveAsync ownerScope reference CancellationToken.None provider
            |> _.Result

        Assert.AreEqual(rotated.Version, (SecretResolution.evidence current).SecretVersion)
        Assert.AreEqual("rotated-secret", SecretResolution.value current |> SecretValue.useValue id)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            SecretManager.rotateAsync
                ownerScope
                reference
                SecretVersion.initial
                (SecretValue.create reference "stale-secret")
                CancellationToken.None
                store
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let revoked =
            SecretManager.revokeAsync ownerScope reference rotated.Version CancellationToken.None store
            |> _.Result

        Assert.AreEqual(SecretMutationKind.Revoked, revoked.Kind)
        Assert.AreEqual("3", SecretVersion.value revoked.Version)

        let revocation =
            Assert.ThrowsExactly<SecretRevokedException>(fun () ->
                SecretProvider.resolveAsync ownerScope reference CancellationToken.None provider
                |> _.GetAwaiter().GetResult()
                |> ignore)

        match revocation :> exn with
        | SecretRevokedException(actualReference, version, revokedAt) ->
            Assert.AreEqual(reference, actualReference)
            Assert.AreEqual(revoked.Version, version)
            Assert.AreEqual(revoked.ChangedAt, revokedAt)
        | _ -> Assert.Fail("Expected explicit secret revocation details.")

        Assert.ThrowsExactly<KeyNotFoundException>(fun () ->
            SecretManager.revokeAsync otherScope reference SecretVersion.initial CancellationToken.None store
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let restored =
            SecretManager.rotateAsync
                ownerScope
                reference
                revoked.Version
                (SecretValue.create reference "restored-secret")
                CancellationToken.None
                store
            |> _.Result

        let restoredResolution =
            SecretProvider.resolveAsync ownerScope reference CancellationToken.None provider
            |> _.Result

        Assert.AreEqual("4", SecretVersion.value restored.Version)
        Assert.AreEqual("restored-secret", SecretResolution.value restoredResolution |> SecretValue.useValue id)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            SecretManager.rotateAsync
                ownerScope
                reference
                restored.Version
                (SecretValue.create (SecretReference.parse "different") "wrong-reference")
                CancellationToken.None
                store
            |> ignore)
        |> ignore

        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()

        Assert.ThrowsExactly<OperationCanceledException>(fun () ->
            SecretManager.revokeAsync ownerScope reference restored.Version cancelled.Token store
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let stillAvailable =
            SecretProvider.resolveAsync ownerScope reference CancellationToken.None provider
            |> _.Result

        Assert.AreEqual(restored.Version, (SecretResolution.evidence stillAvailable).SecretVersion)

    [<TestMethod>]
    member _.``Secret provider rejects mismatched material and propagates cancellation``() =
        let authorization = scope "owner"
        let requested = SecretReference.parse "requested"
        let different = SecretReference.parse "different"

        let mismatched =
            SecretProvider.create "test" "1" (fun _ _ _ ->
                Task.FromResult(SecretLookup.Available(SecretVersion.initial, SecretValue.create different "secret")))

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            SecretProvider.resolveAsync authorization requested CancellationToken.None mismatched
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        let mutable invoked = false

        let provider =
            SecretProvider.create "test" "1" (fun _ _ _ ->
                invoked <- true
                Task.FromResult SecretLookup.Missing)

        Assert.ThrowsExactly<OperationCanceledException>(fun () ->
            SecretProvider.resolveAsync authorization requested cancellation.Token provider
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        Assert.IsFalse invoked

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            SecretProvider.create " test " "1" (fun _ _ _ -> Task.FromResult SecretLookup.Missing)
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Resolved secrets are redacted from artifacts failures and event payloads within one execution``() =
        let authorization = scope "owner"
        let reference = SecretReference.parse "canary/reference"
        let canary = "canary-secret-material"

        let provider =
            SecretProvider.create "test" "1" (fun _ actualReference _ ->
                Task.FromResult(
                    SecretLookup.Available(SecretVersion.initial, SecretValue.create actualReference canary)
                ))

        let mutable captured = None

        let bus =
            EventBus.create
                (fun event ->
                    captured <- Some event
                    Task.CompletedTask)
                ignore
                ignore

        SecretRedaction.runAsync (fun () ->
            task {
                let! _ = SecretProvider.resolveAsync authorization reference CancellationToken.None provider

                let artifact =
                    Artifact.create
                        (IdempotencyKey.forExecution (ExecutionId.generate ()))
                        "test"
                        "text/plain"
                        $"artifact:{canary}"

                Assert.AreEqual("artifact:[REDACTED]", artifact.Payload)

                let failure =
                    PlatformFailure.create PlatformErrorCategory.InternalFailure $"failure:{canary}" false None

                Assert.AreEqual("failure:[REDACTED]", failure.Message)

                let! lifecycle =
                    AgentLifecycle.create ()
                    |> AgentLifecycle.failAsync "agent" (InvalidOperationException($"lifecycle:{canary}"))

                match lifecycle.State with
                | LifecycleState.Failed message -> Assert.AreEqual("lifecycle:[REDACTED]", message)
                | state -> Assert.Fail($"Expected failed lifecycle state, got {state}.")

                do!
                    EventBus.publishAsync
                        (NaoEvent.TurnProgress(
                            EventScope.CreateEmpty(),
                            ProgressSignal.ReasoningAdded $"prompt:{canary}"
                        ))
                        bus
            })
        |> _.GetAwaiter().GetResult()

        match captured with
        | Some(NaoEvent.TurnProgress(_, ProgressSignal.ReasoningAdded content)) ->
            Assert.AreEqual("prompt:[REDACTED]", content)
        | _ -> Assert.Fail("Expected a redacted progress event.")

        Assert.AreEqual(canary, SecretRedaction.redact canary)
