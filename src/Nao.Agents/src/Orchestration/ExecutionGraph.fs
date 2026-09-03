namespace Nao.Agents

open System
open System.Threading.Tasks

/// Stable identity of a node in an agent execution graph.
[<Struct; StructuralEquality; StructuralComparison>]
type GraphNodeId = GraphNodeId of string

[<RequireQualifiedAccess>]
module GraphNodeId =
    /// Create a non-empty graph node identifier.
    let create (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "A graph node identifier cannot be empty."

        GraphNodeId value

    /// Return the text representation of a graph node identifier.
    let value (GraphNodeId value) = value

/// Information available when deciding whether an outgoing graph edge should be followed.
type GraphStep =
    { NodeId: GraphNodeId
      Input: string
      Output: string
      Step: int }

/// An executable graph node backed by an agent.
type AgentGraphNode =
    { Id: GraphNodeId; Agent: Agent }

/// A conditional transition between graph nodes. Outgoing edges are evaluated in declaration
/// order; the first matching edge is selected. No matching edge means successful completion.
type GraphEdge =
    { From: GraphNodeId
      Target: GraphNodeId
      Condition: GraphStep -> bool
      Transform: GraphStep -> string }

/// A bounded directed execution graph. Cycles are supported but remain bounded by `MaxSteps`.
type ExecutionGraph =
    { Entry: GraphNodeId; Nodes: AgentGraphNode list; Edges: GraphEdge list; MaxSteps: int }

/// Successful graph output and the ordered execution path that produced it.
type GraphExecutionResult =
    { Output: string
      Steps: GraphStep list }

/// A graph definition or execution failure.
type GraphExecutionError =
    | InvalidGraph of problems: string list
    | StepLimitReached of maxSteps: int * steps: GraphStep list

[<RequireQualifiedAccess>]
module ExecutionGraph =

    /// Create an unconditional edge that forwards the source node's output.
    let edge (source: GraphNodeId) (target: GraphNodeId) =
        { From = source
          Target = target
          Condition = fun _ -> true
          Transform = fun step -> step.Output }

    /// Validate graph identity, references, entry point, and execution budget.
    let validate (graph: ExecutionGraph) =
        let nodeIds = graph.Nodes |> List.map _.Id
        let known = nodeIds |> Set.ofList

        let duplicates =
            nodeIds
            |> List.countBy id
            |> List.choose (fun (nodeId, count) ->
                if count > 1 then
                    Some(sprintf "Node '%s' is declared more than once." (GraphNodeId.value nodeId))
                else
                    None)

        let entryProblems =
            if Set.contains graph.Entry known then []
            else [ sprintf "Entry node '%s' does not exist." (GraphNodeId.value graph.Entry) ]

        let edgeProblems =
            graph.Edges
            |> List.collect (fun edge ->
                [ if not (Set.contains edge.From known) then
                      sprintf "Edge source '%s' does not exist." (GraphNodeId.value edge.From)
                  if not (Set.contains edge.Target known) then
                      sprintf "Edge target '%s' does not exist." (GraphNodeId.value edge.Target) ])

        let budgetProblems =
            if graph.MaxSteps < 1 then [ "MaxSteps must be at least one." ] else []

        match duplicates @ entryProblems @ edgeProblems @ budgetProblems with
        | [] -> Ok graph
        | problems -> Error(InvalidGraph problems)

    /// Execute a graph. Nodes receive the same run-scoped agent context, while edges explicitly
    /// control routing and transformation of the transport value between nodes.
    let runAsync
        (context: AgentContext)
        (input: string)
        (graph: ExecutionGraph)
        : Task<Result<GraphExecutionResult, GraphExecutionError>> =
        task {
            match validate graph with
            | Error error -> return Error error
            | Ok validGraph ->
                let nodes = validGraph.Nodes |> Seq.map (fun node -> node.Id, node) |> Map.ofSeq

                let definition =
                    { MaxIterations = validGraph.MaxSteps
                      StepAsync =
                        fun iteration (nodeId, nodeInput, completedSteps) ->
                            task {
                                let node = nodes.[nodeId]
                                let! output = Agent.runAsync context nodeInput node.Agent

                                let step =
                                    { NodeId = nodeId
                                      Input = nodeInput
                                      Output = output
                                      Step = iteration }

                                let nextEdge =
                                    validGraph.Edges
                                    |> List.tryFind (fun edge -> edge.From = nodeId && edge.Condition step)

                                match nextEdge with
                                | Some edge ->
                                    return Continue(edge.Target, edge.Transform step, step :: completedSteps)
                                | None ->
                                    return
                                        Complete
                                            { Output = output
                                              Steps = List.rev (step :: completedSteps) }
                            }
                      }

                let! outcome = Loop.runAsync definition (validGraph.Entry, input, [])

                return
                    match outcome with
                    | Completed(result, _) -> Ok result
                    | IterationLimitReached((_, _, completedSteps), _) ->
                        Error(StepLimitReached(validGraph.MaxSteps, List.rev completedSteps))
        }

    /// Build a linear graph from a non-empty ordered list of agents.
    let linear (agents: Agent list) =
        let nodes =
            agents
            |> List.mapi (fun index agent ->
                { Id = GraphNodeId.create (sprintf "stage-%d-%s" index agent.Metadata.Id)
                  Agent = agent })

        match nodes with
        | [] -> None
        | entry :: _ ->
            let edges =
                nodes
                |> List.pairwise
                |> List.map (fun (source, target) -> edge source.Id target.Id)

            Some
                { Entry = entry.Id
                  Nodes = nodes
                  Edges = edges
                  MaxSteps = nodes.Length }

    /// Adapt an execution graph to `IAgent`, preserving the harness as the outer execution,
    /// governance, lifecycle, and verification boundary.
    let asAgent
        (id: string)
        (name: string)
        (description: string)
        (priority: int)
        (responsibilities: string list)
        (contract: AgentContract)
        (graph: ExecutionGraph)
        : Agent =
        let execute context input =
            task {
                let! result = runAsync context input graph

                return
                    match result with
                    | Ok execution -> execution.Output
                    | Error(InvalidGraph problems) ->
                        raise (InvalidOperationException(String.concat " " problems))
                    | Error(StepLimitReached(maxSteps, _)) ->
                        raise (InvalidOperationException(sprintf "Execution graph exceeded its limit of %d steps." maxSteps))
            }

        Agent.createContextual id name description priority responsibilities contract execute
