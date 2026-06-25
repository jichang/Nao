namespace Nao.Assistant.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Assistant

/// Pins how built-in tools map to the resources the permission system guards. Tools that
/// authorize the specific resources they discover at runtime (`convert_document`) are NOT
/// statically classified — they request read/write on their exact source/target themselves.
[<TestClass>]
type ToolPermissionClassifyTests() =

    [<TestMethod>]
    member _.ConvertDocument_IsNotStaticallyClassified() =
        // convert_document confirms its exact source/target dynamically inside the tool, so the
        // static guard must not also classify (and double-prompt for) it.
        Assert.AreEqual(0, List.length (ToolPermissions.classifyAll "convert_document" """{"source":"report.md","target":"pdf"}"""))

    [<TestMethod>]
    member _.SingleResourceTool_StillYieldsOneAccess() =
        let accesses = ToolPermissions.classifyAll "read_file" """{"path":"notes.txt"}"""
        Assert.AreEqual(1, List.length accesses)
        match accesses.[0] with
        | ResourceAccess.File("read", path) -> StringAssert.EndsWith(path.Replace('\\', '/'), "notes.txt")
        | other -> Assert.Fail(sprintf "Expected read access, got %A" other)

    [<TestMethod>]
    member _.UnknownTool_YieldsNoAccess() =
        Assert.AreEqual(0, List.length (ToolPermissions.classifyAll "calculator" "1+1"))
