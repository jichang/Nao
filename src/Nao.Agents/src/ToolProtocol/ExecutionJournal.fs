namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Agents

/// A record of a single tool execution (immutable, for journaling)
type ExecutionRecord =
    { /// Which tool was executed
      ToolName: string
      /// The input provided
      Input: string
      /// The output produced
      Output: string
      /// Content type of the output
      ContentMeta: ContentMeta
      /// When it was executed
      ExecutedAt: DateTimeOffset
      /// Whether the execution has been reverted
      Reverted: bool
      /// Additional metadata
      Metadata: Map<string, string> }

/// Journal that tracks tool executions for revert/audit support.
/// Framework provides the flow; consumers extend with persistence if needed.
type IExecutionJournal =
    /// Record a tool execution
    abstract member RecordAsync: ExecutionRecord -> Task
    /// Get all recorded executions (most recent first)
    abstract member GetHistoryAsync: unit -> Task<ExecutionRecord list>
    /// Get executions that can be reverted (not yet reverted, tool supports revert)
    abstract member GetRevertibleAsync: unit -> Task<ExecutionRecord list>
    /// Mark an execution as reverted
    abstract member MarkRevertedAsync: ExecutionRecord -> Task

/// Orchestrates reverting tool executions in reverse order
module ExecutionJournal =

    /// Revert all revertible executions in reverse chronological order.
    /// Returns list of (toolName, result) for each attempted revert.
    let revertAllAsync (journal: IExecutionJournal) (tools: Tool list) : Task<(string * Result<unit, string>) list> =
        task {
            let! revertible = journal.GetRevertibleAsync()
            let results = System.Collections.Generic.List<string * Result<unit, string>>()

            for record in revertible do
                let tool = tools |> List.tryFind (fun t -> t.Name = record.ToolName)
                match tool with
                | Some t when t.CanRevert ->
                    let ctx: RevertContext =
                        { Input = record.Input
                          Output = record.Output
                          ExecutedAt = record.ExecutedAt
                          Metadata = record.Metadata }
                    let! result = t.Revert.Value ctx
                    match result with
                    | Ok () -> do! journal.MarkRevertedAsync record
                    | _ -> ()
                    results.Add(record.ToolName, result)
                | _ ->
                    results.Add(record.ToolName, Error "Tool not found or does not support revert")

            return results |> Seq.toList
        }

    /// Revert the most recent execution only
    let revertLastAsync (journal: IExecutionJournal) (tools: Tool list) : Task<Result<unit, string>> =
        task {
            let! revertible = journal.GetRevertibleAsync()
            match revertible with
            | [] -> return Error "No revertible executions"
            | record :: _ ->
                let tool = tools |> List.tryFind (fun t -> t.Name = record.ToolName)
                match tool with
                | Some t when t.CanRevert ->
                    let ctx: RevertContext =
                        { Input = record.Input
                          Output = record.Output
                          ExecutedAt = record.ExecutedAt
                          Metadata = record.Metadata }
                    let! result = t.Revert.Value ctx
                    match result with
                    | Ok () -> do! journal.MarkRevertedAsync record
                    | _ -> ()
                    return result
                | _ ->
                    return Error (sprintf "Tool '%s' does not support revert" record.ToolName)
        }
