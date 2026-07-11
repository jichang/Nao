namespace Nao.Agents

open System
open System.Threading.Tasks

/// A relationship between two entities in the knowledge graph
type GraphRelation =
    { Subject: string
      Predicate: string
      Object: string
      Confidence: float
      Source: string option
      Timestamp: DateTimeOffset
      Metadata: Map<string, string> }

/// A node in the knowledge graph with typed properties
type GraphNode =
    { Id: string
      EntityType: string
      Properties: Map<string, string>
      CreatedAt: DateTimeOffset
      LastAccessed: DateTimeOffset
      AccessCount: int }

/// Query for traversing the knowledge graph
[<RequireQualifiedAccess>]
type GraphQuery =
    /// Find all relations where entity is subject or object
    | ByEntity of entity: string
    /// Find all relations with a given predicate
    | ByPredicate of predicate: string
    /// Find paths between two entities (max hops)
    | Path of from': string * to': string * maxHops: int
    /// Find entities matching property filters
    | ByProperties of filters: (string * string) list
    /// Find related entities within N hops
    | Neighborhood of entity: string * hops: int

/// Result of a graph traversal
type GraphTraversalResult =
    { Nodes: GraphNode list
      Relations: GraphRelation list
      PathLength: int option }

/// Interface for graph-based memory (knowledge graph)
type IGraphMemory =
    /// Add or update a node
    abstract member UpsertNodeAsync: GraphNode -> Task<unit>
    /// Add a relation between entities
    abstract member AddRelationAsync: GraphRelation -> Task<unit>
    /// Query the graph
    abstract member QueryAsync: GraphQuery -> Task<GraphTraversalResult>
    /// Remove a node and all its relations
    abstract member RemoveNodeAsync: string -> Task<unit>
    /// Remove a specific relation
    abstract member RemoveRelationAsync: subject: string -> predicate: string -> object': string -> Task<unit>
    /// Get all nodes of a given type
    abstract member GetByTypeAsync: entityType: string -> Task<GraphNode list>
    /// Extract and store relations from text (via LLM or pattern matching)
    abstract member ExtractRelationsAsync: text: string -> Task<GraphRelation list>
