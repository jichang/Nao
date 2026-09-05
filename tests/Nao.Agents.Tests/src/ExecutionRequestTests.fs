namespace Nao.Agents.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type ExecutionRequestTests() =
    let authorization () =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant-a") (UserId.parse "user-a") [ GroupId.parse "group-a" ]

        AuthorizationScope.tryCreate
            principal
            (Some(GroupId.parse "group-a"))
            (WorkspaceId.parse "workspace-a")
            (Some(SessionId.parse "session-a"))
        |> Option.get

    [<TestMethod>]
    member _.CreatePreservesIdentityBudgetsVersionsAndCorrelation() =
        let correlation = CorrelationContext.root ()
        let limits = ResourceLimits.Constrained 30 4 2000
        let sandbox = SandboxConfig.Restricted limits
        let policyCount = Random.Shared.Next(1, 5)
        let dependencyCount = Random.Shared.Next(1, 5)

        let policyVersions =
            [ for index in 1..policyCount -> sprintf "policy-%d" index, sprintf "v%d" index ]
            |> Map.ofList

        let dependencyVersions =
            [ for index in 1..dependencyCount -> sprintf "dependency-%d" index, sprintf "v%d" index ]
            |> Map.ofList

        let request =
            ExecutionRequest.create
                (authorization ())
                (TurnId.parse "turn-a")
                "conversation-a"
                "agent-a"
                "input"
                sandbox
                policyVersions
                dependencyVersions
                correlation

        Assert.AreEqual("agent-a", request.AgentId)
        Assert.AreEqual("conversation-a", request.ConversationId)
        Assert.AreEqual("input", request.Input)
        Assert.AreEqual(limits, request.Sandbox.Limits)
        Assert.AreEqual(policyVersions, request.PolicyVersions)
        Assert.AreEqual(dependencyVersions, request.DependencyVersions)
        Assert.AreEqual(correlation, request.Correlation)
        Assert.AreEqual(UserId.parse "user-a", AuthorizationScope.userId request.Authorization)

    [<TestMethod>]
    member _.CreateRejectsAmbiguousAgentAndVersionIdentity() =
        let create conversationId agentId policies dependencies =
            ExecutionRequest.create
                (authorization ())
                (TurnId.parse "turn-a")
                conversationId
                agentId
                "input"
                SandboxConfig.Default
                policies
                dependencies
                (CorrelationContext.root ())
            |> ignore

        let cases =
            [ (fun () -> create " " "agent" Map.empty Map.empty)
              (fun () -> create "conversation" " " Map.empty Map.empty)
              (fun () -> create "conversation" "agent" (Map.ofList [ " ", "v1" ]) Map.empty)
              (fun () -> create "conversation" "agent" (Map.ofList [ "policy", " " ]) Map.empty)
              (fun () -> create "conversation" "agent" Map.empty (Map.ofList [ " dependency", "v1" ]))
              (fun () -> create "conversation" "agent" Map.empty (Map.ofList [ "dependency", "v1 " ])) ]

        for invalid in cases do
            Assert.ThrowsExactly<ArgumentException>(invalid) |> ignore
