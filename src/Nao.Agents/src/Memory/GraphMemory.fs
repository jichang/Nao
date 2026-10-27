namespace Nao.Agents

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type GraphRelationDirection =
    | Directed
    | Undirected

type GraphTemporalValidity =
    { ValidFrom: DateTimeOffset option
      ValidTo: DateTimeOffset option }

type GraphProvenance =
    { SourceVersionId: string
      ExtractionOperationId: string
      EvidenceAddresses: string list }

/// A node in a property graph. Aliases never replace the stable entity identity.
type GraphNode =
    { Id: string
      EntityType: string
      Aliases: Set<string>
      Properties: Map<string, string>
      Provenance: GraphProvenance list
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset }

/// One independently removable assertion between two stable entity identities.
type GraphRelation =
    { Id: string
      Subject: string
      Predicate: string
      Object: string
      Direction: GraphRelationDirection
      Properties: Map<string, string>
      Validity: GraphTemporalValidity
      Confidence: float
      Provenance: GraphProvenance
      AssertedAt: DateTimeOffset }

type GraphExtraction =
    { Nodes: GraphNode list
      Relations: GraphRelation list }

type GraphSnapshot =
    { Owner: AuthorizationIdentity
      CreatedAt: DateTimeOffset
      Nodes: GraphNode list
      Relations: GraphRelation list }

type GraphRelationEndpointRewrite =
    { RelationId: string
      SubjectId: string option
      ObjectId: string option }

type GraphEntityMerge =
    { SourceIds: Set<string>
      Target: GraphNode }

type GraphEntitySplit =
    { SourceId: string
      Replacements: GraphNode list
      RelationRewrites: GraphRelationEndpointRewrite list }

