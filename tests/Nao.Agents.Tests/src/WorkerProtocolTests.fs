namespace Nao.Agents.Tests

open System.IO
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type WorkerProtocolTests() =
    let principalJson =
        """{"tenantId":"tenant","subjectId":"user","impersonatedUserId":null,"impersonationReason":null,"serviceId":"api","serviceDelegations":[],"groupIds":["engineering"],"roles":["operator"],"claims":{"region":"private-region"},"authenticationStrength":"MultiFactor"}"""

    let executionId = "11111111-1111-1111-1111-111111111111"

    let message kind principal required =
        let node =
            JsonNode.Parse(
                """{"protocolVersion":0,"kind":"","agentId":"test","authorityAgentId":"test","input":"model input","output":"","error":"","executionId":"","correlationId":"","attempt":1,"processId":0,"enforceFileSystemPolicy":false,"allowedPaths":[],"secrets":{},"maxLlmCalls":10,"maxTotalTokens":100,"maxCostUsd":"1","maxToolCalls":10,"limit":"","llmCalls":0,"totalTokens":0,"estimatedCostUsd":"0","toolCalls":0,"principal":null,"agentLineage":[],"requirePrincipal":false}"""
            )
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Worker protocol fixture must be a JSON object.")

        node["protocolVersion"] <- JsonValue.Create WorkerProtocol.CurrentVersion
        node["kind"] <- JsonValue.Create(kind: string)
        node["principal"] <- JsonValue.Create "__principal__"
        node["requirePrincipal"] <- JsonValue.Create(required: bool)

        if required then
            node["executionId"] <- JsonValue.Create executionId

            node["agentLineage"] <- JsonNode.Parse(sprintf """[{"agentId":"test","executionId":"%s"}]""" executionId)

        node.ToJsonString().Replace("\"__principal__\"", principal)

    let run execute handler =
        use reader = new StringReader(message "hello" "null" false + "\n" + execute + "\n")
        use writer = new StringWriter()

        let result =
            WorkerProtocol.runSingleAsync
                WorkerProtocol.CurrentVersion
                (Map.ofList [ "test", handler ])
                reader
                writer
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        result, writer.ToString()

    [<TestMethod>]
    member _.UnreleasedWorkerProtocolAndResponseFramesUseVersionOne() =
        Assert.AreEqual(1, WorkerProtocol.CurrentVersion)

        let result, output =
            run (message "execute" principalJson true) (fun _ -> Task.FromResult(Ok "completed"))

        Assert.AreEqual(Ok(), result)
        let frames = output.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
        Assert.AreEqual(2, frames.Length)

        for frame in frames do
            use document = System.Text.Json.JsonDocument.Parse frame
            Assert.AreEqual(1, document.RootElement.GetProperty("protocolVersion").GetInt32())

    [<TestMethod>]
    member _.WorkerReceivesPrincipalSeparatelyFromInputWithoutEchoingAuthentication() =
        let expected =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse "tenant")
                (UserId.parse "user")
                (Some(ServiceId.parse "api"))
                [ GroupId.parse "engineering" ]
                [ "operator" ]
                (Map.ofList [ "region", "private-region" ])
                AuthenticationStrength.MultiFactor

        let mutable invoked = false

        let result, output =
            run (message "execute" principalJson true) (fun request ->
                invoked <- true
                Assert.AreEqual(Some expected, request.Principal)
                Assert.AreEqual("test", request.AuthorityAgentId)

                Assert.AreEqual(
                    [ { AgentId = "test"
                        ExecutionId = ExecutionId.parse executionId } ],
                    request.AgentLineage
                )

                Assert.AreEqual("model input", request.Input)
                Task.FromResult(Ok "completed"))

        Assert.AreEqual(Ok(), result)
        Assert.IsTrue invoked
        Assert.IsFalse(output.Contains("private-region"))
        Assert.IsFalse(output.Contains("operator"))
        StringAssert.Contains(output, "completed")

    [<TestMethod>]
    member _.WorkerReceivesImpersonationAndServiceDelegationAsHostMetadata() =
        let rootService = ServiceId.parse "api"
        let workerService = ServiceId.parse "worker"

        let servicePrincipal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse "tenant")
                (UserId.parse "user")
                (Some rootService)
                [ GroupId.parse "engineering" ]
                [ "operator" ]
                (Map.ofList [ "region", "private-region" ])
                AuthenticationStrength.MultiFactor

        let serviceScope =
            AuthorizationScope.tryCreate servicePrincipal None (WorkspaceId.parse "workspace") None
            |> Option.get
            |> AuthorizationScope.delegateService rootService workerService

        let delegatedPrincipal = AuthorizationScope.principal serviceScope

        let delegatedJson =
            principalJson.Replace("\"serviceDelegations\":[]", "\"serviceDelegations\":[\"worker\"]")

        let result, output =
            run (message "execute" delegatedJson true) (fun request ->
                Assert.AreEqual(Some delegatedPrincipal, request.Principal)
                Assert.AreEqual("model input", request.Input)
                Task.FromResult(Ok "completed"))

        Assert.AreEqual(Ok(), result)
        Assert.IsFalse(output.Contains("worker"))
        Assert.IsFalse(output.Contains("private-region"))

        let impersonatedPrincipal =
            SecurityPrincipal.createImpersonated
                (TenantId.parse "tenant")
                (UserId.parse "user")
                (UserId.parse "customer")
                "support-case-123"
                [ GroupId.parse "engineering" ]
                [ "operator" ]
                (Map.ofList [ "region", "private-region" ])
                AuthenticationStrength.MultiFactor

        let impersonatedJson =
            principalJson
                .Replace("\"impersonatedUserId\":null", "\"impersonatedUserId\":\"customer\"")
                .Replace("\"impersonationReason\":null", "\"impersonationReason\":\"support-case-123\"")
                .Replace("\"serviceId\":\"api\"", "\"serviceId\":null")

        let result, output =
            run (message "execute" impersonatedJson true) (fun request ->
                Assert.AreEqual(Some impersonatedPrincipal, request.Principal)
                Assert.AreEqual("model input", request.Input)
                Task.FromResult(Ok "completed"))

        Assert.AreEqual(Ok(), result)
        Assert.IsFalse(output.Contains("customer"))
        Assert.IsFalse(output.Contains("support-case-123"))

    [<TestMethod>]
    member _.WorkerRejectsMissingMalformedOrAmbiguousPrincipalBeforeInvocation() =
        let invalidPrincipals =
            [ "null"
              principalJson.Replace("\"tenant\"", "\" \"")
              principalJson.Replace("\"user\"", "null")
              principalJson.Replace("\"api\"", "\" api\"")
              principalJson.Replace("\"engineering\"", "\"\"")
              principalJson.Replace("\"operator\"", "\" operator\"")
              principalJson.Replace("\"private-region\"", "null")
              principalJson.Replace("\"region\":", "\" region\":")
              principalJson.Replace("\"MultiFactor\"", "\"Unknown\"")
              principalJson.Replace("\"impersonatedUserId\":null", "\"unrelated\":null")
              principalJson.Replace("\"impersonationReason\":null", "\"unrelated\":null")
              principalJson.Replace("\"serviceDelegations\":[]", "\"unrelated\":[]")
              principalJson.Replace("\"impersonatedUserId\":null", "\"impersonatedUserId\":\"user\"")
              principalJson
                  .Replace("\"impersonatedUserId\":null", "\"impersonatedUserId\":\"customer\"")
                  .Replace("\"impersonationReason\":null", "\"impersonationReason\":\"support-case-123\"")
              principalJson.Replace("\"serviceDelegations\":[]", "\"serviceDelegations\":[\"api\"]")
              principalJson.Replace("\"serviceDelegations\":[]", "\"serviceDelegations\":[\" worker\"]")
              principalJson.Replace("\"tenantId\":\"tenant\"", "\"tenantId\":\"tenant\",\"tenantId\":\"other\"")
              principalJson.Replace(
                  "\"impersonatedUserId\":null",
                  "\"impersonatedUserId\":null,\"impersonatedUserId\":null"
              )
              principalJson.Replace("\"serviceDelegations\":[]", "\"serviceDelegations\":[],\"serviceDelegations\":[]")
              principalJson.Replace(
                  "\"region\":\"private-region\"",
                  "\"region\":\"private-region\",\"region\":\"other\""
              ) ]

        for principal in invalidPrincipals do
            let mutable invoked = false

            let result, output =
                run (message "execute" principal true) (fun _ ->
                    invoked <- true
                    Task.FromResult(Ok "unexpected"))

            Assert.AreEqual(Error "Worker protocol message was invalid.", result)
            Assert.IsFalse invoked
            Assert.IsFalse(output.Contains("private-region"))

    [<TestMethod>]
    member _.WorkerRejectsOmittedOrDuplicateIdentityEnvelopeFields() =
        let execute = message "execute" principalJson true

        let invalidMessages =
            [ execute.Replace("\"requirePrincipal\":true", "\"requirePrincipal\":true,\"requirePrincipal\":false")
              execute.Replace("\"requirePrincipal\":true", "\"unrelated\":true")
              execute.Replace("\"principal\":", "\"unrelated\":")
              execute.Replace("\"principal\":", "\"principal\":null,\"principal\":")
              execute.Replace("\"authorityAgentId\":\"test\"", "\"unrelated\":\"test\"")
              execute.Replace(
                  "\"authorityAgentId\":\"test\"",
                  "\"authorityAgentId\":\"test\",\"authorityAgentId\":\"test\""
              )
              execute.Replace("\"agentLineage\":", "\"unrelated\":")
              execute.Replace("\"agentLineage\":", "\"agentLineage\":[],\"agentLineage\":") ]

        for invalidMessage in invalidMessages do
            let result, _ =
                run invalidMessage (fun _ ->
                    Assert.Fail "Invalid identity must not reach the handler."
                    Task.FromResult(Ok "unexpected"))

            Assert.AreEqual(Error "Worker protocol message was invalid.", result)

    [<TestMethod>]
    member _.WorkerRejectsMissingOrMismatchedAgentAuthority() =
        let execute = message "execute" principalJson true

        let invalidMessages =
            [ execute.Replace(sprintf """[{"agentId":"test","executionId":"%s"}]""" executionId, "[]")
              execute.Replace("\"authorityAgentId\":\"test\"", "\"authorityAgentId\":\" \"")
              execute.Replace("\"authorityAgentId\":\"test\"", "\"authorityAgentId\":\"other\"")
              execute.Replace("\"agentId\":\"test\",\"executionId\"", "\"agentId\":\" \",\"executionId\"")
              execute.Replace(
                  sprintf """[{"agentId":"test","executionId":"%s"}]""" executionId,
                  sprintf
                      """[{"agentId":"parent","executionId":"%s"},{"agentId":"test","executionId":"%s"}]"""
                      executionId
                      executionId
              )
              execute.Replace(
                  sprintf "\"executionId\":\"%s\"}]" executionId,
                  "\"executionId\":\"22222222-2222-2222-2222-222222222222\"}]"
              ) ]

        for invalidMessage in invalidMessages do
            let result, _ =
                run invalidMessage (fun _ ->
                    Assert.Fail "Mismatched delegated authority must not reach the handler."
                    Task.FromResult(Ok "unexpected"))

            Assert.AreEqual(Error "Worker protocol message was invalid.", result)

    [<TestMethod>]
    member _.UnrestrictedWorkerMayOmitPrincipalButCannotSupplyInvalidIdentity() =
        let result, _ =
            run (message "execute" "null" false) (fun request ->
                Assert.AreEqual(None, request.Principal)
                Task.FromResult(Ok "completed"))

        Assert.AreEqual(Ok(), result)

        let result, _ =
            run (message "execute" "{}" false) (fun _ ->
                Assert.Fail "Malformed optional identity must not reach the handler."
                Task.FromResult(Ok "unexpected"))

        Assert.AreEqual(Error "Worker protocol message was invalid.", result)

    [<TestMethod>]
    member _.WorkerRejectsUnsupportedProtocolVersions() =
        for unsupportedVersion in [ 0; WorkerProtocol.CurrentVersion + 1 ] do
            let execute =
                message "execute" principalJson true
                |> _.Replace(
                    "\"protocolVersion\":" + string WorkerProtocol.CurrentVersion,
                    "\"protocolVersion\":" + string unsupportedVersion
                )

            let result, _ =
                run execute (fun _ ->
                    Assert.Fail "An unsupported protocol must not reach the handler."
                    Task.FromResult(Ok "unexpected"))

            Assert.AreEqual(Error(sprintf "Unsupported worker protocol version %d." unsupportedVersion), result)
