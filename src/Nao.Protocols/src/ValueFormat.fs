namespace Nao.Protocols

open System
open System.IO
open System.Text.Json

/// Structured failure produced while validating or normalizing a formatted value.
type ValueFormatError =
    { Summary: string
      Location: string option
      Details: Map<string, string> }

[<RequireQualifiedAccess>]
module ValueFormatError =
    let create summary =
        { Summary = summary
          Location = None
          Details = Map.empty }

    let format error =
        match error.Location with
        | Some location -> sprintf "%s Location: %s." error.Summary location
        | None -> error.Summary

/// Validates an LLM-produced value and normalizes it into a canonical representation.
type ValueFormat =
    { Name: string
      MediaType: string
      Normalize: string -> Result<string, ValueFormatError> }

[<RequireQualifiedAccess>]
module ValueFormat =
    let create name mediaType normalize : ValueFormat =
        { Name = name
          MediaType = mediaType
          Normalize = normalize }

[<RequireQualifiedAccess>]
module JsonValueFormat =
    let private strictError (ex: JsonException) =
        let location =
            match ex.LineNumber.HasValue, ex.BytePositionInLine.HasValue with
            | true, true -> Some(sprintf "line %d, byte %d" ex.LineNumber.Value ex.BytePositionInLine.Value)
            | true, false -> Some(sprintf "line %d" ex.LineNumber.Value)
            | _ -> None
        { Summary = sprintf "JSON syntax error: %s" ex.Message
          Location = location
          Details =
            if String.IsNullOrWhiteSpace ex.Path then Map.empty
            else Map.ofList [ "path", ex.Path ] }

    let private canonicalStrictJson (input: string) =
        try
            use document = JsonDocument.Parse(input)
            Ok(JsonSerializer.Serialize(document.RootElement))
        with :? JsonException as ex -> Error(strictError ex)

    /// Strict JSON input normalized to compact JSON.
    let strict =
        ValueFormat.create "strict-json" "application/json" canonicalStrictJson

    /// Common JSON5 syntax emitted by LLMs, normalized immediately to strict compact JSON.
    /// Supports identifier keys, single-quoted strings, comments, and trailing commas through
    /// Newtonsoft.Json's permissive reader. The normalized result must still pass System.Text.Json.
    let json5Compatible =
        let normalize input =
            try
                use textReader = new StringReader(input)
                use jsonReader = new Newtonsoft.Json.JsonTextReader(textReader)
                jsonReader.DateParseHandling <- Newtonsoft.Json.DateParseHandling.None
                let value = Newtonsoft.Json.Linq.JToken.ReadFrom(jsonReader)
                if jsonReader.Read() then
                    Error(ValueFormatError.create "Input must contain exactly one JSON5-compatible value.")
                else
                    value.ToString(Newtonsoft.Json.Formatting.None)
                    |> canonicalStrictJson
            with
            | :? Newtonsoft.Json.JsonReaderException as ex ->
                Error
                    { Summary = sprintf "JSON5-compatible syntax error: %s" ex.Message
                      Location = Some(sprintf "line %d, column %d" ex.LineNumber ex.LinePosition)
                      Details = Map.empty }
        ValueFormat.create "json5-compatible" "application/json" normalize
