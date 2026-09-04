namespace Nao.Persistence

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Nao.Agents

module InMemoryGraphMemory =
    let create (relationExtractor: (string -> Task<GraphRelation list>) option) : GraphMemory =
        let nodes = ConcurrentDictionary<string * string, GraphNode>()

        let relations =
            ConcurrentDictionary<string * string * string * string, GraphRelation>()

        let nodeKey owner nodeId = owner, nodeId
        let relationKey owner subject predicate object' = owner, subject, predicate, object'

        let requireOwner owner =
            if String.IsNullOrWhiteSpace owner then
                invalidArg (nameof owner) "Graph-memory owner cannot be blank."

        let ownedNodes owner =
            nodes.Values |> Seq.filter (fun node -> node.Owner = owner)

        let ownedRelations owner =
            relations.Values |> Seq.filter (fun relation -> relation.Owner = owner)

        let upsertNodeAsync (node: GraphNode) =
            requireOwner node.Owner

            nodes.AddOrUpdate(
                nodeKey node.Owner node.Id,
                node,
                fun _ existing ->
                    { node with
                        CreatedAt = existing.CreatedAt
                        AccessCount = existing.AccessCount + 1 }
            )
            |> ignore

            Task.FromResult()

        let addRelationAsync (relation: GraphRelation) =
            requireOwner relation.Owner

            relations.TryAdd(relationKey relation.Owner relation.Subject relation.Predicate relation.Object, relation)
            |> ignore

            Task.FromResult()

        let findPaths owner from' to' maxHops =
            let rec search frontier visited depth =
                if depth > maxHops || List.isEmpty frontier then
                    []
                else
                    let next = ResizeArray<string list>()
                    let found = ResizeArray<string list>()

                    for path in frontier do
                        let current = List.head path

                        if current = to' then
                            found.Add(List.rev path)
                        else
                            for relation in ownedRelations owner do
                                if relation.Subject = current && not (visited |> Set.contains relation.Object) then
                                    next.Add(relation.Object :: path)
                                elif relation.Object = current && not (visited |> Set.contains relation.Subject) then
                                    next.Add(relation.Subject :: path)

                    if found.Count > 0 then
                        found |> Seq.toList
                    else
                        let nextVisited = frontier |> List.map List.head |> Set.ofList |> Set.union visited
                        search (next |> Seq.toList) nextVisited (depth + 1)

            search [ [ from' ] ] (Set.singleton from') 0

        let queryAsync owner query =
            requireOwner owner
            let graphNodes = ownedNodes owner |> Seq.toList
            let graphRelations = ownedRelations owner |> Seq.toList

            let result =
                match query with
                | GraphQuery.ByEntity entity ->
                    let foundRelations =
                        graphRelations
                        |> List.filter (fun relation -> relation.Subject = entity || relation.Object = entity)

                    let ids =
                        foundRelations
                        |> List.collect (fun relation -> [ relation.Subject; relation.Object ])
                        |> Set.ofList

                    { Nodes = graphNodes |> List.filter (fun node -> ids.Contains node.Id)
                      Relations = foundRelations
                      PathLength = None }
                | GraphQuery.ByPredicate predicate ->
                    { Nodes = []
                      Relations = graphRelations |> List.filter (fun relation -> relation.Predicate = predicate)
                      PathLength = None }
                | GraphQuery.Path(from', to', maxHops) ->
                    match findPaths owner from' to' maxHops with
                    | [] ->
                        { Nodes = []
                          Relations = []
                          PathLength = None }
                    | shortest :: _ ->
                        let ids = Set.ofList shortest

                        { Nodes = graphNodes |> List.filter (fun node -> ids.Contains node.Id)
                          Relations = []
                          PathLength = Some(shortest.Length - 1) }
                | GraphQuery.ByProperties filters ->
                    let matches node =
                        filters
                        |> List.forall (fun (key, value) ->
                            node.Properties
                            |> Map.tryFind key
                            |> Option.exists (fun property ->
                                property.Contains(value, StringComparison.OrdinalIgnoreCase)))

                    { Nodes = graphNodes |> List.filter matches
                      Relations = []
                      PathLength = None }
                | GraphQuery.Neighborhood(entity, hops) ->
                    let rec collect frontier visited depth =
                        if depth >= hops then
                            visited
                        else
                            let neighbors =
                                graphRelations
                                |> List.collect (fun relation ->
                                    [ if frontier |> Set.contains relation.Subject then
                                          relation.Object
                                      if frontier |> Set.contains relation.Object then
                                          relation.Subject ])
                                |> Set.ofList
                                |> Set.difference visited

                            collect neighbors (Set.union visited neighbors) (depth + 1)

                    let ids = collect (Set.singleton entity) (Set.singleton entity) 0

                    { Nodes = graphNodes |> List.filter (fun node -> ids.Contains node.Id)
                      Relations =
                        graphRelations
                        |> List.filter (fun relation -> ids.Contains relation.Subject && ids.Contains relation.Object)
                      PathLength = None }

            Task.FromResult result

        let removeRelation owner relation =
            relations.TryRemove(relationKey owner relation.Subject relation.Predicate relation.Object)
            |> ignore

        let removeNodeAsync owner nodeId =
            requireOwner owner
            nodes.TryRemove(nodeKey owner nodeId) |> ignore

            ownedRelations owner
            |> Seq.filter (fun relation -> relation.Subject = nodeId || relation.Object = nodeId)
            |> Seq.toArray
            |> Array.iter (removeRelation owner)

            Task.FromResult()

        let removeRelationAsync owner subject predicate object' =
            requireOwner owner
            relations.TryRemove(relationKey owner subject predicate object') |> ignore
            Task.FromResult()

        let getByTypeAsync owner entityType =
            requireOwner owner

            ownedNodes owner
            |> Seq.filter (fun node -> node.EntityType = entityType)
            |> Seq.toList
            |> Task.FromResult

        let extractRelationsAsync owner text =
            requireOwner owner

            task {
                let! extracted =
                    match relationExtractor with
                    | Some extractor -> extractor text
                    | None ->
                        let patterns =
                            [ "is a"
                              "is an"
                              "has"
                              "contains"
                              "uses"
                              "depends on"
                              "implements"
                              "extends" ]

                        let found =
                            text.Split([| '.'; '!'; '?' |], StringSplitOptions.RemoveEmptyEntries)
                            |> Array.toList
                            |> List.collect (fun sentence ->
                                patterns
                                |> List.choose (fun pattern ->
                                    let trimmed = sentence.Trim()
                                    let index = trimmed.IndexOf(pattern, StringComparison.OrdinalIgnoreCase)

                                    if index <= 0 then
                                        None
                                    else
                                        let subject = trimmed.Substring(0, index).Trim()
                                        let object' = trimmed.Substring(index + pattern.Length).Trim()

                                        if
                                            subject.Length = 0
                                            || subject.Length >= 100
                                            || object'.Length = 0
                                            || object'.Length >= 100
                                        then
                                            None
                                        else
                                            Some
                                                { Owner = owner
                                                  Subject = subject
                                                  Predicate = pattern
                                                  Object = object'
                                                  Confidence = 0.5
                                                  Source = Some "pattern-extraction"
                                                  Timestamp = DateTimeOffset.UtcNow
                                                  Metadata = Map.empty }))

                        Task.FromResult found

                if extracted |> List.exists (fun relation -> relation.Owner <> owner) then
                    invalidArg (nameof owner) "Extracted graph relations must belong to the requested owner."

                for relation in extracted do
                    do! addRelationAsync relation

                return extracted
            }

        let delete predicate =
            let nodeMatches =
                nodes.Values
                |> Seq.filter (fun node -> predicate (Choice1Of2 node))
                |> Seq.toArray

            let deletedNodeIds =
                nodeMatches |> Array.map (fun node -> node.Owner, node.Id) |> Set.ofArray

            let relationMatches =
                relations.Values
                |> Seq.filter (fun relation ->
                    predicate (Choice2Of2 relation)
                    || deletedNodeIds.Contains(relation.Owner, relation.Subject)
                    || deletedNodeIds.Contains(relation.Owner, relation.Object))
                |> Seq.toArray

            nodeMatches
            |> Array.iter (fun node -> nodes.TryRemove(nodeKey node.Owner node.Id) |> ignore)

            relationMatches
            |> Array.iter (fun relation -> removeRelation relation.Owner relation)

            Task.FromResult(nodeMatches.Length + relationMatches.Length)

        let protect owner operation =
            task {
                if String.IsNullOrWhiteSpace owner then
                    return
                        Error(
                            PlatformFailure.create
                                PlatformErrorCategory.InvalidInput
                                "Graph-memory owner cannot be blank."
                                false
                                None
                        )
                else
                    try
                        let! count = operation ()
                        return Ok count
                    with error ->
                        return Error(PlatformFailure.fromException PlatformFailureBoundary.Storage None error)
            }

        let deleteOwnerAsync owner =
            protect owner (fun () ->
                delete (function
                    | Choice1Of2 node -> node.Owner = owner
                    | Choice2Of2 relation -> relation.Owner = owner))

        let deleteExpiredAsync owner before =
            protect owner (fun () ->
                delete (function
                    | Choice1Of2 node -> node.Owner = owner && node.CreatedAt < before
                    | Choice2Of2 relation -> relation.Owner = owner && relation.Timestamp < before))

        { UpsertNodeAsync = upsertNodeAsync
          AddRelationAsync = addRelationAsync
          QueryAsync = queryAsync
          RemoveNodeAsync = removeNodeAsync
          RemoveRelationAsync = removeRelationAsync
          GetByTypeAsync = getByTypeAsync
          ExtractRelationsAsync = extractRelationsAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }
