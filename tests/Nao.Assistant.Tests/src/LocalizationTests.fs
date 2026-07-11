namespace Nao.Assistant.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Microsoft.FSharp.Reflection
open Nao.Assistant

/// Tests for the localization tables. Every shipped language must round-trip through its
/// persisted code and provide a complete, non-empty string table so the UI never renders
/// a blank label after a language switch.
[<TestClass>]
type LocalizationTests() =

    [<TestMethod>]
    member _.Code_round_trips_through_parse_for_every_language() =
        for lang in Localization.all do
            let parsed = Localization.parse (Localization.code lang)
            Assert.AreEqual(lang, parsed, sprintf "code/parse round-trip failed for %A" lang)

    [<TestMethod>]
    member _.Every_language_has_a_distinct_display_name() =
        let names = Localization.all |> List.map Localization.displayName
        Assert.IsTrue(names |> List.forall (fun n -> not (System.String.IsNullOrWhiteSpace n)), "display names must be non-empty")
        Assert.AreEqual(names.Length, (names |> List.distinct |> List.length), "display names must be unique")

    [<TestMethod>]
    member _.Every_language_table_has_all_fields_populated() =
        // Reflect over the Strings record so the test fails automatically if a new field is
        // added without translations in every language.
        let fields = FSharpType.GetRecordFields(typeof<Localization.Strings>)
        for lang in Localization.all do
            Localization.apply lang
            let strings = box (Localization.current ())
            for f in fields do
                let value = f.GetValue(strings) :?> string
                Assert.IsFalse(
                    System.String.IsNullOrWhiteSpace value,
                    sprintf "%A.%s is empty" lang f.Name)
        Localization.apply Localization.English

    [<TestMethod>]
    member _.Unknown_code_falls_back_to_English() =
        Assert.AreEqual(Localization.English, Localization.parse "xx")
        Assert.AreEqual(Localization.English, Localization.parse null)
        Assert.AreEqual(Localization.English, Localization.parse "")
