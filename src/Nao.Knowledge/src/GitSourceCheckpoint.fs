namespace Nao.Knowledge

open System
open System.IO
open System.Text
open System.Text.Json

type internal GitSourceState =
    { CommitId: GitCommitId
      Items: SourceManifestItem list }

module internal GitSourceCheckpoint =
    let connector = ComponentVersion.create "nao.git" "1"

    let serialize state =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("Version", 1)
        writer.WriteString("CommitId", GitCommitId.value state.CommitId)
        writer.WriteStartArray("Items")

        for item in state.Items |> List.sortBy _.RelativePath do
            writer.WriteStartObject()
            writer.WriteString("ItemId", item.ItemId)
            writer.WriteString("Path", item.RelativePath)
            writer.WriteString("ContentHash", item.ContentHash)
            writer.WriteString("CommittedAt", item.ModifiedAt)
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
            invalidArg (nameof checkpoint) "Unsupported Git checkpoint version."

        let items =
            root.GetProperty("Items").EnumerateArray()
            |> Seq.map (fun item ->
                let path = item.GetProperty("Path").GetString()
                let byteLength = item.GetProperty("ByteLength").GetInt64()

                if
                    String.IsNullOrWhiteSpace path
                    || path <> path.Trim()
                    || path.Contains('\\')
                    || Path.IsPathRooted path
                    || path.Split('/')
                       |> Array.exists (fun segment -> segment = "." || segment = ".." || segment = ".git")
                then
                    invalidArg (nameof checkpoint) "Git checkpoint contains a non-canonical path."

                if byteLength < 0L then
                    invalidArg (nameof checkpoint) "Git checkpoint contains a negative blob length."

                { ItemId =
                    item.GetProperty("ItemId").GetString()
                    |> SourceItemId.parse
                    |> SourceItemId.value
                  RelativePath = path
                  ContentHash =
                    item.GetProperty("ContentHash").GetString()
                    |> ContentHash.parse
                    |> ContentHash.value
                  ModifiedAt = item.GetProperty("CommittedAt").GetDateTimeOffset()
                  ByteLength = byteLength })
            |> List.ofSeq

        if
            (items |> List.distinctBy _.RelativePath |> List.length) <> items.Length
            || (items |> List.distinctBy _.ItemId |> List.length) <> items.Length
        then
            invalidArg (nameof checkpoint) "Git checkpoint contains duplicate paths or item identifiers."

        { CommitId = root.GetProperty("CommitId").GetString() |> GitCommitId.parse
          Items = items }
