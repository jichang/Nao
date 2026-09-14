namespace Nao.Knowledge

open System
open System.IO
open System.Text
open System.Text.Json

module internal FilesystemSourceCheckpoint =
    let connector = ComponentVersion.create "nao.filesystem" "1"

    let serialize items =
        use stream = new MemoryStream()

        use writer =
            new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false, SkipValidation = false))

        writer.WriteStartObject()
        writer.WriteNumber("Version", 1)
        writer.WriteStartArray("Items")

        for item in items |> List.sortBy _.RelativePath do
            writer.WriteStartObject()
            writer.WriteString("ItemId", item.ItemId)
            writer.WriteString("RelativePath", item.RelativePath)
            writer.WriteString("ContentHash", item.ContentHash)
            writer.WriteString("ModifiedAt", item.ModifiedAt)
            writer.WriteNumber("ByteLength", item.ByteLength)
            writer.WriteEndObject()

        writer.WriteEndArray()
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
            invalidArg (nameof checkpoint) "Unsupported or malformed filesystem checkpoint."

        let items =
            root.GetProperty("Items").EnumerateArray()
            |> Seq.map (fun item ->
                let itemId = item.GetProperty("ItemId").GetString() |> SourceItemId.parse
                let relativePath = item.GetProperty("RelativePath").GetString()
                let contentHash = item.GetProperty("ContentHash").GetString() |> ContentHash.parse
                let byteLength = item.GetProperty("ByteLength").GetInt64()

                if
                    String.IsNullOrWhiteSpace relativePath
                    || relativePath <> relativePath.Trim()
                    || relativePath.Contains('\\')
                    || Path.IsPathRooted relativePath
                    || relativePath.Split('/')
                       |> Array.exists (fun segment -> segment = "." || segment = "..")
                then
                    invalidArg (nameof checkpoint) "Checkpoint contains a non-canonical relative path."

                if byteLength < 0L then
                    invalidArg (nameof checkpoint) "Checkpoint contains a negative content length."

                { ItemId = SourceItemId.value itemId
                  RelativePath = relativePath
                  ContentHash = ContentHash.value contentHash
                  ModifiedAt = item.GetProperty("ModifiedAt").GetDateTimeOffset()
                  ByteLength = byteLength })
            |> List.ofSeq

        if
            (items |> List.distinctBy _.RelativePath |> List.length) <> items.Length
            || (items |> List.distinctBy _.ItemId |> List.length) <> items.Length
        then
            invalidArg (nameof checkpoint) "Checkpoint contains duplicate paths or item identifiers."

        items
