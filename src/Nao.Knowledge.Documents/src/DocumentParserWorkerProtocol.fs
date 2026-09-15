namespace Nao.Knowledge.Documents

open System.Text.Json
open System.Text.Json.Serialization
open Nao.Knowledge
open Nao.Documents

type DocumentParserWorkerRequest =
    { Schema: string
      ParserId: string
      FileName: string option
      Content: byte array
      GroupId: string option
      WorkspaceId: string
      SessionId: string option
      PackageReadLimits: PackageReadLimits }

type DocumentParserWorkerResponse =
    { Schema: string
      Outcome: Result<ParsedKnowledgeDocument, ParserRejection> }

[<RequireQualifiedAccess>]
module DocumentParserWorkerProtocol =
    [<Literal>]
    let CurrentSchema = "nao-document-parser/v1"

    [<Literal>]
    let WorkerId = "knowledge-document-parser"

    let private options =
        let value = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        value.Converters.Add(JsonFSharpConverter())
        value.Converters.Add(JsonStringEnumConverter())
        value

    let serializeRequest request =
        JsonSerializer.Serialize(request, options)

    let deserializeRequest (value: string) =
        let request =
            JsonSerializer.Deserialize<DocumentParserWorkerRequest>(value, options)

        if isNull (box request) || request.Schema <> CurrentSchema then
            invalidArg (nameof value) "Document parser request uses an unsupported schema."

        request

    let serializeResponse response =
        JsonSerializer.Serialize(
            { Schema = CurrentSchema
              Outcome = response },
            options
        )

    let deserializeResponse (value: string) =
        let response =
            JsonSerializer.Deserialize<DocumentParserWorkerResponse>(value, options)

        if isNull (box response) || response.Schema <> CurrentSchema then
            invalidArg (nameof value) "Document parser response uses an unsupported schema."

        response.Outcome
