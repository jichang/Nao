namespace Nao.Core.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type PromptPatchTests () =

    let prompt =
        { Role = "base role"
          Objective = "base objective"
          DomainKnowledge = [ "base domain" ]
          Constraints = [ "base constraint" ]
          Examples =
              [ { Input = "base input"
                  Output = "base output"
                  Explanation = None } ]
          OutputFormat = FreeText
          Context = [ "base context" ] }

    [<TestMethod>]
    member _.ReplaceOperationsReplaceIndividualFields() =
        let patch =
            { PromptPatch.Empty with
                Role = Some(ReplaceText "new role")
                DomainKnowledge = Some(ReplaceList [ "new domain" ])
                OutputFormat = Some(ReplaceValue Markdown) }

        let result = Prompt.applyPatch patch prompt

        Assert.AreEqual("new role", result.Role)
        Assert.AreEqual([ "new domain" ], result.DomainKnowledge)
        Assert.AreEqual(Markdown, result.OutputFormat)
        Assert.AreEqual("base objective", result.Objective)
        Assert.AreEqual([ "base constraint" ], result.Constraints)

    [<TestMethod>]
    member _.AppendOperationsExtendTextAndLists() =
        let patch =
            { PromptPatch.Empty with
                Objective = Some(AppendText " plus extension")
                Constraints = Some(AppendList [ "extra constraint" ])
                Context = Some(AppendList [ "extra context" ]) }

        let result = Prompt.applyPatch patch prompt

        Assert.AreEqual("base objective plus extension", result.Objective)
        Assert.AreEqual([ "base constraint"; "extra constraint" ], result.Constraints)
        Assert.AreEqual([ "base context"; "extra context" ], result.Context)

    [<TestMethod>]
    member _.UpdateOperationsReceiveTheExistingFieldValue() =
        let patch =
            { PromptPatch.Empty with
                Role = Some(UpdateText String.ToUpperInvariant)
                Examples =
                    Some(UpdateList (fun examples ->
                        examples
                        |> List.map (fun example -> { example with Output = example.Output + " updated" }))) }

        let result = Prompt.applyPatch patch prompt

        Assert.AreEqual("BASE ROLE", result.Role)
        Assert.AreEqual("base output updated", result.Examples.Head.Output)
