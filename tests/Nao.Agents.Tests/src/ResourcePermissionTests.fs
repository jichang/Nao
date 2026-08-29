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

    // ---- glob ----------------------------------------------------------------

    [<TestMethod>]
    member _.ApprovedResourceCoversEquivalentRequest() =
        Assert.IsTrue(ResourceAccess.isCoveredBy (ResourceAccess.File("READ", "/tmp/../tmp/a")) (ResourceAccess.File("read", "/tmp/a")))
        Assert.IsTrue(ResourceAccess.isCoveredBy (ResourceAccess.Web("GET", "https://example.com/first")) (ResourceAccess.Web("get", "https://example.com/second")))
        Assert.IsTrue(ResourceAccess.isCoveredBy (ResourceAccess.ToolCall "*") (ResourceAccess.ToolCall "search"))

    [<TestMethod>]
    member _.ApprovedResourceDoesNotCoverDifferentAccess() =
        Assert.IsFalse(ResourceAccess.isCoveredBy (ResourceAccess.File("read", "/tmp/a")) (ResourceAccess.File("write", "/tmp/a")))
        Assert.IsFalse(ResourceAccess.isCoveredBy (ResourceAccess.Web("GET", "https://example.com")) (ResourceAccess.Web("GET", "https://other.com")))
        Assert.IsFalse(ResourceAccess.isCoveredBy (ResourceAccess.ToolCall "search") (ResourceAccess.ToolCall "delete"))
        Assert.IsFalse(ResourceAccess.isCoveredBy (ResourceAccess.ToolCall "search") (ResourceAccess.File("read", "/tmp/search")))

    [<TestMethod>]
    member _.GlobMatchesStarAndQuestion() =
        Assert.IsTrue(ResourcePermission.glob "*" "anything")
        Assert.IsTrue(ResourcePermission.glob "a*c" "abbbc")
        Assert.IsTrue(ResourcePermission.glob "a?c" "abc")
        Assert.IsFalse(ResourcePermission.glob "a?c" "ac")
        Assert.IsFalse(ResourcePermission.glob "abc" "abd")

    [<TestMethod>]
    member _.GlobIsCaseInsensitive() =
        Assert.IsTrue(ResourcePermission.glob "Example.COM" "example.com")

    // ---- hostOf / hostMatches ------------------------------------------------

    [<TestMethod>]
    member _.HostOfHandlesUrlAndBareHost() =
        Assert.AreEqual(Some "a.example.com", ResourcePermission.hostOf "https://a.example.com/x")
        Assert.AreEqual(Some "example.com", ResourcePermission.hostOf "example.com/page")

    [<TestMethod>]
    member _.HostMatchesSubdomainAndWildcard() =
        Assert.IsTrue(ResourcePermission.hostMatches "example.com" "example.com")
        Assert.IsTrue(ResourcePermission.hostMatches "example.com" "api.example.com")
        Assert.IsFalse(ResourcePermission.hostMatches "example.com" "notexample.com")
        Assert.IsTrue(ResourcePermission.hostMatches "*" "anything.org")

    // ---- pathMatches ---------------------------------------------------------

    [<TestMethod>]
    member _.PathMatchesPrefixAndExact() =
        Assert.IsTrue(ResourcePermission.pathMatches "/home/me/project" "/home/me/project")
        Assert.IsTrue(ResourcePermission.pathMatches "/home/me/project" "/home/me/project/sub/a.txt")
        Assert.IsFalse(ResourcePermission.pathMatches "/home/me/project" "/home/me/projectile")
        Assert.IsTrue(ResourcePermission.pathMatches "*" "/anywhere")

    // ---- evaluate / defaults -------------------------------------------------

    [<TestMethod>]
    member _.EvaluateDeniesByDefault() =
        let access = ResourceAccess.Web("GET", "https://example.com")
        Assert.AreEqual(PermissionDecision.Deny, ResourcePermission.evaluate [] access)

    [<TestMethod>]
    member _.EvaluateWithCustomDefault() =
        let access = ResourceAccess.ToolCall "unknown"
        Assert.AreEqual(PermissionDecision.Allow, ResourcePermission.evaluateWith PermissionDecision.Allow [] access)

    [<TestMethod>]
    member _.AllowRulePermitsMatchingWeb() =
        let r = rule (PermissionTarget.Web("example.com", [])) PermissionDecision.Allow RuleScope.Global
        let access = ResourceAccess.Web("GET", "https://api.example.com/data")
        Assert.AreEqual(PermissionDecision.Allow, ResourcePermission.evaluate [ r ] access)

    [<TestMethod>]
    member _.DenyRuleWinsOverAllow() =
        let allow = rule (PermissionTarget.Web("example.com", [])) PermissionDecision.Allow RuleScope.Global
        let deny = rule (PermissionTarget.Web("example.com", [])) PermissionDecision.Deny RuleScope.Global
        let access = ResourceAccess.Web("GET", "https://example.com")
        Assert.AreEqual(PermissionDecision.Deny, ResourcePermission.evaluate [ allow; deny ] access)

    [<TestMethod>]
    member _.OperationFilterRestrictsRule() =
        let r = rule (PermissionTarget.File("/data", [ "read" ])) PermissionDecision.Allow RuleScope.Global
        let readAccess = ResourceAccess.File("read", "/data/a.txt")
        let writeAccess = ResourceAccess.File("write", "/data/a.txt")
        Assert.AreEqual(PermissionDecision.Allow, ResourcePermission.evaluate [ r ] readAccess)
        // write isn't covered, so falls through to deny-by-default
        Assert.AreEqual(PermissionDecision.Deny, ResourcePermission.evaluate [ r ] writeAccess)

    // ---- scoping -------------------------------------------------------------

    [<TestMethod>]
    member _.ApplicableKeepsGlobalAndMatchingSession() =
        let g = rule (PermissionTarget.Web("a.com", [])) PermissionDecision.Allow RuleScope.Global
        let s1 = rule (PermissionTarget.Web("b.com", [])) PermissionDecision.Allow (RuleScope.Session "user/1")
        let s2 = rule (PermissionTarget.Web("c.com", [])) PermissionDecision.Allow (RuleScope.Session "user/2")
        let kept = ResourcePermission.applicable "user/1" [ g; s1; s2 ]
        Assert.AreEqual(2, kept.Length)
        Assert.IsTrue(kept |> List.contains g)
        Assert.IsTrue(kept |> List.contains s1)
        Assert.IsFalse(kept |> List.contains s2)

    [<TestMethod>]
    member _.SessionRuleDoesNotLeakToOtherSession() =
        let s1 = rule (PermissionTarget.Web("b.com", [])) PermissionDecision.Allow (RuleScope.Session "user/1")
        let access = ResourceAccess.Web("GET", "https://b.com")
        let forUser2 = ResourcePermission.applicable "user/2" [ s1 ]
        Assert.AreEqual(PermissionDecision.Deny, ResourcePermission.evaluate forUser2 access)
        let forUser1 = ResourcePermission.applicable "user/1" [ s1 ]
        Assert.AreEqual(PermissionDecision.Allow, ResourcePermission.evaluate forUser1 access)
