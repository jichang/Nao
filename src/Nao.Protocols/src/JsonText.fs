namespace Nao.Protocols

open System
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
module JsonText =
    /// Render a System.Text.Json parse failure with its path and source location.
    let formatException (ex: JsonException) =
        let location =
            match ex.LineNumber.HasValue, ex.BytePositionInLine.HasValue with
            | true, true -> sprintf "line %d, byte %d" ex.LineNumber.Value ex.BytePositionInLine.Value
            | true, false -> sprintf "line %d" ex.LineNumber.Value
            | _ -> "unknown location"
        let path = if String.IsNullOrWhiteSpace ex.Path then "$" else ex.Path
        sprintf "JSON syntax error at %s, path %s: %s" location path ex.Message

    /// Extract the first balanced object, respecting double- and single-quoted strings.
    let tryExtractBalancedObject (text: string) : string option =
        let start = text.IndexOf('{')
        if start < 0 then None
        else
            let mutable depth = 0
            let mutable inString = false
            let mutable quote = '\000'
            let mutable escaped = false
            let mutable finish = -1
            for index in start .. text.Length - 1 do
                let character = text[index]
                if finish < 0 then
                    if inString then
                        if escaped then escaped <- false
                        elif character = '\\' then escaped <- true
                        elif character = quote then inString <- false
                    elif character = '"' || character = '\'' then
                        inString <- true
                        quote <- character
                    elif character = '{' then depth <- depth + 1
                    elif character = '}' then
                        depth <- depth - 1
                        if depth = 0 then finish <- index
            if finish < 0 then None else Some(text.Substring(start, finish - start + 1))

    /// Collapse model-generated doubled object delimiters while preserving nested objects.
    let normalizeRedundantObjectDelimiters (text: string) =
        let trimmed = text.Trim()
        if not (trimmed.Contains("{{", StringComparison.Ordinal)) then trimmed
        else
            let output = StringBuilder(trimmed.Length)
            let mutable stack: (char * int) list = []
            let mutable index = 0
            let mutable inString = false
            let mutable quote = '\000'
            let mutable escaped = false
            let mutable invalid = false
            while index < trimmed.Length && not invalid do
                let character = trimmed[index]
                if inString then
                    output.Append(character) |> ignore
                    if escaped then escaped <- false
                    elif character = '\\' then escaped <- true
                    elif character = quote then inString <- false
                    index <- index + 1
                elif character = '"' || character = '\'' then
                    output.Append(character) |> ignore
                    inString <- true
                    quote <- character
                    index <- index + 1
                elif character = '{' then
                    let count = if index + 1 < trimmed.Length && trimmed[index + 1] = '{' then 2 else 1
                    output.Append('{') |> ignore
                    stack <- ('}', count) :: stack
                    index <- index + count
                elif character = '[' then
                    output.Append(character) |> ignore
                    stack <- (']', 1) :: stack
                    index <- index + 1
                elif character = '}' || character = ']' then
                    match stack with
                    | (expected, count) :: rest when expected = character ->
                        if count = 2 && (index + 1 >= trimmed.Length || trimmed[index + 1] <> character) then
                            invalid <- true
                        else
                            output.Append(character) |> ignore
                            stack <- rest
                            index <- index + count
                    | _ -> invalid <- true
                else
                    output.Append(character) |> ignore
                    index <- index + 1
            if invalid || inString || not stack.IsEmpty then trimmed else output.ToString()

    /// Complete unmatched object and array delimiters when their nesting is otherwise valid.
    let tryCompleteDelimiters (text: string) : string option =
        let start = text.IndexOf('{')
        if start < 0 then None
        else
            let candidate = text.Substring(start).Trim()
            let mutable stack = []
            let mutable inString = false
            let mutable quote = '\000'
            let mutable escaped = false
            let mutable invalid = false
            for character in candidate do
                if inString then
                    if escaped then escaped <- false
                    elif character = '\\' then escaped <- true
                    elif character = quote then inString <- false
                elif character = '"' || character = '\'' then
                    inString <- true
                    quote <- character
                elif character = '{' then stack <- '}' :: stack
                elif character = '[' then stack <- ']' :: stack
                elif character = '}' || character = ']' then
                    match stack with
                    | expected :: rest when expected = character -> stack <- rest
                    | _ -> invalid <- true
            if invalid || inString || List.isEmpty stack then None
            else Some(candidate + String(Array.ofList stack))

    /// Replace physical newlines inside double-quoted strings with spaces.
    let normalizeWrappedStrings (json: string) =
        let output = StringBuilder(json.Length)
        let mutable inString = false
        let mutable escaped = false
        for character in json do
            if inString then
                if escaped then
                    output.Append(character) |> ignore
                    escaped <- false
                elif character = '\\' then
                    output.Append(character) |> ignore
                    escaped <- true
                elif character = '"' then
                    output.Append(character) |> ignore
                    inString <- false
                elif character = '\r' || character = '\n' then
                    output.Append(' ') |> ignore
                else
                    output.Append(character) |> ignore
            else
                output.Append(character) |> ignore
                if character = '"' then inString <- true
        output.ToString()

    /// Recover arrays whose elements were emitted as property lists instead of objects.
    let normalizeObjectLikeArrays (json: string) =
        let nextNonWhitespace start =
            let mutable index = start
            while index < json.Length && Char.IsWhiteSpace(json[index]) do index <- index + 1
            if index < json.Length then Some json[index] else None

        let startsWithProperty start =
            let mutable index = start + 1
            while index < json.Length && Char.IsWhiteSpace(json[index]) do index <- index + 1
            if index >= json.Length || json[index] <> '"' then false
            else
                index <- index + 1
                let mutable escaped = false
                let mutable closingQuote = -1
                while index < json.Length && closingQuote < 0 do
                    if escaped then escaped <- false
                    elif json[index] = '\\' then escaped <- true
                    elif json[index] = '"' then closingQuote <- index
                    index <- index + 1
                while index < json.Length && Char.IsWhiteSpace(json[index]) do index <- index + 1
                closingQuote >= 0 && index < json.Length && json[index] = ':'

        let output = StringBuilder(json.Length)
        let mutable stack: (char * char) list = []
        let mutable inString = false
        let mutable escaped = false
        for index in 0 .. json.Length - 1 do
            let character = json[index]
            if inString then
                output.Append(character) |> ignore
                if escaped then escaped <- false
                elif character = '\\' then escaped <- true
                elif character = '"' then inString <- false
            else
                match character with
                | '"' ->
                    output.Append(character) |> ignore
                    inString <- true
                | '[' when startsWithProperty index ->
                    output.Append('{') |> ignore
                    stack <- (']', '}') :: stack
                | '[' ->
                    output.Append(character) |> ignore
                    stack <- (']', ']') :: stack
                | '{' ->
                    output.Append(character) |> ignore
                    stack <- ('}', '}') :: stack
                | ']' | '}' ->
                    match stack with
                    | (inputClose, outputClose) :: rest when character = inputClose || character = outputClose ->
                        output.Append(outputClose) |> ignore
                        stack <- rest
                    | _ -> output.Append(character) |> ignore
                | ',' when nextNonWhitespace (index + 1) = Some '{' ->
                    match stack with
                    | ('}', '}') :: (']', ']') :: rest ->
                        output.Append("},") |> ignore
                        stack <- (']', ']') :: rest
                    | _ -> output.Append(character) |> ignore
                | _ -> output.Append(character) |> ignore
        output.ToString()
