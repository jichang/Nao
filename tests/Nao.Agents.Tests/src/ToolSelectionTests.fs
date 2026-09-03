namespace Nao.Agents.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type ToolSelectionTests() =

    let tool name priority description inputSchema outputSchema =
        Tool.create
            name
            description
            priority
            []
            (ToolCodec.create inputSchema Ok Ok)
            (ToolCodec.create outputSchema Ok Ok)
            (ToolOperation.create (fun _ input -> Task.FromResult(Ok input)))

    let selector maxTools threshold =
        ToolSelector.create
            { MaxTools = maxTools
              RelevanceThreshold = threshold }

    [<TestMethod>]
    member _.``selection discovers once and matches schema terms``() =
        let generic = tool "lookup" 0 "Looks up information" "string" "string" 
        let invoice = tool "fetch" 0 "Fetches a record" "object: invoiceNumber" "object: invoiceStatus"
        let baseProtocol = ToolProtocol.fromTools [ generic; invoice ]
        let mutable discoveries = 0
        let protocol =
            { baseProtocol with
                ListTools = fun () ->
                    discoveries <- discoveries + 1
                    baseProtocol.ListTools() }

        let result = (selector 5 0.1).SelectAsync "check invoice status" 1000 protocol |> _.Result

        Assert.AreEqual(1, discoveries)
        Assert.AreEqual([ "fetch" ], result.Selected |> List.map _.Name)
        Assert.AreEqual(2, result.Available.Length)

    [<TestMethod>]
    member _.``selection is deterministic for equal scores``() =
        let alpha = tool "alpha" 0 "Handles reports" "string" "string"
        let beta = tool "beta" 0 "Handles reports" "string" "string"
        let select tools =
            let protocol = ToolProtocol.fromTools tools
            (selector 2 0.1).SelectAsync "reports" 1000 protocol
            |> fun task -> task.Result.Selected |> List.map _.Name

        Assert.AreEqual<string list>([ "alpha"; "beta" ], select [ beta; alpha ])
        Assert.AreEqual<string list>([ "alpha"; "beta" ], select [ alpha; beta ])

    [<TestMethod>]
    member _.``selection respects count and token budgets``() =
        let first = tool "first" 10 "Handles data" "string" "string"
        let second = tool "second" 0 "Handles data" "string" "string"
        let protocol = ToolProtocol.fromTools [ first; second ]

        let countLimited = (selector 1 0.1).SelectAsync "data" 1000 protocol |> _.Result
        let budgetLimited = (selector 2 0.1).SelectAsync "data" 0 protocol |> _.Result

        Assert.AreEqual([ "first" ], countLimited.Selected |> List.map _.Name)
        Assert.IsTrue(budgetLimited.Selected.IsEmpty)

    [<TestMethod>]
    member _.``selection falls back to highest priority tool``() =
        let low = tool "alpha" 0 "Unrelated" "string" "string"
        let high = tool "omega" 20 "Also unrelated" "string" "string"
        let protocol = ToolProtocol.fromTools [ low; high ]

        let result = (selector 3 0.5).SelectAsync "weather" 1000 protocol |> _.Result

        Assert.AreEqual([ "omega" ], result.Selected |> List.map _.Name)