namespace Nao.DeveloperTools

open System
open System.IO
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
type DeveloperIssueSeverity =
    | Error
    | Warning
    | Information

type DeveloperIssue =
    { Code: string
      Severity: DeveloperIssueSeverity
      Location: string option
      Detail: string }

type DeveloperCommandResult =
    { SchemaVersion: int
      Command: string
      Succeeded: bool
      Data: Map<string, string>
      Issues: DeveloperIssue list }

[<RequireQualifiedAccess>]
module DeveloperCommandResult =
    [<Literal>]
    let CurrentSchemaVersion = 1

    let create command data issues =
        let succeeded =
            issues
            |> List.exists (fun issue -> issue.Severity = DeveloperIssueSeverity.Error)
            |> not

        { SchemaVersion = CurrentSchemaVersion
          Command = command
          Succeeded = succeeded
          Data = data
          Issues = issues }

    let success command data = create command data []

    let failure command code detail =
        create
            command
            Map.empty
            [ { Code = code
                Severity = DeveloperIssueSeverity.Error
                Location = None
                Detail = detail } ]

    let private severity =
        function
        | DeveloperIssueSeverity.Error -> "error"
        | DeveloperIssueSeverity.Warning -> "warning"
        | DeveloperIssueSeverity.Information -> "information"

    let serialize value =
        use stream = new MemoryStream()

        use writer =
            new Utf8JsonWriter(
                stream,
                JsonWriterOptions(
                    Indented = true,
                    Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                )
            )

        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", value.SchemaVersion)
        writer.WriteString("command", value.Command)
        writer.WriteBoolean("succeeded", value.Succeeded)
        writer.WriteStartObject("data")

        value.Data
        |> Map.iter (fun name fieldValue -> writer.WriteString(name, fieldValue))

        writer.WriteEndObject()
        writer.WriteStartArray("issues")

        for issue in value.Issues do
            writer.WriteStartObject()
            writer.WriteString("code", issue.Code)
            writer.WriteString("severity", severity issue.Severity)

            issue.Location
            |> Option.iter (fun location -> writer.WriteString("location", location))

            writer.WriteString("detail", issue.Detail)
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

[<RequireQualifiedAccess>]
module DeveloperIssue =
    let error code location detail =
        { Code = code
          Severity = DeveloperIssueSeverity.Error
          Location = location
          Detail = detail }

    let warning code location detail =
        { Code = code
          Severity = DeveloperIssueSeverity.Warning
          Location = location
          Detail = detail }
