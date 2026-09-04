namespace Nao.Runtime.Orleans.Tests

open System.IO
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Runtime.Orleans.Grains

[<TestClass>]
type GrainStateVersionTests() =

    [<TestMethod>]
    member _.NewStateReceivesCurrentVersion() =
        let mutable version = 0
        GrainStateVersion.prepare "Session state" false version (fun value -> version <- value)
        Assert.AreEqual(GrainStateVersion.Current, version)

    [<TestMethod>]
    member _.CurrentPersistedStateIsAcceptedWithoutMutation() =
        let mutable setterCalled = false

        GrainStateVersion.prepare "Session state" true GrainStateVersion.Current (fun _ -> setterCalled <- true)

        Assert.IsFalse(setterCalled)

    [<TestMethod>]
    member _.UnsupportedPersistedStateIsRejectedWithoutMutation() =
        let mutable setterCalled = false

        let error =
            Assert.ThrowsExactly<InvalidDataException>(fun () ->
                GrainStateVersion.prepare "Session state" true 0 (fun _ -> setterCalled <- true))

        StringAssert.Contains(error.Message, "version 0")
        StringAssert.Contains(error.Message, "docs/migrations")
        Assert.IsFalse(setterCalled)
