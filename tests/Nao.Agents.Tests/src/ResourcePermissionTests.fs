namespace Nao.Agents.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type ResourcePermissionTests() =
    let rule appliesTo decision scope =
        { Id = Guid.NewGuid().ToString("N")
          AppliesTo = appliesTo
          Decision = decision
          Scope = scope
          CreatedAt = DateTimeOffset.UtcNow }

    [<TestMethod>]
    member _.CanonicalResourcesRoundTripAndRejectUnknownActions() =
        let resources =
            [ ResourceAccess.file FileAction.Read "/tmp/../tmp/a", ("file", "read", "/tmp/a")
              ResourceAccess.web WebAction.Post "https://EXAMPLE.com/path", ("web", "post", "https://example.com/path")
              ResourceAccess.tool "search", ("tool", "invoke", "search")
              ResourceAccess.agent "researcher", ("agent", "delegate", "researcher")
              ResourceAccess.memory "working" MemoryAction.Write, ("memory", "write", "working")
              ResourceAccess.knowledge "handbook" KnowledgeAction.Retrieve, ("knowledge", "retrieve", "handbook")
              ResourceAccess.model "reasoner", ("model", "invoke", "reasoner")
              ResourceAccess.artifact "report" ArtifactAction.Publish, ("artifact", "publish", "report")
              ResourceAccess.administration "users" AdministrationAction.Manage, ("administration", "manage", "users") ]

        for access, (kind, action, resource) in resources do
            Assert.AreEqual(kind, ResourceAccess.kind access)
            Assert.AreEqual(action, ResourceAccess.action access)
            Assert.AreEqual(resource, ResourceAccess.resource access)
            Assert.AreEqual(Some access, ResourceAccess.tryCreate kind action resource)

        Assert.AreEqual(None, ResourceAccess.tryCreate "file" "invoke" "/tmp/a")
        Assert.AreEqual(None, ResourceAccess.tryCreate "unknown" "read" "resource")

    [<TestMethod>]
    member _.CanonicalResourcesRejectMalformedIdentifiers() =
        Assert.ThrowsExactly<ArgumentException>(fun () -> ResourceAccess.file FileAction.Read " " |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> ResourceAccess.web WebAction.Get "file:///tmp/a" |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> ResourceAccess.tool " search" |> ignore)
        |> ignore

        let wildcard =
            rule (PermissionTarget.Tool "*") PermissionDecision.Allow RuleScope.Global

        Assert.AreEqual(
            PermissionDecision.Deny,
            ResourcePermission.evaluateWith PermissionDecision.Allow [ wildcard ] (ResourceAccess.Tool " ")
        )

        let blankPattern =
            rule (PermissionTarget.Tool "") PermissionDecision.Allow RuleScope.Global

        Assert.AreEqual(
            PermissionDecision.Deny,
            ResourcePermission.evaluate [ blankPattern ] (ResourceAccess.tool "search")
        )

    [<TestMethod>]
    member _.ApprovedResourceCoversOnlyEquivalentActionAndResource() =
        Assert.IsTrue(
            ResourceAccess.isCoveredBy
                (ResourceAccess.file FileAction.Read "/tmp/../tmp/a")
                (ResourceAccess.file FileAction.Read "/tmp/a")
        )

        Assert.IsTrue(
            ResourceAccess.isCoveredBy
                (ResourceAccess.web WebAction.Get "https://example.com/first")
                (ResourceAccess.web WebAction.Get "https://example.com/second")
        )

        Assert.IsFalse(
            ResourceAccess.isCoveredBy
                (ResourceAccess.web WebAction.Get "https://example.com")
                (ResourceAccess.web WebAction.Get "http://example.com")
        )

        Assert.IsFalse(
            ResourceAccess.isCoveredBy
                (ResourceAccess.web WebAction.Get "https://example.com")
                (ResourceAccess.web WebAction.Get "https://example.com:8443")
        )

        Assert.IsFalse(
            ResourceAccess.isCoveredBy
                (ResourceAccess.file FileAction.Read "/tmp/a")
                (ResourceAccess.file FileAction.Write "/tmp/a")
        )

        Assert.IsFalse(
            ResourceAccess.isCoveredBy
                (ResourceAccess.web WebAction.Get "https://example.com")
                (ResourceAccess.web WebAction.Get "https://other.com")
        )

        Assert.IsFalse(ResourceAccess.isCoveredBy (ResourceAccess.tool "search") (ResourceAccess.tool "delete"))

        Assert.IsFalse(
            ResourceAccess.isCoveredBy
                (ResourceAccess.tool "search")
                (ResourceAccess.file FileAction.Read "/tmp/search")
        )

    [<TestMethod>]
    member _.GlobMatchingIsExplicitAndCaseSensitive() =
        Assert.IsTrue(ResourcePermission.glob "*" "anything")
        Assert.IsTrue(ResourcePermission.glob "a*c" "abbbc")
        Assert.IsTrue(ResourcePermission.glob "a?c" "abc")
        Assert.IsFalse(ResourcePermission.glob "a?c" "ac")
        Assert.IsFalse(ResourcePermission.glob "Example.COM" "example.com")

    [<TestMethod>]
    member _.WebOriginsRequireExplicitSchemePortAndSubdomainWildcards() =
        Assert.AreEqual(Some "https://a.example.com", ResourcePermission.originOf "https://a.EXAMPLE.com/x")
        Assert.AreEqual(None, ResourcePermission.originOf "a.example.com/x")
        Assert.IsTrue(ResourcePermission.originMatches "https://example.com" "https://example.com/path")
        Assert.IsFalse(ResourcePermission.originMatches "https://example.com" "http://example.com")
        Assert.IsFalse(ResourcePermission.originMatches "https://example.com" "https://example.com:8443")
        Assert.IsFalse(ResourcePermission.originMatches "https://example.com" "https://api.example.com")
        Assert.IsTrue(ResourcePermission.originMatches "https://*.example.com" "https://api.example.com")
        Assert.IsFalse(ResourcePermission.originMatches "https://*.example.com" "https://example.com")

    [<TestMethod>]
    member _.FilePatternsMatchExactDescendantOrExplicitGlob() =
        Assert.IsTrue(ResourcePermission.pathMatches "/home/me/project" "/home/me/project")
        Assert.IsTrue(ResourcePermission.pathMatches "/home/me/project" "/home/me/project/sub/a.txt")
        Assert.IsFalse(ResourcePermission.pathMatches "/home/me/project" "/home/me/projectile")
        Assert.IsTrue(ResourcePermission.pathMatches "/home/*/report.txt" "/home/me/report.txt")
        Assert.IsTrue(ResourcePermission.pathMatches "*" "/anywhere")

    [<TestMethod>]
    member _.EvaluateDeniesByDefaultAndDenyAlwaysWins() =
        let access = ResourceAccess.web WebAction.Get "https://example.com"
        Assert.AreEqual(PermissionDecision.Deny, ResourcePermission.evaluate [] access)

        let allow =
            rule
                (PermissionTarget.Web("https://example.com", PermissionActions.only [ WebAction.Get ]))
                PermissionDecision.Allow
                RuleScope.Global

        let ask =
            rule
                (PermissionTarget.Web("https://example.com", PermissionActions.all))
                PermissionDecision.Ask
                RuleScope.Global

        let deny =
            rule
                (PermissionTarget.Web("https://example.com", PermissionActions.only [ WebAction.Get ]))
                PermissionDecision.Deny
                RuleScope.Global

        Assert.AreEqual(PermissionDecision.Deny, ResourcePermission.evaluate [ allow; ask; deny ] access)

    [<TestMethod>]
    member _.ActionsAreExplicitAndCannotUseEmptyAsWildcard() =
        Assert.ThrowsExactly<ArgumentException>(fun () -> PermissionActions.only [] |> ignore)
        |> ignore

        let readRule =
            rule
                (PermissionTarget.File("/data", PermissionActions.only [ FileAction.Read ]))
                PermissionDecision.Allow
                RuleScope.Global

        Assert.AreEqual(
            PermissionDecision.Allow,
            ResourcePermission.evaluate [ readRule ] (ResourceAccess.file FileAction.Read "/data/a.txt")
        )

        Assert.AreEqual(
            PermissionDecision.Deny,
            ResourcePermission.evaluate [ readRule ] (ResourceAccess.file FileAction.Write "/data/a.txt")
        )

    [<TestMethod>]
    member _.EveryCanonicalResourceClassSupportsMatchingAndDenyPrecedence() =
        let cases =
            [ PermissionTarget.File("*", PermissionActions.all), ResourceAccess.file FileAction.List "/tmp"
              PermissionTarget.Web("*", PermissionActions.all), ResourceAccess.web WebAction.Head "https://example.com"
              PermissionTarget.Tool "*", ResourceAccess.tool "search"
              PermissionTarget.Agent "*", ResourceAccess.agent "researcher"
              PermissionTarget.Memory("*", PermissionActions.all), ResourceAccess.memory "working" MemoryAction.Read
              PermissionTarget.Knowledge("*", PermissionActions.all),
              ResourceAccess.knowledge "handbook" KnowledgeAction.Index
              PermissionTarget.Model "*", ResourceAccess.model "reasoner"
              PermissionTarget.Artifact("*", PermissionActions.all),
              ResourceAccess.artifact "report" ArtifactAction.Read
              PermissionTarget.Administration("*", PermissionActions.all),
              ResourceAccess.administration "users" AdministrationAction.Read ]

        for target, access in cases do
            let allow = rule target PermissionDecision.Allow RuleScope.Global
            let deny = rule target PermissionDecision.Deny RuleScope.Global
            Assert.AreEqual(PermissionDecision.Allow, ResourcePermission.evaluate [ allow ] access)
            Assert.AreEqual(PermissionDecision.Deny, ResourcePermission.evaluate [ allow; deny ] access)

    [<TestMethod>]
    member _.TargetCoverageIncludesActionsAndResourceHierarchy() =
        let broader =
            PermissionTarget.File("/data", PermissionActions.only [ FileAction.Read; FileAction.Write ])

        let narrower =
            PermissionTarget.File("/data/reports", PermissionActions.only [ FileAction.Read ])

        let wrongAction =
            PermissionTarget.File("/data/reports", PermissionActions.only [ FileAction.Delete ])

        Assert.IsTrue(ResourcePermission.targetCovers broader narrower)
        Assert.IsFalse(ResourcePermission.targetCovers broader wrongAction)

    [<TestMethod>]
    member _.ApplicableRulesCannotLeakAcrossSessions() =
        let globalRule =
            rule (PermissionTarget.Model "safe-*") PermissionDecision.Allow RuleScope.Global

        let sessionRule =
            rule (PermissionTarget.Model "private") PermissionDecision.Allow (RuleScope.Session "user/1")

        let otherSession =
            rule (PermissionTarget.Model "other") PermissionDecision.Allow (RuleScope.Session "user/2")

        let kept =
            ResourcePermission.applicable "user/1" [ globalRule; sessionRule; otherSession ]

        Assert.AreEqual([ globalRule; sessionRule ], kept)
        Assert.AreEqual(PermissionDecision.Allow, ResourcePermission.evaluate kept (ResourceAccess.model "private"))

        Assert.AreEqual(
            PermissionDecision.Deny,
            ResourcePermission.evaluate
                (ResourcePermission.applicable "user/2" [ sessionRule ])
                (ResourceAccess.model "private")
        )
