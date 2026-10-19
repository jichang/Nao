namespace Nao.Runtime.Orleans.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Runtime.Orleans.Grains

[<TestClass>]
type TelemetryPropagationTests() =
    [<TestMethod>]
    member _.``Orleans correlation envelope preserves execution causation and retry lineage``() =
        let root = CorrelationContext.root ()
        let delegated = CorrelationContext.delegateFrom root
        let retry = CorrelationContext.retry delegated
        let envelope = OrleansCorrelationContext.fromDomain retry
        let restored = OrleansCorrelationContext.toDomain envelope

        Assert.AreEqual(retry, restored)
        Assert.AreEqual(root.CorrelationId, restored.CorrelationId)
        Assert.AreEqual(Some delegated.ExecutionId, restored.CausationId)
        Assert.AreEqual(2, restored.Attempt)
