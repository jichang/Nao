namespace Nao.Documents

open System
open System.IO
open System.Collections.Generic
open System.Reflection
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open Microsoft.FSharp.Reflection

/// Reader/writer for the unified SignedByte document model. The current on-disk payload
/// is JSON, but callers should treat the `.signedbytedoc` file as an opaque document format.
module SignedByteDoc =

    [<Literal>]
    let MediaType = "application/vnd.signedbyte.document"

    [<Literal>]
    let Extension = ".signedbytedoc"

    let private mediaTypes = [ MediaType ]

    /// JSON serializer settings used by the native SignedByte document format.
    let JsonOptions =
        let options = JsonSerializerOptions(WriteIndented = false)
        options.Converters.Add(JsonFSharpConverter())
        options.Converters.Add(JsonStringEnumConverter())
        options

    let private schemaJsonOptions = JsonSerializerOptions(WriteIndented = false)

    let private jsonObject (properties: (string * JsonNode) list) =
        let result = JsonObject()

        for name, value in properties do
            result.Add(name, value)

        result :> JsonNode

    let private jsonArray (values: JsonNode list) =
        let result = JsonArray()

        for value in values do
            result.Add(value)

        result :> JsonNode

    /// Generates the schema for the shape emitted by FSharp.SystemTextJson.
    let private generateJsonSchema () =
        let definitions = JsonObject()
        let generating = HashSet<Type>()

        let typeName (t: Type) =
            (if isNull t.FullName then t.Name else t.FullName).Replace("+", ".")

        let reference (t: Type) =
            jsonObject [ "$ref", JsonValue.Create(sprintf "#/$defs/%s" (typeName t)) ]

        let rec schemaFor (t: Type) : JsonNode =
            if t = typeof<string> then
                jsonObject [ "type", JsonValue.Create "string" ]
            elif t = typeof<bool> then
                jsonObject [ "type", JsonValue.Create "boolean" ]
            elif
                t = typeof<byte>
                || t = typeof<sbyte>
                || t = typeof<int16>
                || t = typeof<uint16>
                || t = typeof<int>
                || t = typeof<uint32>
                || t = typeof<int64>
                || t = typeof<uint64>
            then
                jsonObject [ "type", JsonValue.Create "integer" ]
            elif t = typeof<float> || t = typeof<double> || t = typeof<decimal> then
                jsonObject [ "type", JsonValue.Create "number" ]
            elif t = typeof<DateTime> || t = typeof<DateTimeOffset> then
                jsonObject [ "type", JsonValue.Create "string"; "format", JsonValue.Create "date-time" ]
            elif t = typeof<byte[]> then
                jsonObject
                    [ "type", JsonValue.Create "string"
                      "contentEncoding", JsonValue.Create "base64" ]
            elif t.IsArray then
                jsonObject [ "type", JsonValue.Create "array"; "items", schemaFor (t.GetElementType()) ]
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>> then
                jsonObject
                    [ "anyOf",
                      jsonArray
                          [ schemaFor (t.GetGenericArguments().[0])
                            jsonObject [ "type", JsonValue.Create "null" ] ] ]
            elif
                t.IsGenericType
                && (t.GetGenericTypeDefinition() = typedefof<list<_>>
                    || t.GetGenericTypeDefinition() = typedefof<seq<_>>)
            then
                jsonObject
                    [ "type", JsonValue.Create "array"
                      "items", schemaFor (t.GetGenericArguments().[0]) ]
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<_, _>> then
                jsonObject
                    [ "type", JsonValue.Create "object"
                      "additionalProperties", schemaFor (t.GetGenericArguments().[1]) ]
            elif FSharpType.IsTuple t then
                let tupleElements = FSharpType.GetTupleElements t

                jsonObject
                    [ "type", JsonValue.Create "array"
                      "prefixItems", jsonArray (tupleElements |> Array.toList |> List.map schemaFor)
                      "minItems", JsonValue.Create tupleElements.Length
                      "maxItems", JsonValue.Create tupleElements.Length ]
            elif FSharpType.IsRecord t || FSharpType.IsUnion t then
                addDefinition t
                reference t
            else
                jsonObject [ "type", JsonValue.Create "object" ]

        and addDefinition (t: Type) =
            if not (definitions.ContainsKey(typeName t)) && generating.Add t then
                let definition =
                    if FSharpType.IsRecord t then
                        let fields = FSharpType.GetRecordFields t

                        jsonObject
                            [ "type", JsonValue.Create "object"
                              "properties",
                              jsonObject [ for field in fields -> field.Name, schemaFor field.PropertyType ]
                              "required", jsonArray [ for field in fields -> JsonValue.Create field.Name ]
                              "additionalProperties", JsonValue.Create false ]
                    else
                        let cases = FSharpType.GetUnionCases t

                        if cases.Length = 1 && cases.[0].GetFields().Length = 1 then
                            schemaFor (cases.[0].GetFields().[0].PropertyType)
                        else
                            let caseSchema (caseInfo: UnionCaseInfo) =
                                let fields = caseInfo.GetFields()
                                let properties = [ "Case", jsonObject [ "const", JsonValue.Create caseInfo.Name ] ]

                                if fields.Length = 0 then
                                    jsonObject
                                        [ "type", JsonValue.Create "object"
                                          "required", jsonArray [ JsonValue.Create "Case" ]
                                          "properties", jsonObject properties
                                          "additionalProperties", JsonValue.Create false ]
                                else
                                    jsonObject
                                        [ "type", JsonValue.Create "object"
                                          "required", jsonArray [ JsonValue.Create "Case"; JsonValue.Create "Fields" ]
                                          "properties",
                                          jsonObject (
                                              properties
                                              @ [ "Fields",
                                                  jsonObject
                                                      [ "type", JsonValue.Create "array"
                                                        "prefixItems",
                                                        jsonArray (
                                                            fields
                                                            |> Array.toList
                                                            |> List.map (fun field -> schemaFor field.PropertyType)
                                                        )
                                                        "minItems", JsonValue.Create fields.Length
                                                        "maxItems", JsonValue.Create fields.Length ] ]
                                          )
                                          "additionalProperties", JsonValue.Create false ]

                            jsonObject [ "oneOf", jsonArray (cases |> Array.toList |> List.map caseSchema) ]

                definitions.Add(typeName t, definition)
                generating.Remove t |> ignore

        schemaFor typeof<Document> |> ignore
        let documentDefinition = definitions.[typeName typeof<Document>].AsObject()

        documentDefinition.["properties"].AsObject().["Schema"] <-
            jsonObject [ "const", JsonValue.Create Document.CurrentSchema ]

        let result = documentDefinition.DeepClone().AsObject()
        result.Add("$schema", JsonValue.Create "https://json-schema.org/draft/2020-12/schema")
        result.Add("$id", JsonValue.Create "https://signedbyte.dev/schemas/document/1.json")
        result.Add("title", JsonValue.Create "SignedByte document")
        result.Add("$defs", definitions)
        result.ToJsonString(schemaJsonOptions)

    /// JSON Schema generated directly from the public document model types.
    let JsonSchema = generateJsonSchema ()

    /// Serialize a document to the native JSON representation.
    let serialize (doc: Document) : string =
        JsonSerializer.Serialize(doc, JsonOptions)

    /// Serialize a document to UTF-8 JSON bytes.
    let serializeBytes (doc: Document) : byte[] = serialize doc |> Encoding.UTF8.GetBytes

    /// Deserialize a document from the native JSON representation.
    let deserialize (json: string) : Document =
        JsonSerializer.Deserialize<Document>(json, JsonOptions)

    /// Deserialize a document from UTF-8 JSON bytes.
    let deserializeBytes (bytes: byte[]) : Document =
        deserialize (Encoding.UTF8.GetString bytes)

    /// Validate the model-level invariants required by the native document format.
    let validate (doc: Document) : string list =
        [ if doc.Schema <> Document.CurrentSchema then
              yield sprintf "Schema must be '%s'." Document.CurrentSchema
          if
              doc.Resources
              |> List.map (fun resource -> resource.Id)
              |> List.distinct
              |> List.length
              <> doc.Resources.Length
          then
              yield "Resources must not contain duplicate ids."
          match doc.Body with
          | Fluid _ -> ()
          | Paged pages ->
              if
                  pages
                  |> List.exists (fun page -> page.Setup.Width.Value < 0.0 || page.Setup.Height.Value < 0.0)
              then
                  yield "Paged page dimensions must not be negative." ]

    /// Return true when the document satisfies the native model invariants.
    let isValid (doc: Document) = validate doc |> List.isEmpty

    /// Parse and validate a native JSON document, returning readable errors.
    let validateJson (json: string) : string list =
        try
            deserialize json |> validate
        with ex ->
            [ ex.Message ]

    type Writer() =
        interface IDocumentWriter with
            member _.MediaTypes = mediaTypes

            member _.Write(doc, output, _ctx) =
                JsonSerializer.Serialize(output, doc, JsonOptions)

    type Reader() =
        interface IDocumentReader with
            member _.MediaTypes = mediaTypes

            member _.Read(input, _ctx) =
                JsonSerializer.Deserialize<Document>(input, JsonOptions)
