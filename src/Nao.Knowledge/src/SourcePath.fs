namespace Nao.Knowledge

open System
open System.IO

module internal SourcePath =
    let globMatches (pattern: string) (value: string) =
        let pattern = pattern.Replace('\\', '/')
        let rows = pattern.Length + 1
        let columns = value.Length + 1
        let matches = Array2D.create rows columns false
        matches.[0, 0] <- true

        for patternIndex in 1 .. pattern.Length do
            if pattern.[patternIndex - 1] = '*' then
                matches.[patternIndex, 0] <- matches.[patternIndex - 1, 0]

        for patternIndex in 1 .. pattern.Length do
            for valueIndex in 1 .. value.Length do
                match pattern.[patternIndex - 1] with
                | '*' ->
                    matches.[patternIndex, valueIndex] <-
                        matches.[patternIndex - 1, valueIndex] || matches.[patternIndex, valueIndex - 1]
                | '?' -> matches.[patternIndex, valueIndex] <- matches.[patternIndex - 1, valueIndex - 1]
                | character ->
                    matches.[patternIndex, valueIndex] <-
                        character = value.[valueIndex - 1] && matches.[patternIndex - 1, valueIndex - 1]

        matches.[pattern.Length, value.Length]

    let selected includePatterns excludePatterns relativePath =
        let included =
            List.isEmpty includePatterns
            || includePatterns |> List.exists (fun pattern -> globMatches pattern relativePath)

        included
        && not (excludePatterns |> List.exists (fun pattern -> globMatches pattern relativePath))

    let mediaType (path: string) =
        match Path.GetExtension(path).ToLowerInvariant() with
        | ".txt" -> "text/plain"
        | ".md"
        | ".markdown" -> "text/markdown"
        | ".json" -> "application/json"
        | ".html"
        | ".htm" -> "text/html"
        | ".pdf" -> "application/pdf"
        | _ -> "application/octet-stream"
