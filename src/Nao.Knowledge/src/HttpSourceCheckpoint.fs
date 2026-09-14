namespace Nao.Knowledge

open System
open System.IO
open System.Text
open System.Text.Json

type internal HttpSourceState =
    { ItemId: SourceItemId
      Uri: Uri
      ContentHash: ContentHash
      ETag: string option
      LastModified: DateTimeOffset option }

module internal HttpSourceCheckpoint =
    let connector = ComponentVersion.create "nao.http" "1"

    let serialize state =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("Version", 1)
        writer.WriteString("ItemId", SourceItemId.value state.ItemId)
        writer.WriteString("Uri", state.Uri.AbsoluteUri)
        writer.WriteString("ContentHash", ContentHash.value state.ContentHash)

        match state.ETag with
        | Some etag -> writer.WriteString("ETag", etag)
        | None -> writer.WriteNull("ETag")

        match state.LastModified with
        | Some lastModified -> writer.WriteString("LastModified", lastModified)
        | None -> writer.WriteNull("LastModified")

        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray() |> Encoding.UTF8.GetString

    let deserialize checkpoint =
        if SourceConnectorCheckpoint.connector checkpoint <> connector then
            invalidArg (nameof checkpoint) "Checkpoint belongs to another connector or connector version."

        let cursor = SourceConnectorCheckpoint.cursor checkpoint

        if ContentHash.computeText cursor <> SourceConnectorCheckpoint.stateHash checkpoint then
            invalidArg (nameof checkpoint) "Connector checkpoint hash is invalid."

        use document = JsonDocument.Parse(cursor)
        let root = document.RootElement

        if root.GetProperty("Version").GetInt32() <> 1 then
            invalidArg (nameof checkpoint) "Unsupported HTTP checkpoint version."

        let uri = root.GetProperty("Uri").GetString() |> Uri

        if
            not uri.IsAbsoluteUri
            || (uri.Scheme <> Uri.UriSchemeHttp && uri.Scheme <> Uri.UriSchemeHttps)
            || not (String.IsNullOrEmpty uri.UserInfo)
            || not (String.IsNullOrEmpty uri.Fragment)
        then
            invalidArg (nameof checkpoint) "HTTP checkpoint contains an invalid URI."

        let optionalString (name: string) =
            let value = root.GetProperty(name)

            if value.ValueKind = JsonValueKind.Null then
                None
            else
                value.GetString() |> Some

        let optionalDate (name: string) =
            let value = root.GetProperty(name)

            if value.ValueKind = JsonValueKind.Null then
                None
            else
                value.GetDateTimeOffset() |> Some

        { ItemId = root.GetProperty("ItemId").GetString() |> SourceItemId.parse
          Uri = uri
          ContentHash = root.GetProperty("ContentHash").GetString() |> ContentHash.parse
          ETag = optionalString "ETag"
          LastModified = optionalDate "LastModified" }
