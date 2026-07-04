namespace Nao.Persistence

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Nao.Agents

/// In-memory knowledge graph implementation
type InMemoryGraphMemory(?relationExtractor: string -> Task<GraphRelation list>) =
    let nodes = ConcurrentDictionary<string, GraphNode>()
    let relations = ConcurrentBag<GraphRelation>()

    let getRelations () = relations |> Seq.toList

    let findPaths (from': string) (to': string) (maxHops: int) =
        let rec bfs (frontier: string list list) (visited: Set<string>) (depth: int) =
            if depth > maxHops || frontier.IsEmpty then []
            else
                let nextFrontier = ResizeArray<string list>()
                let mutable found = []
                for path in frontier do
                    let current = List.head path
                    if current = to' then
                        found <- (List.rev path) :: found
                    else
                        let neighbors =
                            getRelations ()
                            |> List.collect (fun r ->
                                if r.Subject = current && not (Set.contains r.Object visited) then [r.Object]
                                elif r.Object = current && not (Set.contains r.Subject visited) then [r.Subject]
                                else [])
                        for n in neighbors do
                            nextFrontier.Add(n :: path)
                if not found.IsEmpty then found
                else
                    let newVisited = frontier |> List.map List.head |> Set.ofList |> Set.union visited
                    bfs (nextFrontier |> Seq.toList) newVisited (depth + 1)
        bfs [[from']] (Set.singleton from') 0

    interface IGraphMemory with
        member _.UpsertNodeAsync(node: GraphNode) =
            nodes.AddOrUpdate(node.Id, node, fun _ existing ->
                { node with
                    AccessCount = existing.AccessCount + 1
                    CreatedAt = existing.CreatedAt }) |> ignore
            task { return () }

        member _.AddRelationAsync(relation: GraphRelation) =
            // Deduplicate
            let exists =
                getRelations ()
                |> List.exists (fun r ->
                    r.Subject = relation.Subject &&
                    r.Predicate = relation.Predicate &&
                    r.Object = relation.Object)
            if not exists then
                relations.Add(relation)
            task { return () }

        member _.QueryAsync(query: GraphQuery) =
            let result =
                match query with
                | GraphQuery.ByEntity entity ->
                    let rels =
                        getRelations ()
                        |> List.filter (fun r -> r.Subject = entity || r.Object = entity)
                    let nodeIds =
                        rels
                        |> List.collect (fun r -> [r.Subject; r.Object])
                        |> List.distinct
                    let foundNodes =
                        nodeIds
                        |> List.choose (fun id ->
                            match nodes.TryGetValue(id) with
                            | true, n -> Some n
                            | _ -> None)
                    { Nodes = foundNodes; Relations = rels; PathLength = None }

                | GraphQuery.ByPredicate predicate ->
                    let rels =
                        getRelations ()
                        |> List.filter (fun r -> r.Predicate = predicate)
                    { Nodes = []; Relations = rels; PathLength = None }

                | GraphQuery.Path (from', to', maxHops) ->
                    let paths = findPaths from' to' maxHops
                    match paths with
                    | [] -> { Nodes = []; Relations = []; PathLength = None }
                    | shortest :: _ ->
                        let pathNodes =
                            shortest
                            |> List.choose (fun id ->
                                match nodes.TryGetValue(id) with
                                | true, n -> Some n
                                | _ -> None)
                        { Nodes = pathNodes; Relations = []; PathLength = Some (shortest.Length - 1) }

                | GraphQuery.ByProperties filters ->
                    let matching =
                        nodes.Values
                        |> Seq.filter (fun n ->
                            filters |> List.forall (fun (k, v) ->
                                match n.Properties |> Map.tryFind k with
                                | Some pv -> pv.Contains(v, StringComparison.OrdinalIgnoreCase)
                                | None -> false))
                        |> Seq.toList
                    { Nodes = matching; Relations = []; PathLength = None }

                | GraphQuery.Neighborhood (entity, hops) ->
                    let rec collect (frontier: Set<string>) (visited: Set<string>) (depth: int) =
                        if depth >= hops then visited
                        else
                            let neighbors =
                                getRelations ()
                                |> List.collect (fun r ->
                                    [ if frontier.Contains r.Subject && not (visited.Contains r.Object) then r.Object
                                      if frontier.Contains r.Object && not (visited.Contains r.Subject) then r.Subject ])
                                |> Set.ofList
                            collect neighbors (Set.union visited neighbors) (depth + 1)
                    let neighborhood = collect (Set.singleton entity) (Set.singleton entity) 0
                    let foundNodes =
                        neighborhood
                        |> Set.toList
                        |> List.choose (fun id ->
                            match nodes.TryGetValue(id) with
                            | true, n -> Some n
                            | _ -> None)
                    let rels =
                        getRelations ()
                        |> List.filter (fun r ->
                            neighborhood.Contains r.Subject && neighborhood.Contains r.Object)
                    { Nodes = foundNodes; Relations = rels; PathLength = None }
            Task.FromResult result

        member _.RemoveNodeAsync(nodeId: string) =
            nodes.TryRemove(nodeId) |> ignore
            // Note: ConcurrentBag doesn't support removal, so we rebuild
            // In production, use a proper data structure
            task { return () }

        member _.RemoveRelationAsync (subject: string) (predicate: string) (object': string) =
            // ConcurrentBag limitation - in production use a different structure
            task { return () }

        member _.GetByTypeAsync(entityType: string) =
            nodes.Values
            |> Seq.filter (fun n -> n.EntityType = entityType)
            |> Seq.toList
            |> Task.FromResult

        member _.ExtractRelationsAsync(text: string) =
            task {
                match relationExtractor with
                | Some extractor ->
                    let! extracted = extractor text
                    for rel in extracted do
                        relations.Add(rel)
                    return extracted
                | None ->
                    // Simple pattern-based extraction: "X is Y", "X has Y"
                    let patterns =
                        [ "is a"; "is an"; "has"; "contains"; "uses"; "depends on"; "implements"; "extends" ]
                    let results = ResizeArray<GraphRelation>()
                    let sentences = text.Split([|'.'; '!'; '?'|], StringSplitOptions.RemoveEmptyEntries)
                    for sentence in sentences do
                        let trimmed = sentence.Trim()
                        for pattern in patterns do
                            let idx = trimmed.IndexOf(pattern, StringComparison.OrdinalIgnoreCase)
                            if idx > 0 then
                                let subject = trimmed.Substring(0, idx).Trim()
                                let obj = trimmed.Substring(idx + pattern.Length).Trim()
                                if subject.Length > 0 && subject.Length < 100 && obj.Length > 0 && obj.Length < 100 then
                                    let rel =
                                        { Subject = subject
                                          Predicate = pattern
                                          Object = obj
                                          Confidence = 0.5
                                          Source = Some "pattern-extraction"
                                          Timestamp = DateTimeOffset.UtcNow
                                          Metadata = Map.empty }
                                    results.Add(rel)
                                    relations.Add(rel)
                    return results |> Seq.toList
            }
