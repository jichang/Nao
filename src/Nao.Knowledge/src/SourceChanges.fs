namespace Nao.Knowledge

open System
open System.IO

type internal SourceManifestItem =
    { ItemId: string
      RelativePath: string
      ContentHash: string
      ModifiedAt: DateTimeOffset
      ByteLength: int64 }

module internal SourceChanges =
    let detect
        (previousUri: string -> Uri)
        (previous: SourceManifestItem list)
        (current: SourceManifestItem list)
        (loaded: (SourceManifestItem * SourceContent) list)
        =
        let previousByPath =
            previous |> Seq.map (fun item -> item.RelativePath, item) |> Map.ofSeq

        let currentByPath =
            current |> Seq.map (fun item -> item.RelativePath, item) |> Map.ofSeq

        let removed =
            previous
            |> List.filter (fun item -> not (currentByPath.ContainsKey item.RelativePath))

        let added =
            loaded
            |> List.filter (fun (item, _) -> not (previousByPath.ContainsKey item.RelativePath))

        let uniqueRemovedByHash =
            removed
            |> List.groupBy _.ContentHash
            |> List.choose (fun (hash, items) ->
                match items with
                | [ item ] -> Some(hash, item)
                | _ -> None)
            |> Map.ofList

        let uniqueAddedHashes =
            added
            |> List.groupBy (fst >> _.ContentHash)
            |> List.choose (fun (hash, items) ->
                match items with
                | [ item ] -> Some(hash, item)
                | _ -> None)
            |> Map.ofList

        let movedHashes =
            uniqueAddedHashes
            |> Map.toSeq
            |> Seq.choose (fun (hash, _) ->
                if uniqueRemovedByHash.ContainsKey hash then
                    Some hash
                else
                    None)
            |> Set.ofSeq

        let moves, reconciledCurrent =
            (([], current), movedHashes)
            ||> Seq.fold (fun (moves, currentItems) hash ->
                let previousItem = uniqueRemovedByHash.[hash]
                let currentItem, content = uniqueAddedHashes.[hash]
                let stableId = previousItem.ItemId |> SourceItemId.parse

                let movedContent = { content with ItemId = stableId }

                let nextCurrent =
                    currentItems
                    |> List.map (fun item ->
                        if item.RelativePath = currentItem.RelativePath then
                            { item with
                                ItemId = previousItem.ItemId }
                        else
                            item)

                let move = SourceChange.Moved(previousUri previousItem.RelativePath, movedContent)

                move :: moves, nextCurrent)

        let changes =
            [ yield!
                  loaded
                  |> List.choose (fun (item, content) ->
                      match previousByPath |> Map.tryFind item.RelativePath with
                      | Some previousItem when previousItem.ContentHash = item.ContentHash -> None
                      | Some _ -> Some(SourceChange.Updated content)
                      | None when movedHashes.Contains item.ContentHash -> None
                      | None -> Some(SourceChange.Added content))
              yield! List.rev moves
              yield!
                  removed
                  |> List.choose (fun item ->
                      if movedHashes.Contains item.ContentHash then
                          None
                      else
                          Some(
                              SourceChange.Deleted(
                                  item.ItemId |> SourceItemId.parse,
                                  previousUri item.RelativePath,
                                  ContentHash.parse item.ContentHash
                              )
                          )) ]

        reconciledCurrent, changes
