namespace Nao.Runtime.Orleans.Tests

open System.IO
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Runtime.Orleans.Grains

[<TestClass>]
type GrainStateVersionTests() =

    [<TestMethod>]
    member _.NewStateReceivesCurrentVersion() =
        let mutable version = 0

        GrainStateVersion.prepare GrainStateVersion.SessionCurrent "Session state" false version (fun value ->
            version <- value)

        Assert.AreEqual(GrainStateVersion.SessionCurrent, version)

    [<TestMethod>]
    member _.CurrentPersistedStateIsAcceptedWithoutMutation() =
        let mutable setterCalled = false

        GrainStateVersion.prepare
            GrainStateVersion.SessionCurrent
            "Session state"
            true
            GrainStateVersion.SessionCurrent
            (fun _ -> setterCalled <- true)

        Assert.IsFalse(setterCalled)

    [<TestMethod>]
    member _.UnsupportedPersistedStateIsRejectedWithoutMutation() =
        let mutable setterCalled = false

        let error =
            Assert.ThrowsExactly<InvalidDataException>(fun () ->
                GrainStateVersion.prepare GrainStateVersion.SessionCurrent "Session state" true 0 (fun _ ->
                    setterCalled <- true))

        StringAssert.Contains(error.Message, "version 0")
        StringAssert.Contains(error.Message, "docs/migrations")
        Assert.IsFalse(setterCalled)