[<RequireQualifiedAccess>]
module GraphContracts =
    let private canonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"{name} must be canonical and non-blank."

    let validateProvenance (provenance: GraphProvenance) =
        canonical (nameof provenance.SourceVersionId) provenance.SourceVersionId
        canonical (nameof provenance.ExtractionOperationId) provenance.ExtractionOperationId

        if List.isEmpty provenance.EvidenceAddresses then
            invalidArg (nameof provenance.EvidenceAddresses) "Graph provenance requires source evidence."

        provenance.EvidenceAddresses
        |> List.iter (canonical (nameof provenance.EvidenceAddresses))

    let validateNode (node: GraphNode) =
        canonical (nameof node.Id) node.Id
        canonical (nameof node.EntityType) node.EntityType
        node.Aliases |> Set.iter (canonical (nameof node.Aliases))

        if node.Aliases.Contains node.Id then
            invalidArg (nameof node.Aliases) "An entity identity cannot also be its alias."

        if node.UpdatedAt < node.CreatedAt then
            invalidArg (nameof node.UpdatedAt) "Entity update time cannot precede creation."

        node.Provenance |> List.iter validateProvenance

    let validateMerge merge =
        validateNode merge.Target

        if Set.isEmpty merge.SourceIds then
            invalidArg (nameof merge.SourceIds) "Entity merge requires at least one source identity."

        merge.SourceIds |> Set.iter (canonical (nameof merge.SourceIds))

        if not (merge.SourceIds.Contains merge.Target.Id) then
            let missingAliases = Set.difference merge.SourceIds merge.Target.Aliases

            if not (Set.isEmpty missingAliases) then
                invalidArg (nameof merge.Target) "A new merge target must retain every source identity as an alias."

    let validateSplit split =
        canonical (nameof split.SourceId) split.SourceId

        if split.Replacements.Length < 2 then
            invalidArg (nameof split.Replacements) "Entity split requires at least two replacement entities."

        split.Replacements |> List.iter validateNode

        let replacementIds = split.Replacements |> List.map _.Id

        if replacementIds |> List.distinct |> List.length <> replacementIds.Length then
            invalidArg (nameof split.Replacements) "Entity split replacement identities must be unique."

        if replacementIds |> List.contains split.SourceId then
            invalidArg (nameof split.Replacements) "Entity split replacements cannot reuse the source identity."

        let rewriteIds = split.RelationRewrites |> List.map _.RelationId

        if rewriteIds |> List.distinct |> List.length <> rewriteIds.Length then
            invalidArg (nameof split.RelationRewrites) "Entity split can rewrite each relation only once."

        let replacementSet = Set.ofList replacementIds

        split.RelationRewrites
        |> List.iter (fun rewrite ->
            canonical (nameof rewrite.RelationId) rewrite.RelationId

            if rewrite.SubjectId.IsNone && rewrite.ObjectId.IsNone then
                invalidArg (nameof split.RelationRewrites) "A relation rewrite must replace at least one endpoint."

            rewrite.SubjectId
            |> Option.iter (fun id ->
                if not (replacementSet.Contains id) then
                    invalidArg (nameof split.RelationRewrites) $"Unknown split replacement '{id}'.")

            rewrite.ObjectId
            |> Option.iter (fun id ->
                if not (replacementSet.Contains id) then
                    invalidArg (nameof split.RelationRewrites) $"Unknown split replacement '{id}'."))

    let validateRelation (relation: GraphRelation) =
        canonical (nameof relation.Id) relation.Id
        canonical (nameof relation.Subject) relation.Subject
        canonical (nameof relation.Predicate) relation.Predicate
        canonical (nameof relation.Object) relation.Object

        if
            relation.Validity.ValidFrom.IsSome
            && relation.Validity.ValidTo.IsSome
            && relation.Validity.ValidTo.Value <= relation.Validity.ValidFrom.Value
        then
            invalidArg (nameof relation.Validity) "Relation validity end must follow its start."

        if
            Double.IsNaN relation.Confidence
            || Double.IsInfinity relation.Confidence
            || relation.Confidence < 0.0
            || relation.Confidence > 1.0
        then
            invalidArg (nameof relation.Confidence) "Relation confidence must be finite and between zero and one."

        validateProvenance relation.Provenance

    let validateSnapshot authorization snapshot =
        if snapshot.Owner <> AuthorizationScope.identity authorization then
            raise (UnauthorizedAccessException "Graph snapshot owner does not match the authenticated scope.")

        snapshot.Nodes |> List.iter validateNode
        snapshot.Relations |> List.iter validateRelation

        let nodeIds = snapshot.Nodes |> List.map _.Id

        if nodeIds |> List.distinct |> List.length <> nodeIds.Length then
            invalidArg (nameof snapshot) "Graph snapshot contains duplicate entity identities."

        let relationIds = snapshot.Relations |> List.map _.Id

        if relationIds |> List.distinct |> List.length <> relationIds.Length then
            invalidArg (nameof snapshot) "Graph snapshot contains duplicate relation identities."

        let identities = Set.ofList nodeIds

        snapshot.Relations
        |> List.iter (fun relation ->
            if
                not (identities.Contains relation.Subject)
                || not (identities.Contains relation.Object)
            then
                invalidArg (nameof snapshot) $"Relation '{relation.Id}' has an endpoint outside the snapshot.")

        let aliases = snapshot.Nodes |> List.collect (_.Aliases >> Set.toList)

        if
            aliases |> List.distinct |> List.length <> aliases.Length
            || aliases |> List.exists identities.Contains
        then
            invalidArg (nameof snapshot) "Graph snapshot contains ambiguous entity aliases."

    let relationId subject predicate object' sourceVersionId extractionOperationId =
        [ subject; predicate; object'; sourceVersionId; extractionOperationId ]
        |> List.iter (canonical "Relation identity component")

        String.concat "\n" [ subject; predicate; object'; sourceVersionId; extractionOperationId ]
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

/// Query for traversing one authorization-partitioned property graph.
[<RequireQualifiedAccess>]
type GraphQuery =
    | ByEntity of entity: string
    | ByAlias of alias: string
    | ByPredicate of predicate: string
    | Path of from': string * to': string * maxHops: int
    | ByProperties of filters: (string * string) list
    | Neighborhood of entity: string * hops: int

type GraphTraversalResult =
    { Nodes: GraphNode list
      Relations: GraphRelation list
      PathLength: int option }

type GraphMemory =
    { UpsertNodeAsync: AuthorizationScope -> GraphNode -> Task<unit>
      AddRelationAsync: AuthorizationScope -> GraphRelation -> Task<unit>
      QueryAsync: AuthorizationScope -> GraphQuery -> Task<GraphTraversalResult>
      RemoveNodeAsync: AuthorizationScope -> string -> Task<unit>
      RemoveRelationAsync: AuthorizationScope -> string -> Task<unit>
      MergeEntityAsync: AuthorizationScope -> GraphEntityMerge -> Task<unit>
      SplitEntityAsync: AuthorizationScope -> GraphEntitySplit -> Task<unit>
      SnapshotAsync: AuthorizationScope -> Task<GraphSnapshot>
      ReplaceAsync: AuthorizationScope -> GraphSnapshot -> Task<unit>
      GetByTypeAsync: AuthorizationScope -> string -> Task<GraphNode list>
      ExtractAsync: AuthorizationScope -> string -> Task<GraphExtraction>
      DeleteOwnerAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: AuthorizationScope -> DateTimeOffset -> Task<Result<int, PlatformFailure>> }
