namespace Nao.Persistence

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Nao.Agents

module InMemoryGraphMemory =
    let create (relationExtractor: (string -> Task<GraphExtraction>) option) : GraphMemory =
        let sync = obj ()
        let nodes = ConcurrentDictionary<string * string, GraphNode>()

        let relations = ConcurrentDictionary<string * string, GraphRelation>()

        let entityTypeIndex =
            ConcurrentDictionary<string * string, ConcurrentDictionary<string, byte>>()

        let aliasIndex =
            ConcurrentDictionary<string * string, ConcurrentDictionary<string, byte>>()

        let propertyIndex =
            ConcurrentDictionary<string * string * string, ConcurrentDictionary<string, byte>>()

        let predicateIndex =
            ConcurrentDictionary<string * string, ConcurrentDictionary<string, byte>>()

        let outgoingIndex =
            ConcurrentDictionary<string * string, ConcurrentDictionary<string, byte>>()

        let incomingIndex =
            ConcurrentDictionary<string * string, ConcurrentDictionary<string, byte>>()

        let nodeKey owner nodeId = owner, nodeId
        let relationKey owner relationId = owner, relationId

        let ownerKey = AuthorizationScope.identity >> AuthorizationIdentity.storageKey

        let addIndex (index: ConcurrentDictionary<'key, ConcurrentDictionary<string, byte>>) key value =
            index.GetOrAdd(key, fun _ -> ConcurrentDictionary()).TryAdd(value, 0uy)
            |> ignore

        let removeIndex (index: ConcurrentDictionary<'key, ConcurrentDictionary<string, byte>>) key value =
            match index.TryGetValue key with
            | true, values ->
                let mutable removed = 0uy
                values.TryRemove(value, &removed) |> ignore

                if values.IsEmpty then
                    let mutable removedValues = Unchecked.defaultof<ConcurrentDictionary<string, byte>>
                    index.TryRemove(key, &removedValues) |> ignore
            | _ -> ()

        let indexNode owner (node: GraphNode) =
            addIndex entityTypeIndex (owner, node.EntityType) node.Id

            node.Aliases
            |> Set.iter (fun alias -> addIndex aliasIndex (owner, alias) node.Id)

            node.Properties
            |> Map.iter (fun name value -> addIndex propertyIndex (owner, name, value) node.Id)

        let unindexNode owner (node: GraphNode) =
            removeIndex entityTypeIndex (owner, node.EntityType) node.Id

            node.Aliases
            |> Set.iter (fun alias -> removeIndex aliasIndex (owner, alias) node.Id)

            node.Properties
            |> Map.iter (fun name value -> removeIndex propertyIndex (owner, name, value) node.Id)

        let indexRelation owner (relation: GraphRelation) =
            addIndex predicateIndex (owner, relation.Predicate) relation.Id
            addIndex outgoingIndex (owner, relation.Subject) relation.Id
            addIndex incomingIndex (owner, relation.Object) relation.Id

            if relation.Direction = GraphRelationDirection.Undirected then
                addIndex outgoingIndex (owner, relation.Object) relation.Id
                addIndex incomingIndex (owner, relation.Subject) relation.Id

        let unindexRelation owner (relation: GraphRelation) =
            removeIndex predicateIndex (owner, relation.Predicate) relation.Id
            removeIndex outgoingIndex (owner, relation.Subject) relation.Id
            removeIndex incomingIndex (owner, relation.Object) relation.Id

            if relation.Direction = GraphRelationDirection.Undirected then
                removeIndex outgoingIndex (owner, relation.Object) relation.Id
                removeIndex incomingIndex (owner, relation.Subject) relation.Id

        let indexedValues (index: ConcurrentDictionary<'key, ConcurrentDictionary<string, byte>>) key =
            match index.TryGetValue key with
            | true, values -> values.Keys |> Set.ofSeq
            | _ -> Set.empty

        let ownedNodes owner =
            nodes
            |> Seq.choose (fun pair -> if fst pair.Key = owner then Some pair.Value else None)

        let ownedRelations owner =
            relations
            |> Seq.choose (fun pair ->
                let partition, _ = pair.Key
                if partition = owner then Some pair.Value else None)

        let requireIdentityAvailability owner excludedIds (node: GraphNode) =
            let conflictsWithNode identity =
                match nodes.TryGetValue(nodeKey owner identity) with
                | true, existing -> not (excludedIds |> Set.contains existing.Id)
                | _ -> false

            let conflictsWithAlias identity =
                indexedValues aliasIndex (owner, identity)
                |> Set.exists (fun id -> not (excludedIds |> Set.contains id))

            if conflictsWithAlias node.Id then
                invalidArg (nameof node) $"Entity identity '{node.Id}' is already used as an alias."

            for alias in node.Aliases do
                if conflictsWithNode alias || conflictsWithAlias alias then
                    invalidArg (nameof node) $"Entity alias '{alias}' is already assigned."

        let storeNode owner (node: GraphNode) =
            let stored =
                match nodes.TryGetValue(nodeKey owner node.Id) with
                | true, existing ->
                    unindexNode owner existing

                    { node with
                        CreatedAt = existing.CreatedAt
                        Provenance = (existing.Provenance @ node.Provenance) |> List.distinct }
                | _ -> node

            nodes.[nodeKey owner node.Id] <- stored
            indexNode owner stored

        let replaceRelation owner (relation: GraphRelation) =
            let existing = relations.[relationKey owner relation.Id]
            unindexRelation owner existing
            relations.[relationKey owner relation.Id] <- relation
            indexRelation owner relation

        let upsertNodeAsync scope (node: GraphNode) =
            GraphContracts.validateNode node
            let owner = ownerKey scope
            requireIdentityAvailability owner (Set.singleton node.Id) node
            storeNode owner node

            Task.FromResult()

        let addRelationAsync scope (relation: GraphRelation) =
            GraphContracts.validateRelation relation
            let owner = ownerKey scope

            if not (nodes.ContainsKey(nodeKey owner relation.Subject)) then
                invalidArg (nameof relation) $"Relation subject '{relation.Subject}' does not exist."

            if not (nodes.ContainsKey(nodeKey owner relation.Object)) then
                invalidArg (nameof relation) $"Relation object '{relation.Object}' does not exist."

            if relations.TryAdd(relationKey owner relation.Id, relation) then
                indexRelation owner relation
            else
                let existing = relations.[relationKey owner relation.Id]

                if existing <> relation then
                    invalidArg
                        (nameof relation)
                        $"Relation identity '{relation.Id}' is already bound to another assertion."

            Task.FromResult()

        let findPaths owner from' to' maxHops =
            let adjacent entity =
                indexedValues outgoingIndex (owner, entity)
                |> Seq.choose (fun id ->
                    match relations.TryGetValue(relationKey owner id) with
                    | true, relation -> Some relation
                    | _ -> None)

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
                            for relation in adjacent current do
                                if relation.Subject = current && not (visited |> Set.contains relation.Object) then
                                    next.Add(relation.Object :: path)
                                elif
                                    relation.Direction = GraphRelationDirection.Undirected
                                    && relation.Object = current
                                    && not (visited |> Set.contains relation.Subject)
                                then
                                    next.Add(relation.Subject :: path)

                    if found.Count > 0 then
                        found |> Seq.toList
                    else
                        let nextVisited = frontier |> List.map List.head |> Set.ofList |> Set.union visited
                        search (next |> Seq.toList) nextVisited (depth + 1)

            search [ [ from' ] ] (Set.singleton from') 0

        let queryAsync scope query =
            let owner = ownerKey scope
            let graphNodes = ownedNodes owner |> Seq.toList
            let graphRelations = ownedRelations owner |> Seq.toList

            let result =
                match query with
                | GraphQuery.ByEntity entity ->
                    let relationIds =
                        Set.union
                            (indexedValues outgoingIndex (owner, entity))
                            (indexedValues incomingIndex (owner, entity))

                    let foundRelations =
                        graphRelations |> List.filter (fun relation -> relationIds.Contains relation.Id)

                    let ids =
                        foundRelations
                        |> List.collect (fun relation -> [ relation.Subject; relation.Object ])
                        |> Set.ofList

                    { Nodes = graphNodes |> List.filter (fun node -> ids.Contains node.Id)
                      Relations = foundRelations
                      PathLength = None }
                | GraphQuery.ByAlias alias ->
                    let ids = indexedValues aliasIndex (owner, alias)

                    { Nodes = graphNodes |> List.filter (fun node -> ids.Contains node.Id)
                      Relations = []
                      PathLength = None }
                | GraphQuery.ByPredicate predicate ->
                    let ids = indexedValues predicateIndex (owner, predicate)

                    { Nodes = []
                      Relations = graphRelations |> List.filter (fun relation -> ids.Contains relation.Id)
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
                    let ids =
                        filters
                        |> List.map (fun (name, value) -> indexedValues propertyIndex (owner, name, value))
                        |> function
                            | [] -> Set.empty
                            | first :: rest -> rest |> List.fold Set.intersect first

                    { Nodes = graphNodes |> List.filter (fun node -> ids.Contains node.Id)
                      Relations = []
                      PathLength = None }
                | GraphQuery.Neighborhood(entity, hops) ->
                    let rec collect frontier visited depth =
                        if depth >= hops then
                            visited
                        else
                            let neighbors =
                                frontier
                                |> Set.toList
                                |> List.collect (fun entity ->
                                    indexedValues outgoingIndex (owner, entity) |> Set.toList)
                                |> List.distinct
                                |> List.choose (fun relationId ->
                                    match relations.TryGetValue(relationKey owner relationId) with
                                    | true, relation -> Some relation
                                    | _ -> None)
                                |> List.collect (fun relation ->
                                    [ if frontier |> Set.contains relation.Subject then
                                          relation.Object
                                      if
                                          relation.Direction = GraphRelationDirection.Undirected
                                          && frontier |> Set.contains relation.Object
                                      then
                                          relation.Subject ])
                                |> Set.ofList
                                |> fun found -> Set.difference found visited

                            collect neighbors (Set.union visited neighbors) (depth + 1)

                    let ids = collect (Set.singleton entity) (Set.singleton entity) 0

                    { Nodes = graphNodes |> List.filter (fun node -> ids.Contains node.Id)
                      Relations =
                        graphRelations
                        |> List.filter (fun relation -> ids.Contains relation.Subject && ids.Contains relation.Object)
                      PathLength = None }

            Task.FromResult result

        let removeRelation owner (relation: GraphRelation) =
            if relations.TryRemove(relationKey owner relation.Id) |> fst then
                unindexRelation owner relation

        let clearOwner owner =
            ownedRelations owner |> Seq.toArray |> Array.iter (removeRelation owner)

            ownedNodes owner
            |> Seq.toArray
            |> Array.iter (fun node ->
                nodes.TryRemove(nodeKey owner node.Id) |> ignore
                unindexNode owner node)

        let removeNodeAsync scope nodeId =
            let owner = ownerKey scope

            match nodes.TryRemove(nodeKey owner nodeId) with
            | true, node -> unindexNode owner node
            | _ -> ()

            ownedRelations owner
            |> Seq.filter (fun relation -> relation.Subject = nodeId || relation.Object = nodeId)
            |> Seq.toArray
            |> Array.iter (removeRelation owner)

            Task.FromResult()

        let removeRelationAsync scope relationId =
            let owner = ownerKey scope

            match relations.TryGetValue(relationKey owner relationId) with
            | true, relation -> removeRelation owner relation
            | _ -> ()

            Task.FromResult()

        let mergeEntityAsync scope merge =
            GraphContracts.validateMerge merge
            let owner = ownerKey scope

            let sources =
                merge.SourceIds
                |> Set.toList
                |> List.map (fun id ->
                    match nodes.TryGetValue(nodeKey owner id) with
                    | true, node -> node
                    | _ -> invalidArg (nameof merge) $"Merge source entity '{id}' does not exist.")

            if
                sources
                |> List.exists (fun source -> source.EntityType <> merge.Target.EntityType)
            then
                invalidArg (nameof merge) "Merge sources and target must have the same entity type."

            requireIdentityAvailability owner merge.SourceIds merge.Target

            let merged =
                { merge.Target with
                    Aliases =
                        sources
                        |> List.fold
                            (fun aliases source -> Set.union aliases source.Aliases |> Set.add source.Id)
                            merge.Target.Aliases
                        |> Set.remove merge.Target.Id
                    Provenance =
                        (merge.Target.Provenance @ (sources |> List.collect _.Provenance))
                        |> List.distinct
                    CreatedAt =
                        sources
                        |> List.map _.CreatedAt
                        |> List.append [ merge.Target.CreatedAt ]
                        |> List.min }

            GraphContracts.validateNode merged

            let rewired =
                ownedRelations owner
                |> Seq.filter (fun relation ->
                    merge.SourceIds.Contains relation.Subject
                    || merge.SourceIds.Contains relation.Object)
                |> Seq.map (fun relation ->
                    { relation with
                        Subject =
                            if merge.SourceIds.Contains relation.Subject then
                                merged.Id
                            else
                                relation.Subject
                        Object =
                            if merge.SourceIds.Contains relation.Object then
                                merged.Id
                            else
                                relation.Object })
                |> Seq.toArray

            for source in sources do
                nodes.TryRemove(nodeKey owner source.Id) |> ignore
                unindexNode owner source

            storeNode owner merged
            rewired |> Array.iter (replaceRelation owner)
            Task.FromResult()

        let splitEntityAsync scope split =
            GraphContracts.validateSplit split
            let owner = ownerKey scope

            let source =
                match nodes.TryGetValue(nodeKey owner split.SourceId) with
                | true, node -> node
                | _ -> invalidArg (nameof split) $"Split source entity '{split.SourceId}' does not exist."

            for replacement in split.Replacements do
                requireIdentityAvailability owner (Set.singleton split.SourceId) replacement

                if
                    split.Replacements
                    |> List.exists (fun other ->
                        other.Id <> replacement.Id
                        && (other.Aliases.Contains replacement.Id
                            || replacement.Aliases.Contains other.Id
                            || not (Set.intersect other.Aliases replacement.Aliases |> Set.isEmpty)))
                then
                    invalidArg (nameof split) "Split replacement identities and aliases must be disjoint."

            let incident =
                ownedRelations owner
                |> Seq.filter (fun relation -> relation.Subject = split.SourceId || relation.Object = split.SourceId)
                |> Seq.map (fun relation -> relation.Id, relation)
                |> Map.ofSeq

            let rewriteIds = split.RelationRewrites |> List.map _.RelationId |> Set.ofList

            if rewriteIds <> (incident |> Map.keys |> Set.ofSeq) then
                invalidArg (nameof split) "Entity split must rewrite every incident relation exactly once."

            let rewired =
                split.RelationRewrites
                |> List.map (fun rewrite ->
                    let relation = incident.[rewrite.RelationId]
                    let replacesSubject = relation.Subject = split.SourceId
                    let replacesObject = relation.Object = split.SourceId

                    if
                        replacesSubject <> rewrite.SubjectId.IsSome
                        || replacesObject <> rewrite.ObjectId.IsSome
                    then
                        invalidArg
                            (nameof split)
                            $"Relation rewrite '{rewrite.RelationId}' does not match its source endpoints."

                    { relation with
                        Subject = rewrite.SubjectId |> Option.defaultValue relation.Subject
                        Object = rewrite.ObjectId |> Option.defaultValue relation.Object })

            nodes.TryRemove(nodeKey owner source.Id) |> ignore
            unindexNode owner source

            for replacement in split.Replacements do
                storeNode owner replacement

            rewired |> List.iter (replaceRelation owner)
            Task.FromResult()

        let snapshotAsync scope =
            let owner = ownerKey scope

            { Owner = AuthorizationScope.identity scope
              CreatedAt = DateTimeOffset.UtcNow
              Nodes = ownedNodes owner |> Seq.sortBy _.Id |> Seq.toList
              Relations = ownedRelations owner |> Seq.sortBy _.Id |> Seq.toList }
            |> Task.FromResult

        let replaceAsync scope snapshot =
            GraphContracts.validateSnapshot scope snapshot
            let owner = ownerKey scope
            clearOwner owner

            for node in snapshot.Nodes do
                storeNode owner node

            for relation in snapshot.Relations do
                relations.[relationKey owner relation.Id] <- relation
                indexRelation owner relation

            Task.FromResult()

        let getByTypeAsync scope entityType =
            let owner = ownerKey scope
            let ids = indexedValues entityTypeIndex (owner, entityType)

            ownedNodes owner
            |> Seq.filter (fun node -> ids.Contains node.Id)
            |> Seq.toList
            |> Task.FromResult

        let extractAsync scope text =
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
                                            Some(subject, pattern, object')))

                        let assertedAt = DateTimeOffset.UtcNow
                        let operationId = Guid.NewGuid().ToString("N")

                        let provenance =
                            { SourceVersionId = "inline-text"
                              ExtractionOperationId = operationId
                              EvidenceAddresses = [ "inline:0" ] }

                        let identities =
                            found
                            |> List.collect (fun (subject, _, object') -> [ subject; object' ])
                            |> List.distinct

                        Task.FromResult
                            { Nodes =
                                identities
                                |> List.map (fun identity ->
                                    { Id = identity
                                      EntityType = "extracted"
                                      Aliases = Set.empty
                                      Properties = Map.empty
                                      Provenance = [ provenance ]
                                      CreatedAt = assertedAt
                                      UpdatedAt = assertedAt })
                              Relations =
                                found
                                |> List.map (fun (subject, predicate, object') ->
                                    { Id =
                                        GraphContracts.relationId
                                            subject
                                            predicate
                                            object'
                                            provenance.SourceVersionId
                                            operationId
                                      Subject = subject
                                      Predicate = predicate
                                      Object = object'
                                      Direction = GraphRelationDirection.Directed
                                      Properties = Map.empty
                                      Validity = { ValidFrom = None; ValidTo = None }
                                      Confidence = 0.5
                                      Provenance = provenance
                                      AssertedAt = assertedAt }) }

                lock sync (fun () ->
                    for node in extracted.Nodes do
                        upsertNodeAsync scope node |> _.GetAwaiter().GetResult()

                    for relation in extracted.Relations do
                        addRelationAsync scope relation |> _.GetAwaiter().GetResult())

                return extracted
            }

        let delete scope predicate =
            let owner = ownerKey scope

            let nodeMatches =
                ownedNodes owner
                |> Seq.filter (fun node -> predicate (Choice1Of2 node))
                |> Seq.toArray

            let deletedNodeIds = nodeMatches |> Array.map (fun node -> node.Id) |> Set.ofArray

            let relationMatches =
                ownedRelations owner
                |> Seq.filter (fun relation ->
                    predicate (Choice2Of2 relation)
                    || deletedNodeIds.Contains relation.Subject
                    || deletedNodeIds.Contains relation.Object)
                |> Seq.toArray

            nodeMatches
            |> Array.iter (fun (node: GraphNode) ->
                nodes.TryRemove(nodeKey owner node.Id) |> ignore
                unindexNode owner node)

            relationMatches |> Array.iter (removeRelation owner)

            Task.FromResult(nodeMatches.Length + relationMatches.Length)

        let deleteOwnerAsync scope =
            task {
                let! count = delete scope (fun _ -> true)
                return Ok count
            }

        let deleteExpiredAsync scope before =
            task {
                let! count =
                    delete scope (function
                        | Choice1Of2 node -> node.CreatedAt < before
                        | Choice2Of2 relation -> relation.AssertedAt < before)

                return Ok count
            }

        { UpsertNodeAsync = fun scope node -> lock sync (fun () -> upsertNodeAsync scope node)
          AddRelationAsync = fun scope relation -> lock sync (fun () -> addRelationAsync scope relation)
          QueryAsync = fun scope query -> lock sync (fun () -> queryAsync scope query)
          RemoveNodeAsync = fun scope nodeId -> lock sync (fun () -> removeNodeAsync scope nodeId)
          RemoveRelationAsync = fun scope relationId -> lock sync (fun () -> removeRelationAsync scope relationId)
          MergeEntityAsync = fun scope merge -> lock sync (fun () -> mergeEntityAsync scope merge)
          SplitEntityAsync = fun scope split -> lock sync (fun () -> splitEntityAsync scope split)
          SnapshotAsync = fun scope -> lock sync (fun () -> snapshotAsync scope)
          ReplaceAsync = fun scope snapshot -> lock sync (fun () -> replaceAsync scope snapshot)
          GetByTypeAsync = fun scope entityType -> lock sync (fun () -> getByTypeAsync scope entityType)
          ExtractAsync = extractAsync
          DeleteOwnerAsync = fun scope -> lock sync (fun () -> deleteOwnerAsync scope)
          DeleteExpiredAsync = fun scope before -> lock sync (fun () -> deleteExpiredAsync scope before) }
