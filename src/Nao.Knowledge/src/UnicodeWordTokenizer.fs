namespace Nao.Knowledge

open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module UnicodeWordTokenizer =
    let private tokenPattern =
        Regex(@"[\p{L}\p{M}\p{N}_]+|[^\s]", RegexOptions.Compiled ||| RegexOptions.CultureInvariant)

    let create () =
        { Descriptor =
            { Component = ComponentVersion.create "nao.tokenizer.unicode-word" "1"
              ConfigurationHash = ContentHash.computeText @"[\p{L}\p{M}\p{N}_]+|[^\s]"
              Deterministic = true }
          Tokenize =
            fun text ->
                if isNull text then
                    nullArg (nameof text)

                tokenPattern.Matches text
                |> Seq.map (fun token ->
                    { CharacterOffset = token.Index
                      CharacterLength = token.Length })
                |> List.ofSeq }
