namespace Nao.Agents.Tests

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type PlatformFailureTests() =

    [<TestMethod>]
    member _.ClassifiesKnownExceptionsConsistentlyAcrossBoundaries() =
        let boundaries =
            [ PlatformFailureBoundary.Agent
              PlatformFailureBoundary.Tool
              PlatformFailureBoundary.Provider
              PlatformFailureBoundary.Storage
              PlatformFailureBoundary.Host ]

        let cases =
            [ (fun () -> UnauthorizedAccessException("denied") :> exn), PlatformErrorCategory.PermissionDenied, false
              (fun () -> ArgumentException("invalid") :> exn), PlatformErrorCategory.InvalidInput, false
              (fun () -> JsonException("malformed") :> exn), PlatformErrorCategory.InvalidOutput, false
              (fun () -> TimeoutException("timeout") :> exn), PlatformErrorCategory.TransientDependency, true
              (fun () -> TaskCanceledException("request timeout") :> exn),
              PlatformErrorCategory.TransientDependency,
              true
              (fun () -> IOException("storage unavailable") :> exn), PlatformErrorCategory.TransientDependency, true
              (fun () -> HttpRequestException("provider unavailable") :> exn),
              PlatformErrorCategory.TransientDependency,
              true
              (fun () -> OperationCanceledException("cancelled") :> exn), PlatformErrorCategory.Cancelled, false ]

        for boundary in boundaries do
            for createException, expectedCategory, expectedRetryable in cases do
                let failure =
                    PlatformFailure.fromException boundary (Some "correlation") (createException ())

                Assert.AreEqual(expectedCategory, failure.Category)
                Assert.AreEqual(expectedRetryable, failure.Retryable)
                Assert.AreEqual(Some "correlation", failure.CorrelationId)

    [<TestMethod>]
    member _.ClassifiesUnknownExceptionsByBoundaryResponsibility() =
        let cases =
            [ PlatformFailureBoundary.Agent, PlatformErrorCategory.InternalFailure, false
              PlatformFailureBoundary.Tool, PlatformErrorCategory.InternalFailure, false
              PlatformFailureBoundary.Provider, PlatformErrorCategory.TransientDependency, true
              PlatformFailureBoundary.Storage, PlatformErrorCategory.TransientDependency, true
              PlatformFailureBoundary.Host, PlatformErrorCategory.InternalFailure, false ]

        for boundary, expectedCategory, expectedRetryable in cases do
            let failure = PlatformFailure.fromException boundary None (Exception("unexpected"))
            Assert.AreEqual(expectedCategory, failure.Category)
            Assert.AreEqual(expectedRetryable, failure.Retryable)

    [<TestMethod>]
    member _.MapsProviderHttpStatusesConsistently() =
        let cases =
            [ 400, PlatformErrorCategory.InvalidInput, false
              401, PlatformErrorCategory.PermissionDenied, false
              403, PlatformErrorCategory.PermissionDenied, false
              404, PlatformErrorCategory.PermanentDependency, false
              408, PlatformErrorCategory.ResourceExhausted, true
              422, PlatformErrorCategory.InvalidInput, false
              429, PlatformErrorCategory.ResourceExhausted, true
              500, PlatformErrorCategory.TransientDependency, true
              503, PlatformErrorCategory.TransientDependency, true ]

        for status, expectedCategory, expectedRetryable in cases do
            let failure = PlatformFailure.fromHttpStatus None status "provider failed"
            Assert.AreEqual(expectedCategory, failure.Category)
            Assert.AreEqual(expectedRetryable, failure.Retryable)

    [<TestMethod>]
    member _.PreservesStructuredFailureThroughExceptionTransport() =
        let expected =
            PlatformFailure.create PlatformErrorCategory.ResourceExhausted "limited" true (Some "origin")

        let transported =
            PlatformFailure.fromException PlatformFailureBoundary.Host None (PlatformFailureException expected)

        Assert.AreEqual(expected, transported)
