module MSTestSettings

open Microsoft.VisualStudio.TestTools.UnitTesting

// Each test boots its own isolated Kestrel host on a free port, so run test
// methods serially to keep resource usage predictable.
[<assembly: Parallelize(Workers = 1, Scope = ExecutionScope.ClassLevel)>]
do ()
